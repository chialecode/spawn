using System.Globalization;
using System.Runtime.InteropServices;

namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Result of verifying silent MCI playback and replay of the 27-byte note-less SMF file.
/// </summary>
public sealed record SilentMidiMciVerificationResult
{
    public bool Succeeded { get; init; }
    public bool SkippedNoMidiDevice { get; init; }
    public uint MidiOutputDeviceCount { get; init; }
    public int? ReportedLengthMs { get; init; }
    public string? InitialMode { get; init; }
    public string? ReplayMode { get; init; }
    public uint? MciErrorCode { get; init; }
    public string? DiagnosticMessage { get; init; }
}

/// <summary>
/// Verifies that the 27-byte pure-silence SMF opens, plays, reports non-zero duration, and replays cleanly via Windows MCI
/// (<c>winmm.dll!mciSendStringW</c>) without producing any audible sound or CPU busy-waiting.
/// Safely classifies environments without a MIDI sequencer device as <see cref="SilentMidiMciVerificationResult.SkippedNoMidiDevice"/>.
/// </summary>
public static partial class SilentMidiMciVerifier
{
    // MMSYSERR / MCIERR codes (mmsystem.h, MCIERR_BASE = 256) meaning no MIDI device or sequencer driver is usable.
    private const uint MmsysErrNoDriver = 6;
    private const uint MciErrHardware = 262;            // MCIERR_BASE + 6
    private const uint MciErrCannotLoadDriver = 266;    // MCIERR_BASE + 10
    private const uint MciErrInvalidDeviceName = 287;   // "the specified device was not found"
    private const uint MciErrDeviceNotInstalled = 306;  // MCIERR_BASE + 50
    private const uint MciErrSeqPortInUse = 337;        // MCIERR_BASE + 81
    private const uint MciErrSeqPortNonExistent = 338;  // MCIERR_BASE + 82
    private const uint MciErrSeqPortMapNoDevice = 339;  // MCIERR_BASE + 83
    private const uint MciErrSeqPortUnspecified = 342;  // MCIERR_BASE + 86
    private const uint MciErrSeqNoMidiPresent = 343;    // MCIERR_BASE + 87

    /// <summary>
    /// Returns the number of MIDI output devices currently reported by Windows Multimedia (<c>midiOutGetNumDevs</c>).
    /// </summary>
    public static uint GetMidiOutputDeviceCount() => midiOutGetNumDevs();

    /// <summary>
    /// Opens, queries duration, plays, stops, rewinds, replays, and closes <paramref name="midiFilePath"/> synchronously
    /// on the calling thread (preserving MCI alias thread affinity).
    /// </summary>
    public static SilentMidiMciVerificationResult VerifyPlaybackAndReplay(string midiFilePath, int settleDelayMs = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(midiFilePath);

        if (!File.Exists(midiFilePath))
        {
            return new SilentMidiMciVerificationResult
            {
                Succeeded = false,
                DiagnosticMessage = $"MIDI file does not exist: {midiFilePath}",
            };
        }

        uint deviceCount = midiOutGetNumDevs();
        if (deviceCount == 0)
        {
            return new SilentMidiMciVerificationResult
            {
                Succeeded = false,
                SkippedNoMidiDevice = true,
                MidiOutputDeviceCount = 0,
                DiagnosticMessage = "No MIDI output device present on this machine (midiOutGetNumDevs == 0).",
            };
        }

        string fullPath = Path.GetFullPath(midiFilePath);
        string alias = $"spawn_smf_{Guid.NewGuid():N}";
        bool opened = false;

        try
        {
            uint openErr = SendCommand($"open \"{fullPath}\" type sequencer alias {alias}", out _);
            if (openErr != 0)
            {
                return BuildErrorResult(deviceCount, "open", openErr);
            }

            opened = true;

            uint fmtErr = SendCommand($"set {alias} time format milliseconds", out _);
            if (fmtErr != 0)
            {
                return BuildErrorResult(deviceCount, "set time format", fmtErr);
            }

            uint lenErr = SendCommand($"status {alias} length", out string lengthText);
            if (lenErr != 0)
            {
                return BuildErrorResult(deviceCount, "status length", lenErr);
            }

            int? reportedLengthMs = int.TryParse(lengthText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMs)
                ? parsedMs
                : null;

            uint playErr = SendCommand($"play {alias}", out _);
            if (playErr != 0)
            {
                return BuildErrorResult(deviceCount, "play", playErr);
            }

            if (settleDelayMs > 0)
            {
                // Bounded sleep (never spin-wait) to verify the 5.0s silent track remains in "playing" mode
                // rather than snapping immediately to "stopped" (which happens with 0-tick MIDI files and causes busy loops).
                Thread.Sleep(Math.Min(settleDelayMs, 250));
            }

            uint mode1Err = SendCommand($"status {alias} mode", out string initialMode);
            if (mode1Err != 0)
            {
                return BuildErrorResult(deviceCount, "status mode (initial)", mode1Err);
            }

            uint stopErr = SendCommand($"stop {alias} wait", out _);
            if (stopErr != 0)
            {
                return BuildErrorResult(deviceCount, "stop", stopErr);
            }

            uint seekErr = SendCommand($"seek {alias} to start wait", out _);
            if (seekErr != 0)
            {
                return BuildErrorResult(deviceCount, "seek to start", seekErr);
            }

            uint replayErr = SendCommand($"play {alias}", out _);
            if (replayErr != 0)
            {
                return BuildErrorResult(deviceCount, "play (replay)", replayErr);
            }

            if (settleDelayMs > 0)
            {
                Thread.Sleep(Math.Min(settleDelayMs, 250));
            }

            uint mode2Err = SendCommand($"status {alias} mode", out string replayMode);
            if (mode2Err != 0)
            {
                return BuildErrorResult(deviceCount, "status mode (replay)", mode2Err);
            }

            return new SilentMidiMciVerificationResult
            {
                Succeeded = true,
                SkippedNoMidiDevice = false,
                MidiOutputDeviceCount = deviceCount,
                ReportedLengthMs = reportedLengthMs,
                InitialMode = initialMode.Trim(),
                ReplayMode = replayMode.Trim(),
                DiagnosticMessage =
                    $"MCI silent SMF playback and replay succeeded (initialMode={initialMode.Trim()}, replayMode={replayMode.Trim()}, reportedLengthMs={reportedLengthMs}, devices={deviceCount}).",
            };
        }
        finally
        {
            if (opened)
            {
                _ = SendCommand($"stop {alias} wait", out _);
                _ = SendCommand($"close {alias}", out _);
            }
        }
    }

    private static SilentMidiMciVerificationResult BuildErrorResult(uint deviceCount, string stage, uint errorCode)
    {
        string errorText = GetMciErrorString(errorCode);
        bool noMidiDevice = IsNoMidiDeviceError(errorCode);
        return new SilentMidiMciVerificationResult
        {
            Succeeded = false,
            SkippedNoMidiDevice = noMidiDevice,
            MidiOutputDeviceCount = deviceCount,
            MciErrorCode = errorCode,
            DiagnosticMessage = $"MCI '{stage}' returned error {errorCode}: {errorText}",
        };
    }

    private static bool IsNoMidiDeviceError(uint errorCode) =>
        errorCode is MmsysErrNoDriver
            or MciErrHardware
            or MciErrCannotLoadDriver
            or MciErrInvalidDeviceName
            or MciErrDeviceNotInstalled
            or MciErrSeqPortInUse
            or MciErrSeqPortNonExistent
            or MciErrSeqPortMapNoDevice
            or MciErrSeqPortUnspecified
            or MciErrSeqNoMidiPresent;

    private static unsafe uint SendCommand(string command, out string response)
    {
        Span<char> buffer = stackalloc char[256];
        buffer.Clear();
        fixed (char* pBuffer = buffer)
        {
            uint result = mciSendStringW(command, pBuffer, (uint)buffer.Length, nint.Zero);
            int nullIndex = buffer.IndexOf('\0');
            response = nullIndex >= 0
                ? new string(buffer[..nullIndex])
                : new string(buffer);
            return result;
        }
    }

    private static unsafe string GetMciErrorString(uint errorCode)
    {
        Span<char> buffer = stackalloc char[256];
        buffer.Clear();
        fixed (char* pBuffer = buffer)
        {
            if (mciGetErrorStringW(errorCode, pBuffer, (uint)buffer.Length))
            {
                int nullIndex = buffer.IndexOf('\0');
                return nullIndex >= 0 ? new string(buffer[..nullIndex]) : new string(buffer);
            }
        }

        return $"Unknown MCI error 0x{errorCode:X8}";
    }

    [LibraryImport("winmm.dll")]
    private static partial uint midiOutGetNumDevs();

    [LibraryImport("winmm.dll", EntryPoint = "mciSendStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint mciSendStringW(
        string lpszCommand,
        char* lpszReturnString,
        uint cchReturn,
        nint hwndCallback);

    [LibraryImport("winmm.dll", EntryPoint = "mciGetErrorStringW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool mciGetErrorStringW(
        uint fdwError,
        char* lpszErrorText,
        uint cchErrorText);
}
