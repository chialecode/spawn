using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Host.Services;

/// <summary>
/// Lightweight entry for a quick-launch recent game item in the system tray context menu.
/// </summary>
public sealed record TrayRecentGameEntry(string GameId, string Title);

/// <summary>
/// Live runtime status summary displayed in the system tray tooltip and context menu.
/// </summary>
public sealed record TrayRuntimeStatusSnapshot
{
    public string? RunningGameId { get; init; }
    public string? RunningGameTitle { get; init; }
    public PlaySessionActivityState RunningGameState { get; init; } = PlaySessionActivityState.Stopped;
    public string? ActiveTrainerName { get; init; }
    public AntiCheatGuardPhase AntiCheatPhase { get; init; } = AntiCheatGuardPhase.IdleSafe;
    public double AntiCheatCooldownSeconds { get; init; }
    public IReadOnlyList<TrayRecentGameEntry> RecentGames { get; init; } = [];
}

/// <summary>
/// Declarative descriptor for a native Win32 system tray context menu item (enables headless unit testing).
/// </summary>
public sealed record TrayMenuItemDescriptor(
    uint CommandId,
    string Text,
    bool IsSeparator = false,
    bool IsDisabled = false,
    bool IsDefault = false,
    string? Tag = null);

/// <summary>
/// Pure Win32 <c>Shell_NotifyIconW</c> + <c>SkiaSharp</c> system tray integration for <c>Spawn.Host</c>.
/// Zero <c>System.Windows.Forms</c> dependency and zero sensitive Win32 imports.
/// Automatically renders an amber <c>DEV</c> badge for <c>dev</c>/<c>source</c> channels and recovers
/// automatically on Explorer <c>TaskbarCreated</c> broadcasts.
/// </summary>
public sealed partial class TrayIconService : IDisposable
{
    private const int WmAppTrayCallback = 0x8000 + 0x42;
    private const int WmNull = 0x0000;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int NinBalloonUserClick = 0x0405;

    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;
    private const uint NiifNoSound = 0x00000010;

    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmBottomAlign = 0x0020;

    public const uint CmdOpenWindow = 1001;
    public const uint CmdExitApp = 1002;
    public const uint CmdTestToast = 1003;
    public const uint CmdRestartForUpdate = 1004;
    public const uint CmdOpenUpdateSettings = 1005;
    public const uint CmdStopActiveTrainer = 1006;
    public const uint CmdStopRunningGame = 1007;
    public const uint CmdStatusHeaderBase = 1900;
    public const uint CmdQuickLaunchBase = 2000;
    private const int MaxQuickLaunchRecentGames = 3;

    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const int IdiApplicationResourceId = 32512;
    private const int PrimaryIconResourceId = 1;

    private static readonly Guid TrayItemGuid = Guid.Empty;

    private readonly bool _isHeadless;
    private readonly bool _isDevChannel;
    private readonly string _productTitle;

    private HwndSource? _messageWindow;
    private nint _hIcon;
    private uint _taskbarCreatedMessage;
    private string _currentTooltip;
    private bool _iconAdded;
    private int _disposed;
    private List<TrayMenuItemDescriptor> _lastBuiltMenu = [];

    public bool IsActive => _iconAdded && _disposed == 0;
    public bool IsDevChannel => _isDevChannel;
    public string CurrentTooltip => _currentTooltip;
    public TrayRuntimeStatusSnapshot RuntimeStatus { get; private set; } = new();
    public string? ReadyUpdateVersion { get; private set; }
    public string? LastNotificationTitle { get; private set; }
    public string? LastNotificationMessage { get; private set; }
    public int NotificationCount { get; private set; }

    public event Action? OpenMainWindowRequested;
    public event Action? TestToastRequested;
    public event Action? RestartForUpdateRequested;
    public event Action? OpenUpdateSettingsRequested;
    public event Action? StopActiveTrainerRequested;
    public event Action<string>? StopRunningGameRequested;
    public event Action<string>? QuickLaunchGameRequested;
    public event Action? ExitApplicationRequested;

    public TrayIconService()
        : this(
            isDevChannelOverride: SpawnBuildInfo.IsDevOrSourceChannel,
            forceHeadless: StartupWindowPolicy.IsHeadlessTestEnvironment(SpawnBuildInfo.IsReleaseChannel),
            productTitleOverride: SpawnBuildInfo.DisplayProductName)
    {
    }

    public TrayIconService(
        bool isDevChannelOverride,
        bool forceHeadless = true,
        string? productTitleOverride = null)
    {
        _isDevChannel = isDevChannelOverride;
        _isHeadless = forceHeadless;
        _productTitle = !string.IsNullOrWhiteSpace(productTitleOverride)
            ? productTitleOverride.Trim()
            : (isDevChannelOverride ? "SPAWN Dev" : "SPAWN");
        _currentTooltip = $"{_productTitle} · 运行就绪";
    }

    public void Initialize()
    {
        if (_isHeadless || _iconAdded || _disposed != 0)
        {
            return;
        }

        try
        {
            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

            // Use a hidden top-level zero-size tool window (NOT HWND_MESSAGE) so we receive
            // Explorer's broadcast TaskbarCreated message and auto-restore the tray icon after Explorer restarts,
            // while remaining 100% invisible (no WS_VISIBLE, no taskbar button, no focus).
            var parameters = new HwndSourceParameters("SpawnTrayMessageSink")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
                ParentWindow = nint.Zero,
            };

            _messageWindow = new HwndSource(parameters);
            _messageWindow.AddHook(WndProc);

            var (iconWidth, iconHeight) = GetSystemSmallIconSize();
            _hIcon = LoadOrRenderTrayHIcon(_isDevChannel, iconWidth, iconHeight);
            if (_hIcon == nint.Zero)
            {
                CleanupFailedInitialization();
                return;
            }

            var data = CreateNotifyIconData(_messageWindow.Handle, _hIcon, _currentTooltip);
            _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
            if (!_iconAdded)
            {
                CleanupFailedInitialization();
            }
        }
        catch
        {
            // Gracefully degrade on headless / non-interactive desktop sessions (App falls back to showing MainWindow).
            CleanupFailedInitialization();
        }
    }

    private void CleanupFailedInitialization()
    {
        _iconAdded = false;

        if (_hIcon != nint.Zero)
        {
            _ = DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }

        if (_messageWindow is not null)
        {
            try
            {
                _messageWindow.RemoveHook(WndProc);
                _messageWindow.Dispose();
            }
            catch
            {
                // Ignore cleanup failure.
            }

            _messageWindow = null;
        }
    }

    public void UpdateToolTip(string statusSuffix)
    {
        _currentTooltip = string.IsNullOrWhiteSpace(statusSuffix)
            ? _productTitle
            : $"{_productTitle} · {statusSuffix}";

        if (!_iconAdded || _messageWindow is null || _hIcon == nint.Zero || _disposed != 0)
        {
            return;
        }

        var data = CreateNotifyIconData(_messageWindow.Handle, _hIcon, _currentTooltip);
        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    public void SetReadyUpdateVersion(string? version)
    {
        string? normalized = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        if (string.Equals(ReadyUpdateVersion, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ReadyUpdateVersion = normalized;
        UpdateToolTip(BuildDynamicTooltipSuffix());
    }

    /// <summary>
    /// Updates the live runtime summary (running game, active trainer, anti-cheat cooldown, and recent games)
    /// reflected in both the tray tooltip and the right-click context menu.
    /// </summary>
    public void UpdateRuntimeStatus(TrayRuntimeStatusSnapshot? snapshot)
    {
        RuntimeStatus = snapshot ?? new TrayRuntimeStatusSnapshot();
        UpdateToolTip(BuildDynamicTooltipSuffix());
    }

    private string BuildDynamicTooltipSuffix()
    {
        TrayRuntimeStatusSnapshot status = RuntimeStatus;
        if (status.AntiCheatPhase == AntiCheatGuardPhase.ResidualGuardWatch)
        {
            return "反作弊守护进程监测中";
        }

        if (status.AntiCheatPhase == AntiCheatGuardPhase.CooldownLocked)
        {
            int secs = (int)Math.Ceiling(Math.Max(0.0, status.AntiCheatCooldownSeconds));
            return secs > 0 ? $"反作弊冷却中 ({secs}s)" : "反作弊冷却中";
        }

        bool hasRunningGame = !string.IsNullOrWhiteSpace(status.RunningGameTitle)
                              && status.RunningGameState != PlaySessionActivityState.Stopped;
        bool hasActiveTrainer = !string.IsNullOrWhiteSpace(status.ActiveTrainerName);

        if (hasRunningGame && hasActiveTrainer)
        {
            return $"运行中：{status.RunningGameTitle} · 修改器运行中";
        }

        if (hasRunningGame)
        {
            return $"运行中：{status.RunningGameTitle}";
        }

        if (hasActiveTrainer)
        {
            return $"修改器运行中：{status.ActiveTrainerName}";
        }

        if (!string.IsNullOrWhiteSpace(ReadyUpdateVersion))
        {
            return $"新版本 v{ReadyUpdateVersion} 已就绪，重启更新";
        }

        return "运行就绪";
    }

    public void ShowNotification(string title, string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        LastNotificationTitle = title;
        LastNotificationMessage = message;
        NotificationCount++;

        if (_isHeadless || !_iconAdded || _messageWindow is null || _hIcon == nint.Zero || _disposed != 0)
        {
            return;
        }

        var data = CreateNotifyIconDataWithInfo(
            _messageWindow.Handle,
            _hIcon,
            _currentTooltip,
            title ?? _productTitle,
            message ?? string.Empty,
            isError);
        _ = Shell_NotifyIconW(NimModify, ref data);
    }

    public static BitmapImage? CreateWindowIconImage(bool isDevChannel)
    {
        try
        {
            byte[] png = LoadOrRenderBrandIconPngBytes(isDevChannel, sizePx: 128);
            using var ms = new MemoryStream(png, writable: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static (int Width, int Height) NormalizeSmallIconSize(int rawWidth, int rawHeight)
    {
        int width = rawWidth > 0 ? Math.Clamp(rawWidth, 16, 256) : 16;
        int height = rawHeight > 0 ? Math.Clamp(rawHeight, 16, 256) : width;
        return (width, height);
    }

    public static (int Width, int Height) GetSystemSmallIconSize() =>
        NormalizeSmallIconSize(GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon));

    public static string? ResolveBrandIconIcoPath(bool isDevChannel)
    {
        string icoFileName = isDevChannel ? "icon-dev.ico" : "icon.ico";
        string? discovered = SpawnPaths.DiscoverLocalFileUpwards(Path.Combine("assets", icoFileName));
        return !string.IsNullOrWhiteSpace(discovered) && File.Exists(discovered) ? discovered : null;
    }

    /// <summary>
    /// Loads a DPI-matched small tray <c>HICON</c> at <c>SM_CXSMICON x SM_CYSMICON</c> from the pre-averaged
    /// multi-size <c>assets/icon[-dev].ico</c> (or embedded Win32 PE icon resource), falling back to
    /// <see cref="BrandIconGeometry"/> SkiaSharp rendering at the exact target pixel size.
    /// </summary>
    public static nint LoadOrRenderTrayHIcon(
        bool isDevChannel,
        int widthPx,
        int heightPx,
        string? icoPathOverride = null,
        bool allowEmbeddedModuleIcon = true)
    {
        var (width, height) = NormalizeSmallIconSize(widthPx, heightPx);

        string? icoPath = icoPathOverride is null ? ResolveBrandIconIcoPath(isDevChannel) : icoPathOverride;
        if (!string.IsNullOrWhiteSpace(icoPath) && File.Exists(icoPath))
        {
            nint fromFile = LoadImageFromFileW(nint.Zero, icoPath, ImageIcon, width, height, LrLoadFromFile);
            if (fromFile != nint.Zero)
            {
                return fromFile;
            }
        }

        if (allowEmbeddedModuleIcon && CanUseCurrentProcessEmbeddedIcon(isDevChannel))
        {
            nint hModule = GetModuleHandleW(null);
            if (hModule != nint.Zero)
            {
                nint fromPe = LoadImageFromResourceIdW(hModule, IdiApplicationResourceId, ImageIcon, width, height, 0);
                if (fromPe != nint.Zero)
                {
                    return fromPe;
                }

                fromPe = LoadImageFromResourceIdW(hModule, PrimaryIconResourceId, ImageIcon, width, height, 0);
                if (fromPe != nint.Zero)
                {
                    return fromPe;
                }
            }
        }

        byte[] fallbackPng = RenderBrandIconPngBytes(isDevChannel, sizePx: Math.Max(width, height));
        return CreateHIconFromPngBytes(fallbackPng, width, height);
    }

    public static bool DestroyTrayHIcon(nint hIcon) =>
        hIcon != nint.Zero && DestroyIcon(hIcon);

    private static bool CanUseCurrentProcessEmbeddedIcon(bool requestedDevChannel)
    {
        if (requestedDevChannel != SpawnBuildInfo.IsDevOrSourceChannel)
        {
            return false;
        }

        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        string exeStem = Path.GetFileNameWithoutExtension(processPath);
        return exeStem.StartsWith("spawn", StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] LoadOrRenderBrandIconPngBytes(bool isDevChannel, int sizePx = 64)
    {
        string assetFileName = isDevChannel ? "icon-dev.png" : "icon.png";
        string? discovered = Spawn.Core.SpawnPaths.DiscoverLocalFileUpwards(Path.Combine("assets", assetFileName));
        if (!string.IsNullOrWhiteSpace(discovered) && File.Exists(discovered))
        {
            try
            {
                return File.ReadAllBytes(discovered);
            }
            catch
            {
                // Fall back to SkiaSharp vector synthesis below.
            }
        }

        return RenderBrandIconPngBytes(isDevChannel, sizePx);
    }

    /// <summary>
    /// Renders the flat SPAWN brand mark (see <see cref="BrandIconGeometry"/>) used when <c>assets/icon*.png</c> is not
    /// shipped next to the executable: dark-gray rounded square, white geometric <c>S</c>, optional amber <c>DEV</c> badge.
    /// </summary>
    public static byte[] RenderBrandIconPngBytes(bool isDevChannel, int sizePx = 64)
    {
        int s = Math.Clamp(sizePx, 16, 1024);
        var info = new SKImageInfo(s, s, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        BrandIconGeometry.Draw(canvas, s, isDevChannel);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage && _hIcon != nint.Zero && _disposed == 0)
        {
            var data = CreateNotifyIconData(hwnd, _hIcon, _currentTooltip);
            _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
            handled = true;
            return nint.Zero;
        }

        if (msg == WmAppTrayCallback)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WmLButtonDblClk or WmLButtonUp)
            {
                handled = true;
                OpenMainWindowRequested?.Invoke();
            }
            else if (mouseMsg is WmRButtonUp or WmContextMenu)
            {
                handled = true;
                ShowNativeContextMenu(hwnd);
            }
            else if (mouseMsg == NinBalloonUserClick)
            {
                handled = true;
                if (!_isDevChannel && !string.IsNullOrWhiteSpace(ReadyUpdateVersion))
                {
                    OpenUpdateSettingsRequested?.Invoke();
                }
                else
                {
                    OpenMainWindowRequested?.Invoke();
                }
            }
        }

        return nint.Zero;
    }

    /// <summary>
    /// Builds the ordered context menu items for the current channel and runtime status snapshot.
    /// Dev channel hides invalid "更新设置…" and retains debug "测试跳杯弹窗"; release channel keeps the menu focused and clean.
    /// </summary>
    public IReadOnlyList<TrayMenuItemDescriptor> BuildContextMenuItems()
    {
        var items = new List<TrayMenuItemDescriptor>(12)
        {
            new(CmdOpenWindow, $"打开主界面 ({_productTitle})", IsDefault: true),
        };

        TrayRuntimeStatusSnapshot status = RuntimeStatus;
        bool isCoolingDown = status.AntiCheatPhase is AntiCheatGuardPhase.ResidualGuardWatch or AntiCheatGuardPhase.CooldownLocked;
        bool hasRunningGame = !string.IsNullOrWhiteSpace(status.RunningGameTitle)
                              && status.RunningGameState != PlaySessionActivityState.Stopped;
        bool hasActiveTrainer = !string.IsNullOrWhiteSpace(status.ActiveTrainerName);

        uint statusHeaderId = CmdStatusHeaderBase;
        if (isCoolingDown || hasRunningGame || hasActiveTrainer)
        {
            items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));

            if (status.AntiCheatPhase == AntiCheatGuardPhase.ResidualGuardWatch)
            {
                items.Add(new TrayMenuItemDescriptor(statusHeaderId++, "反作弊守护进程监测中", IsDisabled: true));
            }
            else if (status.AntiCheatPhase == AntiCheatGuardPhase.CooldownLocked)
            {
                int secs = (int)Math.Ceiling(Math.Max(0.0, status.AntiCheatCooldownSeconds));
                string cooldownText = secs > 0 ? $"反作弊冷却中（剩余 {secs} 秒）" : "反作弊冷却中";
                items.Add(new TrayMenuItemDescriptor(statusHeaderId++, cooldownText, IsDisabled: true));
            }

            if (hasRunningGame)
            {
                items.Add(new TrayMenuItemDescriptor(statusHeaderId++, $"运行中：{status.RunningGameTitle}", IsDisabled: true));
                if (!string.IsNullOrWhiteSpace(status.RunningGameId) && StopRunningGameRequested is not null)
                {
                    items.Add(new TrayMenuItemDescriptor(
                        CmdStopRunningGame,
                        $"优雅关闭《{status.RunningGameTitle}》",
                        Tag: status.RunningGameId));
                }
            }

            if (hasActiveTrainer)
            {
                items.Add(new TrayMenuItemDescriptor(
                    CmdStopActiveTrainer,
                    $"停止修改器 ({status.ActiveTrainerName})"));
            }
        }

        if (!isCoolingDown && status.RecentGames is { Count: > 0 })
        {
            var quickEntries = status.RecentGames
                .Where(g => !string.IsNullOrWhiteSpace(g.GameId)
                            && !string.IsNullOrWhiteSpace(g.Title)
                            && (!hasRunningGame || !string.Equals(g.GameId, status.RunningGameId, StringComparison.OrdinalIgnoreCase)))
                .Take(MaxQuickLaunchRecentGames)
                .ToList();

            if (quickEntries.Count > 0)
            {
                items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));
                for (int i = 0; i < quickEntries.Count; i++)
                {
                    TrayRecentGameEntry entry = quickEntries[i];
                    items.Add(new TrayMenuItemDescriptor(
                        CmdQuickLaunchBase + (uint)i,
                        $"快速启动：{entry.Title}",
                        Tag: entry.GameId));
                }
            }
        }

        bool addedUpdateOrDevSeparator = false;
        if (!string.IsNullOrWhiteSpace(ReadyUpdateVersion))
        {
            items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));
            addedUpdateOrDevSeparator = true;
            items.Add(new TrayMenuItemDescriptor(CmdRestartForUpdate, $"立即重启更新 (v{ReadyUpdateVersion})"));
        }

        if (!_isDevChannel)
        {
            if (!addedUpdateOrDevSeparator)
            {
                items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));
            }

            items.Add(new TrayMenuItemDescriptor(CmdOpenUpdateSettings, "更新设置…"));
        }
        else
        {
            if (!addedUpdateOrDevSeparator)
            {
                items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));
            }

            items.Add(new TrayMenuItemDescriptor(CmdTestToast, "测试跳杯弹窗"));
        }

        items.Add(new TrayMenuItemDescriptor(0, string.Empty, IsSeparator: true));
        items.Add(new TrayMenuItemDescriptor(CmdExitApp, $"退出 {_productTitle}"));

        _lastBuiltMenu = items;
        return items;
    }

    /// <summary>
    /// Dispatches a tray context menu command ID to the matching callback event.
    /// </summary>
    public bool DispatchMenuCommand(uint commandId)
    {
        if (commandId == 0)
        {
            return false;
        }

        if (commandId == CmdOpenWindow)
        {
            OpenMainWindowRequested?.Invoke();
            return true;
        }

        if (commandId == CmdRestartForUpdate)
        {
            RestartForUpdateRequested?.Invoke();
            return true;
        }

        if (commandId == CmdOpenUpdateSettings)
        {
            OpenUpdateSettingsRequested?.Invoke();
            return true;
        }

        if (commandId == CmdTestToast)
        {
            TestToastRequested?.Invoke();
            return true;
        }

        if (commandId == CmdStopActiveTrainer)
        {
            StopActiveTrainerRequested?.Invoke();
            return true;
        }

        if (commandId == CmdStopRunningGame)
        {
            string? runningId = RuntimeStatus.RunningGameId;
            if (!string.IsNullOrWhiteSpace(runningId))
            {
                StopRunningGameRequested?.Invoke(runningId);
                return true;
            }

            return false;
        }

        if (commandId == CmdExitApp)
        {
            ExitApplicationRequested?.Invoke();
            return true;
        }

        if (commandId >= CmdQuickLaunchBase && commandId < CmdQuickLaunchBase + MaxQuickLaunchRecentGames)
        {
            IReadOnlyList<TrayMenuItemDescriptor> menu = _lastBuiltMenu.Count > 0 ? _lastBuiltMenu : BuildContextMenuItems();
            TrayMenuItemDescriptor? match = menu.FirstOrDefault(m => m.CommandId == commandId);
            if (!string.IsNullOrWhiteSpace(match?.Tag))
            {
                QuickLaunchGameRequested?.Invoke(match.Tag);
                return true;
            }
        }

        return false;
    }

    private void ShowNativeContextMenu(nint hwnd)
    {
        if (!GetCursorPos(out NativePoint pt))
        {
            return;
        }

        nint hMenu = CreatePopupMenu();
        if (hMenu == nint.Zero)
        {
            return;
        }

        try
        {
            IReadOnlyList<TrayMenuItemDescriptor> menuItems = BuildContextMenuItems();
            foreach (TrayMenuItemDescriptor item in menuItems)
            {
                if (item.IsSeparator)
                {
                    _ = AppendMenuW(hMenu, MfSeparator, 0, string.Empty);
                    continue;
                }

                uint flags = item.IsDisabled ? (MfString | MfDisabled | MfGrayed) : MfString;
                _ = AppendMenuW(hMenu, flags, item.CommandId, item.Text);
                if (item.IsDefault)
                {
                    _ = SetMenuDefaultItem(hMenu, item.CommandId, 0);
                }
            }

            _ = SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenuEx(
                hMenu,
                TpmRightButton | TpmBottomAlign | TpmReturnCmd,
                pt.X,
                pt.Y,
                hwnd,
                nint.Zero);
            _ = PostMessageW(hwnd, WmNull, nint.Zero, nint.Zero);

            _ = DispatchMenuCommand(cmd);
        }
        finally
        {
            _ = DestroyMenu(hMenu);
        }
    }

    private static unsafe nint CreateHIconFromPngBytes(byte[] pngBytes, int desiredWidthPx, int desiredHeightPx)
    {
        if (pngBytes.Length == 0)
        {
            return nint.Zero;
        }

        fixed (byte* pBytes = pngBytes)
        {
            return CreateIconFromResourceEx(
                pBytes,
                (uint)pngBytes.Length,
                fIcon: true,
                dwVer: 0x00030000,
                cxDesired: desiredWidthPx,
                cyDesired: desiredHeightPx,
                Flags: 0);
        }
    }

    private static unsafe NotifyIconDataW CreateNotifyIconData(nint hwnd, nint hIcon, string tooltip)
    {
        var data = default(NotifyIconDataW);
        data.CbSize = (uint)sizeof(NotifyIconDataW);
        data.HWnd = hwnd;
        data.UId = 1;
        data.UFlags = NifMessage | NifIcon | NifTip;
        data.UCallbackMessage = WmAppTrayCallback;
        data.HIcon = hIcon;
        data.GuidItem = TrayItemGuid;

        string safeTip = tooltip.Length > 120 ? tooltip[..120] : tooltip;
        for (int i = 0; i < safeTip.Length; i++)
        {
            data.SzTip[i] = safeTip[i];
        }

        data.SzTip[safeTip.Length] = '\0';
        return data;
    }

    private static unsafe NotifyIconDataW CreateNotifyIconDataWithInfo(
        nint hwnd,
        nint hIcon,
        string tooltip,
        string infoTitle,
        string infoMessage,
        bool isError)
    {
        NotifyIconDataW data = CreateNotifyIconData(hwnd, hIcon, tooltip);
        data.UFlags |= NifInfo;
        data.DwInfoFlags = (isError ? NiifWarning : NiifInfo) | NiifNoSound;

        string safeTitle = infoTitle.Length > 60 ? infoTitle[..60] : infoTitle;
        for (int i = 0; i < safeTitle.Length; i++)
        {
            data.SzInfoTitle[i] = safeTitle[i];
        }

        data.SzInfoTitle[safeTitle.Length] = '\0';

        string safeBody = infoMessage.Length > 250 ? infoMessage[..250] : infoMessage;
        for (int i = 0; i < safeBody.Length; i++)
        {
            data.SzInfo[i] = safeBody[i];
        }

        data.SzInfo[safeBody.Length] = '\0';
        return data;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_iconAdded && _messageWindow is not null)
        {
            try
            {
                var data = CreateNotifyIconData(_messageWindow.Handle, _hIcon, string.Empty);
                _ = Shell_NotifyIconW(NimDelete, ref data);
            }
            catch
            {
                // Ignore tray removal races on OS shutdown.
            }

            _iconAdded = false;
        }

        if (_hIcon != nint.Zero)
        {
            _ = DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }

        _messageWindow?.Dispose();
        _messageWindow = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconDataW
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UTimeoutOrVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint dwMessage, ref NotifyIconDataW lpData);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static unsafe partial nint CreateIconFromResourceEx(
        byte* presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint Flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint hMenu, uint uItem, uint fByPos);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenuEx(nint hmenu, uint fuFlags, int x, int y, nint hwnd, nint lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadImageFromFileW(
        nint hInst,
        string name,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
    private static partial nint LoadImageFromResourceIdW(
        nint hInst,
        nint resourceId,
        uint type,
        int cx,
        int cy,
        uint fuLoad);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint GetModuleHandleW(string? lpModuleName);
}
