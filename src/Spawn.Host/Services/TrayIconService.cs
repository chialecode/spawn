using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Host.Services;

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
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmBottomAlign = 0x0020;

    private const uint CmdOpenWindow = 1001;
    private const uint CmdExitApp = 1002;
    private const uint CmdTestToast = 1003;
    private const uint CmdRestartForUpdate = 1004;
    private const uint CmdOpenUpdateSettings = 1005;

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

    public bool IsActive => _iconAdded && _disposed == 0;
    public string? ReadyUpdateVersion { get; private set; }
    public string? LastNotificationTitle { get; private set; }
    public string? LastNotificationMessage { get; private set; }
    public int NotificationCount { get; private set; }

    public event Action? OpenMainWindowRequested;
    public event Action? TestToastRequested;
    public event Action? RestartForUpdateRequested;
    public event Action? OpenUpdateSettingsRequested;
    public event Action? ExitApplicationRequested;

    public TrayIconService()
    {
        _isHeadless = StartupWindowPolicy.IsHeadlessTestEnvironment(SpawnBuildInfo.IsReleaseChannel);
        _isDevChannel = SpawnBuildInfo.IsDevOrSourceChannel;
        _productTitle = SpawnBuildInfo.DisplayProductName;
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

            byte[] pngBytes = LoadOrRenderBrandIconPngBytes(_isDevChannel, sizePx: 64);
            _hIcon = CreateHIconFromPngBytes(pngBytes, desiredPx: 32);
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
        UpdateToolTip(normalized is null ? "运行就绪" : $"新版本 v{normalized} 已就绪，重启更新");
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
                OpenUpdateSettingsRequested?.Invoke();
            }
        }

        return nint.Zero;
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
            _ = AppendMenuW(hMenu, MfString, CmdOpenWindow, $"打开主界面 ({_productTitle})");
            _ = SetMenuDefaultItem(hMenu, CmdOpenWindow, 0);
            if (!string.IsNullOrWhiteSpace(ReadyUpdateVersion))
            {
                _ = AppendMenuW(hMenu, MfString, CmdRestartForUpdate, $"立即重启更新 (v{ReadyUpdateVersion})");
            }

            _ = AppendMenuW(hMenu, MfString, CmdOpenUpdateSettings, "更新设置…");
            _ = AppendMenuW(hMenu, MfString, CmdTestToast, "测试跳杯弹窗");
            _ = AppendMenuW(hMenu, MfSeparator, 0, string.Empty);
            _ = AppendMenuW(hMenu, MfString, CmdExitApp, $"退出 {_productTitle}");

            _ = SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenuEx(
                hMenu,
                TpmRightButton | TpmBottomAlign | TpmReturnCmd,
                pt.X,
                pt.Y,
                hwnd,
                nint.Zero);
            _ = PostMessageW(hwnd, WmNull, nint.Zero, nint.Zero);

            if (cmd == CmdOpenWindow)
            {
                OpenMainWindowRequested?.Invoke();
            }
            else if (cmd == CmdRestartForUpdate)
            {
                RestartForUpdateRequested?.Invoke();
            }
            else if (cmd == CmdOpenUpdateSettings)
            {
                OpenUpdateSettingsRequested?.Invoke();
            }
            else if (cmd == CmdTestToast)
            {
                TestToastRequested?.Invoke();
            }
            else if (cmd == CmdExitApp)
            {
                ExitApplicationRequested?.Invoke();
            }
        }
        finally
        {
            _ = DestroyMenu(hMenu);
        }
    }

    private static unsafe nint CreateHIconFromPngBytes(byte[] pngBytes, int desiredPx)
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
                cxDesired: desiredPx,
                cyDesired: desiredPx,
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
}
