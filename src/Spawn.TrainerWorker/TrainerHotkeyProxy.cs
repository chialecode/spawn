using System.Diagnostics;
using System.Runtime.InteropServices;
using Spawn.Contracts;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Spawn.TrainerWorker;

/// <summary>
/// Managed representation of a Win32 keyboard <c>INPUT</c> event for deterministic hotkey injection and unit-test verification.
/// </summary>
public readonly record struct KeyboardInputEvent(
    ushort VirtualKey,
    ushort ScanCode,
    uint Flags,
    nuint ExtraInfo = 0);

/// <summary>
/// Injectable keyboard input sink. Production uses Win32 <c>SendInput</c>; unit tests inject a fake recorder and never emit real OS keystrokes.
/// </summary>
public interface IHotkeyInputSender
{
    uint SendKeyboardInput(KeyboardInputEvent inputEvent);
}

/// <summary>
/// Injectable mapper wrapping <c>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX)</c>.
/// </summary>
public interface IVirtualKeyScanCodeMapper
{
    uint MapVirtualKeyToExtendedScanCode(ushort virtualKey);
}

/// <summary>
/// Injectable high-precision wait abstraction for staggered hotkey timing (<c>20ms / HoldMs / 20ms</c>).
/// </summary>
public interface IPrecisionWaiter
{
    void WaitMilliseconds(int milliseconds, CancellationToken cancellationToken);
}

/// <summary>
/// Production <c>SendInput</c> keyboard sender powered by CsWin32.
/// </summary>
internal sealed class Win32HotkeyInputSender : IHotkeyInputSender
{
    public static Win32HotkeyInputSender Instance { get; } = new();

    private Win32HotkeyInputSender()
    {
    }

    public unsafe uint SendKeyboardInput(KeyboardInputEvent inputEvent)
    {
        INPUT input = default;
        input.type = INPUT_TYPE.INPUT_KEYBOARD;
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = (VIRTUAL_KEY)inputEvent.VirtualKey,
            wScan = inputEvent.ScanCode,
            dwFlags = (KEYBD_EVENT_FLAGS)inputEvent.Flags,
            time = 0,
            dwExtraInfo = inputEvent.ExtraInfo,
        };

        return PInvoke.SendInput(1, &input, Marshal.SizeOf<INPUT>());
    }
}

/// <summary>
/// Production <c>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX = 4)</c> mapper.
/// </summary>
internal sealed class Win32VirtualKeyScanCodeMapper : IVirtualKeyScanCodeMapper
{
    private const uint MapVkToVscEx = 4;

    public static Win32VirtualKeyScanCodeMapper Instance { get; } = new();

    private Win32VirtualKeyScanCodeMapper()
    {
    }

    public uint MapVirtualKeyToExtendedScanCode(ushort virtualKey) =>
        PInvoke.MapVirtualKey(virtualKey, (MAP_VIRTUAL_KEY_TYPE)MapVkToVscEx);
}

/// <summary>
/// High-precision wait using <see cref="Stopwatch"/> + short sleep/yield/spin to avoid Windows' 15.6ms timer granularity overshoot.
/// </summary>
internal sealed class StopwatchPrecisionWaiter : IPrecisionWaiter
{
    public static StopwatchPrecisionWaiter Instance { get; } = new();

    private StopwatchPrecisionWaiter()
    {
    }

    public void WaitMilliseconds(int milliseconds, CancellationToken cancellationToken)
    {
        if (milliseconds <= 0)
        {
            return;
        }

        long targetTicks = Stopwatch.GetTimestamp() + (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remainingTicks = targetTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                break;
            }

            double remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 16.0)
            {
                Thread.Sleep(1);
            }
            else if (remainingMs > 1.5)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }
}

/// <summary>
/// Synthesizes staggered keyboard chords for external trainers using <c>MAPVK_VK_TO_VSC_EX</c>:
/// <list type="bullet">
/// <item><description><c>wScan = LOBYTE(MapVirtualKeyW(vk, 4))</c>;</description></item>
/// <item><description><c>KEYEVENTF_EXTENDEDKEY (0x0001)</c> set when high byte is <c>0xE0</c>/<c>0xE1</c> or explicitly overridden (e.g. <c>Num Enter</c>);</description></item>
/// <item><description><c>Num 0-9</c>, <c>Num .</c>, <c>Num +</c>, <c>Num -</c>, <c>Num *</c>, <c>Right Shift</c>, and <c>NumLock</c> NEVER set <c>KEYEVENTF_EXTENDEDKEY</c>;</description></item>
/// <item><description>Populates both <c>wVk</c> and <c>wScan</c>, omitting <c>KEYEVENTF_SCANCODE</c> unless <c>ScanCodeMode = true</c>;</description></item>
/// <item><description>Staggered timing: <c>ModifierDown → 20ms → KeyDown → HoldMs (50–120, default 75) → KeyUp → 20ms → ModifierUp</c>.</description></item>
/// </list>
/// </summary>
public sealed class TrainerHotkeyProxy
{
    public const uint KeyEventFExtendedKey = 0x0001;
    public const uint KeyEventFKeyUp = 0x0002;
    public const uint KeyEventFScanCode = 0x0008;
    public const int ModifierStaggerDelayMs = 20;

    /// <summary>
    /// Keys that MUST NEVER carry <c>KEYEVENTF_EXTENDEDKEY</c>:
    /// Num 0–9 (0x60–0x69), Num * (0x6A), Num + (0x6B), Num - (0x6D), Num . (0x6E), NumLock (0x90), Right Shift (0xA1).
    /// </summary>
    private static readonly HashSet<ushort> ForbiddenExtendedVirtualKeys =
    [
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, // VK_NUMPAD0 .. VK_NUMPAD9
        0x6A, // VK_MULTIPLY (Num *)
        0x6B, // VK_ADD (Num +)
        0x6D, // VK_SUBTRACT (Num -)
        0x6E, // VK_DECIMAL (Num .)
        0x90, // VK_NUMLOCK
        0xA1, // VK_RSHIFT
    ];

    /// <summary>
    /// Dedicated extended keys whose <c>MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX)</c> scan code omits the <c>0xE0</c>
    /// prefix on Windows (navigation cluster shared with NumLock-off numpad, plus right modifiers and Num /).
    /// </summary>
    private static readonly HashSet<ushort> AlwaysExtendedVirtualKeys =
    [
        0x21, // VK_PRIOR (Page Up)
        0x22, // VK_NEXT (Page Down)
        0x23, // VK_END
        0x24, // VK_HOME
        0x25, // VK_LEFT
        0x26, // VK_UP
        0x27, // VK_RIGHT
        0x28, // VK_DOWN
        0x2C, // VK_SNAPSHOT (PrintScreen)
        0x2D, // VK_INSERT
        0x2E, // VK_DELETE
        0x5B, // VK_LWIN
        0x5C, // VK_RWIN
        0x5D, // VK_APPS
        0x6F, // VK_DIVIDE (Num /)
        0xA3, // VK_RCONTROL
        0xA5, // VK_RMENU (Right Alt)
    ];

    private readonly IHotkeyInputSender _inputSender;
    private readonly IVirtualKeyScanCodeMapper _scanCodeMapper;
    private readonly IPrecisionWaiter _waiter;
    private readonly Lock _sendGate = new();

    public TrainerHotkeyProxy(
        IHotkeyInputSender? inputSender = null,
        IVirtualKeyScanCodeMapper? scanCodeMapper = null,
        IPrecisionWaiter? waiter = null)
    {
        _inputSender = inputSender ?? Win32HotkeyInputSender.Instance;
        _scanCodeMapper = scanCodeMapper ?? Win32VirtualKeyScanCodeMapper.Instance;
        _waiter = waiter ?? StopwatchPrecisionWaiter.Instance;
    }

    public KeyboardInputEvent BuildKeyboardEvent(
        ushort virtualKey,
        bool isKeyUp,
        bool isExtendedKeyOverride = false,
        bool scanCodeMode = false)
    {
        uint vscEx = _scanCodeMapper.MapVirtualKeyToExtendedScanCode(virtualKey);
        ushort scanCode = (ushort)(vscEx & 0xFFu);
        uint prefixByte = (vscEx >> 8) & 0xFFu;

        bool isExtended = !ForbiddenExtendedVirtualKeys.Contains(virtualKey)
                          && (prefixByte == 0xE0u
                              || prefixByte == 0xE1u
                              || AlwaysExtendedVirtualKeys.Contains(virtualKey)
                              || isExtendedKeyOverride);

        uint flags = 0;
        if (isExtended)
        {
            flags |= KeyEventFExtendedKey;
        }

        if (scanCodeMode)
        {
            flags |= KeyEventFScanCode;
        }

        if (isKeyUp)
        {
            flags |= KeyEventFKeyUp;
        }

        return new KeyboardInputEvent(
            VirtualKey: virtualKey,
            ScanCode: scanCode,
            Flags: flags);
    }

    public TrainerTriggerHotkeyResponse Trigger(
        TrainerTriggerHotkeyRequest request,
        int defaultHoldMs = TrainerSettingsDto.DefaultHotkeyHoldMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VirtualKey == 0)
        {
            return new TrainerTriggerHotkeyResponse
            {
                Succeeded = false,
                EventsInjected = 0,
                ErrorMessage = "VirtualKey must be non-zero.",
            };
        }

        int effectiveHoldMs = Math.Clamp(
            request.HoldMsOverride ?? defaultHoldMs,
            TrainerSettingsDto.MinHotkeyHoldMs,
            TrainerSettingsDto.MaxHotkeyHoldMs);

        var modifiers = DeduplicateModifiers(request.Modifiers, request.VirtualKey);
        int sentCount = 0;

        lock (_sendGate)
        {
            // 1. Press modifiers (in declared order)
            if (modifiers.Count > 0)
            {
                foreach (ushort modVk in modifiers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    KeyboardInputEvent modDown = BuildKeyboardEvent(
                        modVk,
                        isKeyUp: false,
                        isExtendedKeyOverride: false,
                        scanCodeMode: request.ScanCodeMode);
                    sentCount += checked((int)_inputSender.SendKeyboardInput(modDown));
                }

                _waiter.WaitMilliseconds(ModifierStaggerDelayMs, cancellationToken);
            }

            // 2. Press primary key
            cancellationToken.ThrowIfCancellationRequested();
            KeyboardInputEvent keyDown = BuildKeyboardEvent(
                request.VirtualKey,
                isKeyUp: false,
                isExtendedKeyOverride: request.IsExtendedKeyOverride,
                scanCodeMode: request.ScanCodeMode);
            sentCount += checked((int)_inputSender.SendKeyboardInput(keyDown));

            // 3. Hold primary key (50–120ms, default 75ms)
            _waiter.WaitMilliseconds(effectiveHoldMs, cancellationToken);

            // 4. Release primary key
            KeyboardInputEvent keyUp = BuildKeyboardEvent(
                request.VirtualKey,
                isKeyUp: true,
                isExtendedKeyOverride: request.IsExtendedKeyOverride,
                scanCodeMode: request.ScanCodeMode);
            sentCount += checked((int)_inputSender.SendKeyboardInput(keyUp));

            // 5. Wait 20ms and release modifiers in reverse order
            if (modifiers.Count > 0)
            {
                _waiter.WaitMilliseconds(ModifierStaggerDelayMs, CancellationToken.None);
                for (int i = modifiers.Count - 1; i >= 0; i--)
                {
                    KeyboardInputEvent modUp = BuildKeyboardEvent(
                        modifiers[i],
                        isKeyUp: true,
                        isExtendedKeyOverride: false,
                        scanCodeMode: request.ScanCodeMode);
                    sentCount += checked((int)_inputSender.SendKeyboardInput(modUp));
                }
            }
        }

        return new TrainerTriggerHotkeyResponse
        {
            Succeeded = sentCount > 0,
            EventsInjected = sentCount,
        };
    }

    private static List<ushort> DeduplicateModifiers(IReadOnlyList<ushort>? rawModifiers, ushort primaryVk)
    {
        if (rawModifiers is null || rawModifiers.Count == 0)
        {
            return [];
        }

        var result = new List<ushort>(rawModifiers.Count);
        foreach (ushort mod in rawModifiers)
        {
            if (mod != 0 && mod != primaryVk && !result.Contains(mod))
            {
                result.Add(mod);
            }
        }

        return result;
    }
}
