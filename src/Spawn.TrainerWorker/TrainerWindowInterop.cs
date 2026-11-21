using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spawn.TrainerWorker;

/// <summary>
/// Abstraction over Win32 top-level window enumeration, graceful <c>WM_CLOSE</c> posting, and <c>ShowWindow</c> visibility control.
/// </summary>
internal interface ITrainerWindowInterop
{
    IReadOnlyList<nint> EnumerateTopLevelWindowsForProcesses(IReadOnlySet<int> processIds);
    bool PostCloseMessage(nint hwnd);
    bool SetWindowVisibility(nint hwnd, bool visible);
}

/// <summary>
/// Production Win32 implementation using CsWin32 source-generated <c>EnumWindows</c>,
/// <c>GetWindowThreadProcessId</c>, <c>PostMessageW(WM_CLOSE)</c>, and <c>ShowWindow(SW_SHOW/SW_HIDE)</c>.
/// Never injects code or modifies window procedures.
/// </summary>
internal sealed class Win32TrainerWindowInterop : ITrainerWindowInterop
{
    private const uint WmClose = 0x0010;

    public static Win32TrainerWindowInterop Instance { get; } = new();

    private Win32TrainerWindowInterop()
    {
    }

    public unsafe IReadOnlyList<nint> EnumerateTopLevelWindowsForProcesses(IReadOnlySet<int> processIds)
    {
        if (processIds.Count == 0)
        {
            return [];
        }

        var state = new EnumState(processIds);
        GCHandle handle = GCHandle.Alloc(state);
        try
        {
            _ = PInvoke.EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(handle));
            return state.Windows;
        }
        finally
        {
            handle.Free();
        }
    }

    public bool PostCloseMessage(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        return PInvoke.PostMessage(new HWND(hwnd), WmClose, default, default);
    }

    public bool SetWindowVisibility(nint hwnd, bool visible)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        SHOW_WINDOW_CMD cmd = visible ? SHOW_WINDOW_CMD.SW_SHOW : SHOW_WINDOW_CMD.SW_HIDE;
        _ = PInvoke.ShowWindow(new HWND(hwnd), cmd);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static unsafe BOOL EnumWindowsCallback(HWND hwnd, LPARAM lParam)
    {
        if (lParam.Value == 0)
        {
            return false;
        }

        GCHandle handle = GCHandle.FromIntPtr(lParam.Value);
        if (handle.Target is not EnumState state)
        {
            return false;
        }

        uint pid = 0;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid > 4 && state.ProcessIds.Contains(checked((int)pid)))
        {
            state.Windows.Add((nint)hwnd.Value);
        }

        return true;
    }

    private sealed class EnumState(IReadOnlySet<int> processIds)
    {
        public IReadOnlySet<int> ProcessIds { get; } = processIds;
        public List<nint> Windows { get; } = [];
    }
}
