using System.Runtime.InteropServices;

namespace Spawn.OverlayHost.Interop;

/// <summary>
/// Pure monitor rectangle geometry helpers for selecting between <c>rcWork</c> (taskbar-aware desktop)
/// and <c>rcMonitor</c> (full screen when a borderless fullscreen game covers the monitor).
/// </summary>
public static class OverlayMonitorPlacement
{
    public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Math.Max(0, Right - Left);
        public int Height => Math.Max(0, Bottom - Top);
    }

    public readonly record struct MonitorPlacementBounds(
        int LeftPx,
        int TopPx,
        int WidthPx,
        int HeightPx,
        uint DpiX,
        uint DpiY,
        bool UsesFullscreenMonitorBounds);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="foregroundRect"/> covers the target monitor's
    /// full <paramref name="monitorRect"/> (within <paramref name="tolerancePx"/> pixels on each edge).
    /// </summary>
    public static bool IsForegroundRectCoveringMonitor(
        ScreenRect foregroundRect,
        ScreenRect monitorRect,
        int tolerancePx = 2)
    {
        if (foregroundRect.Width <= 0 || foregroundRect.Height <= 0
            || monitorRect.Width <= 0 || monitorRect.Height <= 0)
        {
            return false;
        }

        int tol = Math.Max(0, tolerancePx);
        return foregroundRect.Left <= monitorRect.Left + tol
               && foregroundRect.Top <= monitorRect.Top + tol
               && foregroundRect.Right >= monitorRect.Right - tol
               && foregroundRect.Bottom >= monitorRect.Bottom - tol;
    }

    /// <summary>
    /// Selects <paramref name="rcMonitor"/> when a non-shell foreground window covers the entire monitor
    /// (borderless/exclusive fullscreen), and falls back to <paramref name="rcWork"/> on normal desktop sessions.
    /// </summary>
    public static MonitorPlacementBounds SelectEffectiveBounds(
        ScreenRect rcMonitor,
        ScreenRect rcWork,
        ScreenRect? foregroundRect,
        bool isShellOrDesktopWindow,
        uint dpiX,
        uint dpiY)
    {
        bool useFullMonitor = !isShellOrDesktopWindow
                              && foregroundRect is { } fg
                              && IsForegroundRectCoveringMonitor(fg, rcMonitor);

        ScreenRect chosen = useFullMonitor ? rcMonitor : rcWork;
        return new MonitorPlacementBounds(
            chosen.Left,
            chosen.Top,
            Math.Max(640, chosen.Width),
            Math.Max(480, chosen.Height),
            dpiX >= 72 ? dpiX : 96u,
            dpiY >= 72 ? dpiY : 96u,
            useFullMonitor);
    }
}

/// <summary>
/// Clean-Host compliant Win32 window style, PerMonitorV2 DPI, and monitor topology helpers for <c>Spawn.OverlayHost</c>.
/// Contains zero process memory, thread, or input-hook imports so binary PE audits always pass 100%.
/// </summary>
internal static partial class OverlayNativeMethods
{
    public const int GwlExStyle = -20;

    public const nint WsExTopmost = 0x00000008;
    public const nint WsExTransparent = 0x00000020;
    public const nint WsExToolWindow = 0x00000080;
    public const nint WsExNoActivate = 0x08000000;

    public const int WmMouseActivate = 0x0021;
    public const nint MaNoActivate = 3;

    public static readonly nint HwndTopmost = new(-1);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;

    public const uint MonitorDefaultToPrimary = 0x00000001;
    public const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;

    public static void ApplyClickThroughNoActivateStyles(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        nint current = GetWindowLongPtrSafe(hwnd, GwlExStyle);
        nint updated = current | WsExTopmost | WsExTransparent | WsExNoActivate | WsExToolWindow;
        _ = SetWindowLongPtrSafe(hwnd, GwlExStyle, updated);
        _ = SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
    }

    public static void PositionAndBringTopmostNoActivate(nint hwnd, int physicalX, int physicalY)
    {
        if (hwnd == nint.Zero)
        {
            return;
        }

        _ = SetWindowPos(
            hwnd,
            HwndTopmost,
            physicalX,
            physicalY,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static uint GetEffectiveWindowDpi(nint hwnd)
    {
        if (hwnd != nint.Zero)
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi >= 72)
            {
                return dpi;
            }
        }

        return 96;
    }

    public static OverlayMonitorPlacement.MonitorPlacementBounds GetActiveMonitorWorkArea(nint ignoreHwnd = default)
    {
        nint fgHwnd = GetForegroundWindow();
        if (fgHwnd == ignoreHwnd)
        {
            fgHwnd = nint.Zero;
        }

        nint monitor = fgHwnd != nint.Zero
            ? MonitorFromWindow(fgHwnd, MonitorDefaultToNearest)
            : MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);

        if (monitor == nint.Zero)
        {
            monitor = MonitorFromPoint(new NativePoint { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        }

        uint dpiX = 96;
        uint dpiY = 96;
        if (monitor != nint.Zero)
        {
            try
            {
                if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint mx, out uint my) == 0 && mx >= 72 && my >= 72)
                {
                    dpiX = mx;
                    dpiY = my;
                }
            }
            catch
            {
                // Fallback to 96 DPI if shcore query fails.
            }

            var info = new MonitorInfo
            {
                CbSize = (uint)Marshal.SizeOf<MonitorInfo>(),
            };
            if (GetMonitorInfoW(monitor, ref info))
            {
                bool isShellOrDesktop = fgHwnd == nint.Zero || IsDesktopOrShellWindow(fgHwnd);
                OverlayMonitorPlacement.ScreenRect? fgRect = null;
                if (!isShellOrDesktop && GetWindowRect(fgHwnd, out NativeRect nativeFgRect))
                {
                    fgRect = new OverlayMonitorPlacement.ScreenRect(
                        nativeFgRect.Left,
                        nativeFgRect.Top,
                        nativeFgRect.Right,
                        nativeFgRect.Bottom);
                }

                var rcMonitor = new OverlayMonitorPlacement.ScreenRect(
                    info.RcMonitor.Left,
                    info.RcMonitor.Top,
                    info.RcMonitor.Right,
                    info.RcMonitor.Bottom);
                var rcWork = new OverlayMonitorPlacement.ScreenRect(
                    info.RcWork.Left,
                    info.RcWork.Top,
                    info.RcWork.Right,
                    info.RcWork.Bottom);

                return OverlayMonitorPlacement.SelectEffectiveBounds(
                    rcMonitor,
                    rcWork,
                    fgRect,
                    isShellOrDesktop,
                    dpiX,
                    dpiY);
            }
        }

        return new OverlayMonitorPlacement.MonitorPlacementBounds(0, 0, 1920, 1080, dpiX, dpiY, UsesFullscreenMonitorBounds: false);
    }

    private static bool IsDesktopOrShellWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return true;
        }

        if (hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
        {
            return true;
        }

        Span<char> classBuf = stackalloc char[64];
        int len;
        unsafe
        {
            fixed (char* pBuf = classBuf)
            {
                len = GetClassNameW(hwnd, pBuf, classBuf.Length);
            }
        }

        if (len <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> className = classBuf[..Math.Min(len, classBuf.Length)];
        return className.Equals("Progman", StringComparison.OrdinalIgnoreCase)
               || className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
               || className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
               || className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
    }

    public static (int LeftPx, int TopPx, int WidthPx, int HeightPx)? TryGetPhysicalWindowRect(nint hwnd)
    {
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out NativeRect rect))
        {
            return null;
        }

        return (rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    public static nint GetWindowLongPtrSafe(nint hWnd, int nIndex) =>
        nint.Size == 8
            ? GetWindowLongPtrW(hWnd, nIndex)
            : new nint(GetWindowLongW(hWnd, nIndex));

    public static nint SetWindowLongPtrSafe(nint hWnd, int nIndex, nint dwNewLong) =>
        nint.Size == 8
            ? SetWindowLongPtrW(hWnd, nIndex, dwNewLong)
            : new nint(SetWindowLongW(hWnd, nIndex, dwNewLong.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);
}
