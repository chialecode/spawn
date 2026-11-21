namespace Spawn.Modules.Progress.Audio;

/// <summary>
/// Canonical 27-byte, 5.0-second pure-silence Standard MIDI File (SMF format 0) used by A0 pre-mute (<c>TrainerBGM.mid</c>).
/// Structure:
/// <list type="bullet">
/// <item><description>Header (14 B): <c>MThd</c>, length 6, format 0, 1 track, 96 PPQ (<c>0x0060</c>).</description></item>
/// <item><description>Track (13 B): <c>MTrk</c>, length 5, VLQ delta <c>87 40</c> (960 ticks = 10 quarter notes = 5.0 s at default 120 BPM), followed immediately by Meta End-of-Track <c>FF 2F 00</c> with zero note events.</description></item>
/// </list>
/// Non-zero duration prevents busy-loop replay in trainers that loop <c>TrainerBGM.mid</c> via MCI.
/// </summary>
public static class SilentMidiAssets
{
    public const int SilentSmfByteLength = 27;
    public const int TicksPerQuarterNote = 96;
    public const int SilentTrackDeltaTicks = 960;

    public static readonly TimeSpan NominalDuration = TimeSpan.FromSeconds(5.0);

    private static readonly byte[] CanonicalSilentSmf =
    [
        0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x01, 0x00, 0x60,
        0x4D, 0x54, 0x72, 0x6B, 0x00, 0x00, 0x00, 0x05, 0x87, 0x40, 0xFF, 0x2F, 0x00,
    ];

    /// <summary>Read-only span view over the exact 27-byte silent SMF payload.</summary>
    public static ReadOnlySpan<byte> SilentSmfBytes => CanonicalSilentSmf;

    /// <summary>Returns a defensive copy of the 27-byte silent SMF payload.</summary>
    public static byte[] GetSilentSmfCopy() => CanonicalSilentSmf.ToArray();

    /// <summary>Returns <see langword="true"/> when <paramref name="data"/> matches the exact 27-byte silent SMF byte-for-byte.</summary>
    public static bool IsExactSilentSmf(ReadOnlySpan<byte> data) =>
        data.SequenceEqual(CanonicalSilentSmf);
}
