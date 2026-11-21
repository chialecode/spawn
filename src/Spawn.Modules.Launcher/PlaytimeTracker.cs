using System.Runtime.InteropServices;
using Spawn.Contracts;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Snapshot of the current OS foreground window queried via benign user32 APIs (zero hooks).
/// </summary>
public readonly record struct ForegroundWindowSnapshot(
    nint WindowHandle,
    int ProcessId,
    string WindowClassName);

/// <summary>
/// Abstracts foreground focus, keyboard/mouse idle time, and gamepad activity probing
/// so <see cref="PlaytimeTracker"/> is 100% deterministic under unit tests.
/// </summary>
public interface IUserActivityProbe
{
    ForegroundWindowSnapshot GetForegroundWindow();
    double GetKeyboardMouseIdleSeconds();
    double GetGamepadIdleSeconds();
}

/// <summary>
/// Result of evaluating a single 1Hz playtime tick.
/// </summary>
public readonly record struct PlaytimeTickResult(
    PlaySessionActivityState State,
    long ActiveSec,
    long BackgroundSec,
    long AfkSec,
    bool StateChanged);

/// <summary>
/// Production Clean-Host <see cref="IUserActivityProbe"/> using strictly benign Win32 queries:
/// <c>GetForegroundWindow</c>, <c>GetWindowThreadProcessId</c>, <c>GetClassNameW</c>, <c>GetLastInputInfo</c>,
/// and <c>XInputGetState</c> with a 5-second empty-slot backoff to prevent USB/PCIe bus micro-stutter.
/// </summary>
public sealed partial class Win32UserActivityProbe : IUserActivityProbe
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;

    // Standard XInput deadzone thresholds preventing stick drift from masking AFK detection.
    private const double LeftThumbDeadzone = 7849.0;
    private const double RightThumbDeadzone = 8689.0;
    private const byte TriggerThreshold = 30;
    private static readonly TimeSpan DisconnectedSlotBackoff = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _gamepadLock = new();
    private readonly DateTimeOffset[] _slotNextProbeUtc = new DateTimeOffset[4];
    private readonly XInputState[] _slotLastState = new XInputState[4];
    private readonly bool[] _slotWasConnected = new bool[4];
    private DateTimeOffset? _lastGamepadActiveUtc;
    private volatile bool _xinputUnavailable;

    public Win32UserActivityProbe(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public unsafe ForegroundWindowSnapshot GetForegroundWindow()
    {
        nint hwnd = GetForegroundWindowNative();
        if (hwnd == nint.Zero)
        {
            return new ForegroundWindowSnapshot(nint.Zero, 0, string.Empty);
        }

        _ = GetWindowThreadProcessId(hwnd, out uint pid);

        Span<char> classBuf = stackalloc char[256];
        string className = string.Empty;
        fixed (char* pClass = classBuf)
        {
            int len = GetClassNameW(hwnd, pClass, classBuf.Length);
            if (len > 0)
            {
                className = new string(classBuf[..len]);
            }
        }

        return new ForegroundWindowSnapshot(hwnd, (int)pid, className);
    }

    public double GetKeyboardMouseIdleSeconds()
    {
        var info = new LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>(),
        };

        if (!GetLastInputInfo(ref info))
        {
            return 0.0;
        }

        uint currentTick = unchecked((uint)Environment.TickCount);
        uint elapsedMs = unchecked(currentTick - info.dwTime);
        return elapsedMs / 1000.0;
    }

    public double GetGamepadIdleSeconds()
    {
        if (_xinputUnavailable)
        {
            return double.MaxValue;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_gamepadLock)
        {
            for (uint slot = 0; slot < 4; slot++)
            {
                if (now < _slotNextProbeUtc[slot])
                {
                    continue;
                }

                uint status;
                XInputState state = default;
                try
                {
                    status = TryXInputGetState(slot, out state);
                }
                catch (DllNotFoundException)
                {
                    _xinputUnavailable = true;
                    return double.MaxValue;
                }
                catch (EntryPointNotFoundException)
                {
                    _xinputUnavailable = true;
                    return double.MaxValue;
                }

                if (status == ErrorDeviceNotConnected)
                {
                    _slotWasConnected[slot] = false;
                    // Back off empty controller slots for 5s to avoid hardware bus micro-stutter.
                    _slotNextProbeUtc[slot] = now + DisconnectedSlotBackoff;
                    continue;
                }

                if (status != ErrorSuccess)
                {
                    _slotNextProbeUtc[slot] = now + DisconnectedSlotBackoff;
                    continue;
                }

                bool wasConnected = _slotWasConnected[slot];
                var prev = _slotLastState[slot];
                _slotWasConnected[slot] = true;
                _slotLastState[slot] = state;

                if (IsGamepadEngaged(in state, wasConnected, in prev))
                {
                    _lastGamepadActiveUtc = now;
                }
            }

            if (!_lastGamepadActiveUtc.HasValue)
            {
                return double.MaxValue;
            }

            return Math.Max(0.0, (now - _lastGamepadActiveUtc.Value).TotalSeconds);
        }
    }

    private static bool IsGamepadEngaged(in XInputState current, bool hadPrevious, in XInputState previous)
    {
        ref readonly var gp = ref current.Gamepad;

        if (gp.wButtons != 0 || gp.bLeftTrigger > TriggerThreshold || gp.bRightTrigger > TriggerThreshold)
        {
            return true;
        }

        double leftMag = Math.Sqrt(((double)gp.sThumbLX * gp.sThumbLX) + ((double)gp.sThumbLY * gp.sThumbLY));
        if (leftMag > LeftThumbDeadzone)
        {
            return true;
        }

        double rightMag = Math.Sqrt(((double)gp.sThumbRX * gp.sThumbRX) + ((double)gp.sThumbRY * gp.sThumbRY));
        if (rightMag > RightThumbDeadzone)
        {
            return true;
        }

        if (hadPrevious && current.dwPacketNumber != previous.dwPacketNumber)
        {
            ref readonly var prevGp = ref previous.Gamepad;
            if (gp.wButtons != prevGp.wButtons
                || Math.Abs(gp.bLeftTrigger - prevGp.bLeftTrigger) > 15
                || Math.Abs(gp.bRightTrigger - prevGp.bRightTrigger) > 15
                || Math.Abs(gp.sThumbLX - prevGp.sThumbLX) > 4000
                || Math.Abs(gp.sThumbLY - prevGp.sThumbLY) > 4000
                || Math.Abs(gp.sThumbRX - prevGp.sThumbRX) > 4000
                || Math.Abs(gp.sThumbRY - prevGp.sThumbRY) > 4000)
            {
                return true;
            }
        }

        return false;
    }

    private static uint TryXInputGetState(uint dwUserIndex, out XInputState state)
    {
        try
        {
            return XInputGetState14(dwUserIndex, out state);
        }
        catch (DllNotFoundException)
        {
            return XInputGetState910(dwUserIndex, out state);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint dwPacketNumber;
        public XInputGamepad Gamepad;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint GetForegroundWindowNative();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static unsafe partial int GetClassNameW(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo plii);

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState14(uint dwUserIndex, out XInputState pState);

    [LibraryImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static partial uint XInputGetState910(uint dwUserIndex, out XInputState pState);
}

/// <summary>
/// 1Hz tri-state playtime accounting state machine (<c>Active</c> / <c>Background</c> / <c>AFK</c>).
/// Recognizes tracked game tree PIDs, whitelisted <c>Spawn.OverlayHost</c> windows/PIDs, and companion
/// fullscreen upscaler host windows (<c>Magpie</c> / <c>Lossless Scaling</c>).
/// </summary>
public sealed class PlaytimeTracker
{
    public const double DefaultAfkTimeoutSeconds = 300.0;
    public const string MagpieHostWindowClass = "Window_Magpie_967EB565-6F73-4E94-AE53-00CC42592A22";

    private readonly IUserActivityProbe _activityProbe;
    private PlaySessionActivityState _currentState = PlaySessionActivityState.Launching;
    private long _activeSec;
    private long _backgroundSec;
    private long _afkSec;

    public PlaySessionActivityState CurrentState => _currentState;
    public long ActiveSec => Interlocked.Read(ref _activeSec);
    public long BackgroundSec => Interlocked.Read(ref _backgroundSec);
    public long AfkSec => Interlocked.Read(ref _afkSec);

    public PlaytimeTracker(IUserActivityProbe activityProbe)
    {
        ArgumentNullException.ThrowIfNull(activityProbe);
        _activityProbe = activityProbe;
    }

    public bool TransitionExplicitState(PlaySessionActivityState newState)
    {
        var previous = _currentState;
        _currentState = newState;
        return previous != newState;
    }

    /// <summary>
    /// Evaluates a single 1-second playtime sample and increments the appropriate bucket.
    /// </summary>
    public PlaytimeTickResult SampleOneSecond(
        IReadOnlyCollection<int> trackedGamePids,
        IReadOnlySet<nint> trustedOverlayWindows,
        IReadOnlySet<int> trustedOverlayProcessIds,
        double afkTimeoutSeconds = DefaultAfkTimeoutSeconds)
    {
        double effectiveAfkThreshold = afkTimeoutSeconds > 1.0 ? afkTimeoutSeconds : DefaultAfkTimeoutSeconds;
        var fg = _activityProbe.GetForegroundWindow();

        bool isForeground = IsEffectivelyForeground(
            fg,
            trackedGamePids,
            trustedOverlayWindows,
            trustedOverlayProcessIds);

        PlaySessionActivityState nextState;
        long active;
        long bg;
        long afk;

        if (!isForeground)
        {
            nextState = PlaySessionActivityState.Background;
            bg = Interlocked.Increment(ref _backgroundSec);
            active = Interlocked.Read(ref _activeSec);
            afk = Interlocked.Read(ref _afkSec);
        }
        else
        {
            double kbIdle = Math.Max(0.0, _activityProbe.GetKeyboardMouseIdleSeconds());
            double gpIdle = Math.Max(0.0, _activityProbe.GetGamepadIdleSeconds());
            double minIdle = Math.Min(kbIdle, gpIdle);

            if (minIdle < effectiveAfkThreshold)
            {
                nextState = PlaySessionActivityState.Active;
                active = Interlocked.Increment(ref _activeSec);
                bg = Interlocked.Read(ref _backgroundSec);
                afk = Interlocked.Read(ref _afkSec);
            }
            else
            {
                nextState = PlaySessionActivityState.Afk;
                afk = Interlocked.Increment(ref _afkSec);
                active = Interlocked.Read(ref _activeSec);
                bg = Interlocked.Read(ref _backgroundSec);
            }
        }

        bool changed = _currentState != nextState;
        _currentState = nextState;

        return new PlaytimeTickResult(
            State: nextState,
            ActiveSec: active,
            BackgroundSec: bg,
            AfkSec: afk,
            StateChanged: changed);
    }

    public static bool IsEffectivelyForeground(
        ForegroundWindowSnapshot fg,
        IReadOnlyCollection<int> trackedGamePids,
        IReadOnlySet<nint> trustedOverlayWindows,
        IReadOnlySet<int> trustedOverlayProcessIds)
    {
        if (fg.WindowHandle == nint.Zero)
        {
            return false;
        }

        if (trustedOverlayWindows.Contains(fg.WindowHandle))
        {
            return true;
        }

        if (fg.ProcessId > 0 && trustedOverlayProcessIds.Contains(fg.ProcessId))
        {
            return true;
        }

        if (fg.ProcessId > 0 && trackedGamePids.Contains(fg.ProcessId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(fg.WindowClassName)
            && trackedGamePids.Count > 0
            && IsTrustedCompanionWindowClass(fg.WindowClassName))
        {
            return true;
        }

        return false;
    }

    private static bool IsTrustedCompanionWindowClass(string windowClassName) =>
        string.Equals(windowClassName, MagpieHostWindowClass, StringComparison.OrdinalIgnoreCase)
        || windowClassName.StartsWith("Window_Magpie_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(windowClassName, "LosslessScaling", StringComparison.OrdinalIgnoreCase);
}
