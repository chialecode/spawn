using System.Diagnostics;
using Spawn.Contracts;
using Xunit;

namespace Spawn.TrainerWorker.UnitTests;

/// <summary>
/// M3-A-10 Hotkey proxy verification: asserts <c>MAPVK_VK_TO_VSC_EX</c>, <c>KEYEVENTF_EXTENDEDKEY</c>,
/// <c>wVk + wScan</c> population without default <c>KEYEVENTF_SCANCODE</c>, and staggered <c>20ms / HoldMs / 20ms</c> timing.
/// Uses strictly injected fake <see cref="IHotkeyInputSender"/> — ZERO real OS <c>SendInput</c> calls!
/// </summary>
public sealed class TrainerHotkeyProxyTests
{
    [Theory]
    [InlineData(0x6F, false, true)]  // Num / (VK_DIVIDE) -> 0xE035 -> EXTENDEDKEY ON
    [InlineData(0x0D, true, true)]   // Num Enter (VK_RETURN + override) -> EXTENDEDKEY ON
    [InlineData(0x0D, false, false)] // Regular Enter -> EXTENDEDKEY OFF
    [InlineData(0x24, false, true)]  // Home -> 0xE047 -> EXTENDEDKEY ON
    [InlineData(0x2D, false, true)]  // Insert -> 0xE052 -> EXTENDEDKEY ON
    [InlineData(0x2E, false, true)]  // Delete -> 0xE053 -> EXTENDEDKEY ON
    [InlineData(0x23, false, true)]  // End -> 0xE04F -> EXTENDEDKEY ON
    [InlineData(0x21, false, true)]  // Page Up -> 0xE049 -> EXTENDEDKEY ON
    [InlineData(0x22, false, true)]  // Page Down -> 0xE051 -> EXTENDEDKEY ON
    public void BuildKeyboardEvent_RealMapVirtualKeyEx_SetsExtendedKeyAndPopulatesVkAndScanWithoutScanCodeFlag(
        ushort vk,
        bool isExtendedOverride,
        bool expectedExtended)
    {
        var sender = new RecordingHotkeyInputSender();
        var waiter = new RecordingWaiter();
        var proxy = new TrainerHotkeyProxy(sender, Win32VirtualKeyScanCodeMapper.Instance, waiter);

        KeyboardInputEvent down = proxy.BuildKeyboardEvent(vk, isKeyUp: false, isExtendedKeyOverride: isExtendedOverride, scanCodeMode: false);
        KeyboardInputEvent up = proxy.BuildKeyboardEvent(vk, isKeyUp: true, isExtendedKeyOverride: isExtendedOverride, scanCodeMode: false);

        Assert.Equal(vk, down.VirtualKey);
        Assert.Equal(vk, up.VirtualKey);
        Assert.NotEqual((ushort)0, down.ScanCode);
        Assert.Equal(down.ScanCode, up.ScanCode);

        bool actualDownExt = (down.Flags & TrainerHotkeyProxy.KeyEventFExtendedKey) != 0;
        bool actualUpExt = (up.Flags & TrainerHotkeyProxy.KeyEventFExtendedKey) != 0;
        Assert.Equal(expectedExtended, actualDownExt);
        Assert.Equal(expectedExtended, actualUpExt);

        // Default mode MUST NOT set KEYEVENTF_SCANCODE (critical for NumLock OFF stability).
        Assert.Equal(0u, down.Flags & TrainerHotkeyProxy.KeyEventFScanCode);
        Assert.Equal(0u, up.Flags & TrainerHotkeyProxy.KeyEventFScanCode);
        Assert.Equal(0u, down.Flags & TrainerHotkeyProxy.KeyEventFKeyUp);
        Assert.NotEqual(0u, up.Flags & TrainerHotkeyProxy.KeyEventFKeyUp);
    }

    [Theory]
    [InlineData(0x60)] // Num 0
    [InlineData(0x61)] // Num 1
    [InlineData(0x62)] // Num 2
    [InlineData(0x63)] // Num 3
    [InlineData(0x64)] // Num 4
    [InlineData(0x65)] // Num 5
    [InlineData(0x66)] // Num 6
    [InlineData(0x67)] // Num 7
    [InlineData(0x68)] // Num 8
    [InlineData(0x69)] // Num 9
    [InlineData(0x6E)] // Num .
    [InlineData(0x6B)] // Num +
    [InlineData(0x6D)] // Num -
    [InlineData(0x6A)] // Num *
    [InlineData(0xA1)] // Right Shift
    [InlineData(0x90)] // NumLock
    public void BuildKeyboardEvent_ForbiddenExtendedKeys_NeverSetExtendedKeyEvenWithE0Prefix(ushort vk)
    {
        // Even if a mapper returns 0xE0xx (as Windows can for NumLock), these keys MUST NEVER set EXTENDEDKEY.
        var adversarialMapper = new FixedScanCodeMapper(_ => 0xE04Fu);
        var proxy = new TrainerHotkeyProxy(new RecordingHotkeyInputSender(), adversarialMapper, new RecordingWaiter());

        KeyboardInputEvent down = proxy.BuildKeyboardEvent(vk, isKeyUp: false, isExtendedKeyOverride: true, scanCodeMode: false);
        Assert.Equal(vk, down.VirtualKey);
        Assert.Equal((ushort)0x4F, down.ScanCode);
        Assert.Equal(0u, down.Flags & TrainerHotkeyProxy.KeyEventFExtendedKey);
        Assert.Equal(0u, down.Flags & TrainerHotkeyProxy.KeyEventFScanCode);
    }

    [Fact]
    public void BuildKeyboardEvent_Num1_RealMapVirtualKeyEx_ProducesScanCode0x4FWithoutExtendedOrScanCodeFlags()
    {
        var proxy = new TrainerHotkeyProxy(new RecordingHotkeyInputSender(), Win32VirtualKeyScanCodeMapper.Instance, new RecordingWaiter());
        KeyboardInputEvent num1 = proxy.BuildKeyboardEvent(0x61, isKeyUp: false);

        Assert.Equal((ushort)0x61, num1.VirtualKey);
        Assert.Equal((ushort)0x4F, num1.ScanCode);
        Assert.Equal(0u, num1.Flags);
    }

    [Fact]
    public void BuildKeyboardEvent_ScanCodeModeTrue_SetsKeyEventFScanCodeFlag()
    {
        var proxy = new TrainerHotkeyProxy(new RecordingHotkeyInputSender(), Win32VirtualKeyScanCodeMapper.Instance, new RecordingWaiter());
        KeyboardInputEvent ev = proxy.BuildKeyboardEvent(0x6F, isKeyUp: false, scanCodeMode: true);

        Assert.Equal((ushort)0x6F, ev.VirtualKey);
        Assert.NotEqual((ushort)0, ev.ScanCode);
        Assert.NotEqual(0u, ev.Flags & TrainerHotkeyProxy.KeyEventFScanCode);
        Assert.NotEqual(0u, ev.Flags & TrainerHotkeyProxy.KeyEventFExtendedKey);
    }

    [Fact]
    public void Trigger_WithModifiers_EmitsExactStaggeredTimelineAndReleasesModifiersInReverseOrder()
    {
        var timeline = new List<string>();
        var sender = new RecordingHotkeyInputSender(ev =>
        {
            string kind = (ev.Flags & TrainerHotkeyProxy.KeyEventFKeyUp) != 0 ? "UP" : "DOWN";
            bool ext = (ev.Flags & TrainerHotkeyProxy.KeyEventFExtendedKey) != 0;
            timeline.Add($"{kind}:0x{ev.VirtualKey:X2}:ext={ext}");
        });
        var waiter = new RecordingWaiter(ms => timeline.Add($"WAIT:{ms}ms"));

        var proxy = new TrainerHotkeyProxy(sender, Win32VirtualKeyScanCodeMapper.Instance, waiter);
        TrainerTriggerHotkeyResponse resp = proxy.Trigger(new TrainerTriggerHotkeyRequest
        {
            SessionId = "s1",
            EntryId = "e1",
            VirtualKey = 0x6F, // Num /
            Modifiers = [0x11, 0x10], // Ctrl + Shift
            HoldMsOverride = 90,
            ScanCodeMode = false,
        });

        Assert.True(resp.Succeeded);
        Assert.Equal(6, resp.EventsInjected);

        Assert.Equal(
        [
            "DOWN:0x11:ext=False",
            "DOWN:0x10:ext=False",
            "WAIT:20ms",
            "DOWN:0x6F:ext=True",
            "WAIT:90ms",
            "UP:0x6F:ext=True",
            "WAIT:20ms",
            "UP:0x10:ext=False",
            "UP:0x11:ext=False",
        ],
        timeline);
    }

    [Theory]
    [InlineData(null, 75)]
    [InlineData(10, 50)]
    [InlineData(50, 50)]
    [InlineData(120, 120)]
    [InlineData(300, 120)]
    public void Trigger_HoldMs_ClampedBetween50And120WithDefault75(int? holdOverride, int expectedWaitMs)
    {
        var waiter = new RecordingWaiter();
        var proxy = new TrainerHotkeyProxy(new RecordingHotkeyInputSender(), Win32VirtualKeyScanCodeMapper.Instance, waiter);

        TrainerTriggerHotkeyResponse resp = proxy.Trigger(new TrainerTriggerHotkeyRequest
        {
            SessionId = "s1",
            EntryId = "e1",
            VirtualKey = 0x61, // Num 1
            HoldMsOverride = holdOverride,
        });

        Assert.True(resp.Succeeded);
        Assert.Equal(2, resp.EventsInjected);
        Assert.Equal([expectedWaitMs], waiter.WaitsMs);
    }

    [Fact]
    public void StopwatchPrecisionWaiter_WaitsAtLeastRequestedDurationWithoutOvershootingWildly()
    {
        var sw = Stopwatch.StartNew();
        StopwatchPrecisionWaiter.Instance.WaitMilliseconds(20, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 19, $"Expected at least ~20ms wait, got {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 250, $"Wait should stay tight, got {sw.ElapsedMilliseconds}ms");
    }

    private sealed class RecordingHotkeyInputSender(Action<KeyboardInputEvent>? onSend = null) : IHotkeyInputSender
    {
        public List<KeyboardInputEvent> Events { get; } = [];

        public uint SendKeyboardInput(KeyboardInputEvent inputEvent)
        {
            Events.Add(inputEvent);
            onSend?.Invoke(inputEvent);
            return 1;
        }
    }

    private sealed class RecordingWaiter(Action<int>? onWait = null) : IPrecisionWaiter
    {
        public List<int> WaitsMs { get; } = [];

        public void WaitMilliseconds(int milliseconds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitsMs.Add(milliseconds);
            onWait?.Invoke(milliseconds);
        }
    }

    private sealed class FixedScanCodeMapper(Func<ushort, uint> mapFunc) : IVirtualKeyScanCodeMapper
    {
        public uint MapVirtualKeyToExtendedScanCode(ushort virtualKey) => mapFunc(virtualKey);
    }
}
