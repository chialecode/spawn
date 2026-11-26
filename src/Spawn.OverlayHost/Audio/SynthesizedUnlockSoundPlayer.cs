using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.OverlayHost.Audio;

/// <summary>
/// Plays achievement unlock toast sounds via <c>NAudio.Wasapi</c> in shared mode:
/// - Strictly respects Windows' system default audio output device by default (including HDMI/DP monitors with external speakers),
///   while supporting explicit user-locked <c>MMDevice.ID</c> selection;
/// - Preserves exact <see cref="WaveFormatExtensible"/> (<c>dwChannelMask</c>) via <see cref="ExactMixFormatFloatWaveProvider"/>;
/// - Prepends a <c>160ms</c> sub-audible (<c>-67 dBFS</c>, <c>35 Hz</c> Hann-windowed) wake primer to wake idle DisplayPort / external speaker DACs
///   plus <c>240ms</c> tail silence, and propagates <c>PlaybackStopped</c> exceptions into <c>winmm.dll!PlaySoundW</c> memory fallback.
/// Strictly silent when muted or under automated tests (<c>SPAWN_MUTE_AUDIO=1</c> / <c>SPAWN_HEADLESS_TEST=1</c>).
/// </summary>
public sealed partial class SynthesizedUnlockSoundPlayer : IDisposable
{
    public const int DefaultLeadInSilenceMs = 45;
    public const int DefaultTailSilenceMs = 160;
    public const int DefaultWakePrimerMs = 160;
    public const int DefaultWakeTailSilenceMs = 240;
    public const float DefaultWakePrimerAmplitude = 0.00045f;
    public const double DefaultWakePrimerFrequencyHz = 35.0;
    public const float GentleNormalizeMinPeak = 0.05f;
    public const float GentleNormalizeMaxPeak = 0.65f;
    public const float GentleNormalizeTargetPeak = 0.85f;

    private const int DefaultSampleRate = 48000;
    private const int DefaultChannels = 2;
    private const int MaxDiagnosticLogBytes = 256 * 1024;
    private const uint SndSync = 0x0000;
    private const uint SndNodefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private static readonly Guid KsDataFormatSubtypeIeeeFloat =
        new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

    private static readonly Lock DiagnosticLogLock = new();

    private readonly Lock _cacheLock = new();
    private readonly bool _forceSilentEnvironment;
    private readonly Func<SteamClientSoundProbeResult> _steamProbe;
    private readonly Func<DateTimeOffset> _utcNowProvider;
    private readonly TimeSpan _missingSteamReprobeInterval;
    private DateTimeOffset _lastSteamProbeUtc = DateTimeOffset.MinValue;
    private string? _cachedSteamPath;
    private long _cachedSteamWriteTicks;
    private DecodedStereoWav? _cachedSteamClip;
    private string? _cachedCustomPath;
    private long _cachedCustomWriteTicks;
    private DecodedStereoWav? _cachedCustomClip;
    private int _disposed;

    public SynthesizedUnlockSoundPlayer(bool forceSilent = false)
        : this(
            forceSilent,
            static () => SteamClientSoundLocator.Probe(),
            static () => DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(60))
    {
    }

    public SynthesizedUnlockSoundPlayer(
        bool forceSilent,
        Func<SteamClientSoundProbeResult> steamProbe,
        Func<DateTimeOffset>? utcNowProvider = null,
        TimeSpan? missingSteamReprobeInterval = null)
    {
        ArgumentNullException.ThrowIfNull(steamProbe);
        _forceSilentEnvironment = forceSilent
            || string.Equals(Environment.GetEnvironmentVariable("SPAWN_MUTE_AUDIO"), "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("SPAWN_HEADLESS_TEST"), "1", StringComparison.OrdinalIgnoreCase);
        _steamProbe = steamProbe;
        _utcNowProvider = utcNowProvider ?? (static () => DateTimeOffset.UtcNow);
        _missingSteamReprobeInterval = missingSteamReprobeInterval ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>
    /// Forces the next <see cref="OverlaySoundPreset.SteamAuthentic"/> playback to re-probe the local Steam installation.
    /// </summary>
    public void InvalidateSteamSoundCache()
    {
        lock (_cacheLock)
        {
            _lastSteamProbeUtc = DateTimeOffset.MinValue;
            _cachedSteamPath = null;
            _cachedSteamWriteTicks = 0;
            _cachedSteamClip = null;
        }
    }

    /// <summary>
    /// Fully validates and decodes a user-selected custom <c>.wav</c> file (verifying uncompressed PCM or IEEE Float
    /// encoding and rejecting ADPCM / compressed / malformed containers with an actionable Chinese reason).
    /// </summary>
    public static WavValidationResult ValidateCustomWavFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new WavValidationResult(false, "未指定自定义音效文件路径。");
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new WavValidationResult(false, $"自定义音频路径格式无效：{ex.Message}");
        }

        if (!File.Exists(normalized))
        {
            return new WavValidationResult(false, "找不到指定的自定义 .wav 文件，请检查文件是否已被移动或删除。");
        }

        try
        {
            var info = new FileInfo(normalized);
            if (info.Length <= 44)
            {
                return new WavValidationResult(false, "音频文件过小或为空，不是有效的 RIFF WAVE 文件。");
            }

            if (info.Length > 20 * 1024 * 1024)
            {
                return new WavValidationResult(false, "自定义音频文件超过 20 MB 上限，请选择更精简的提示音文件。");
            }

            byte[] rawBytes = File.ReadAllBytes(normalized);
            return ValidateWavBytes(rawBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WavValidationResult(false, $"无法读取该音频文件：{ex.Message}");
        }
    }

    /// <summary>
    /// Pure in-memory RIFF WAVE format and full-decode validation returning a clear Chinese diagnostic message.
    /// </summary>
    public static WavValidationResult ValidateWavBytes(ReadOnlySpan<byte> wavBytes)
    {
        if (wavBytes.Length < 44
            || !wavBytes[..4].SequenceEqual("RIFF"u8)
            || !wavBytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return new WavValidationResult(
                false,
                "文件头不是标准 RIFF WAVE 格式（仅支持标准 .wav，不支持直接改后缀的 MP3/OGG/FLAC）。");
        }

        ushort effectiveFormatTag = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        bool hasFmt = false;
        bool hasData = false;

        int offset = 12;
        while (offset + 8 <= wavBytes.Length)
        {
            ReadOnlySpan<byte> chunkId = wavBytes.Slice(offset, 4);
            uint rawChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(offset + 4, 4));
            offset += 8;

            int available = wavBytes.Length - offset;
            int chunkLen = rawChunkSize > (uint)available ? available : (int)rawChunkSize;
            ReadOnlySpan<byte> payload = wavBytes.Slice(offset, chunkLen);

            if (chunkId.SequenceEqual("fmt "u8) && chunkLen >= 16)
            {
                ushort rawFormat = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));
                effectiveFormatTag = rawFormat == 0xFFFE && chunkLen >= 40
                    ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(24, 2))
                    : rawFormat;
                hasFmt = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                hasData = true;
                if (hasFmt)
                {
                    break;
                }
            }

            long advance = (long)chunkLen + (chunkLen & 1);
            if (advance <= 0 || offset + advance > wavBytes.Length)
            {
                break;
            }

            offset += (int)advance;
        }

        if (!hasFmt)
        {
            return new WavValidationResult(false, "WAV 文件缺少格式头（fmt 块），文件可能已损坏。");
        }

        if (!hasData)
        {
            return new WavValidationResult(false, "WAV 文件缺少音频数据块（data 块），无法播放。");
        }

        if (effectiveFormatTag is not (1 or 3))
        {
            string codecName = effectiveFormatTag switch
            {
                0x0002 => "Microsoft ADPCM 压缩格式",
                0x0011 => "IMA/DVI ADPCM 压缩格式",
                0x0006 => "ITU G.711 A-law 压缩格式",
                0x0007 => "ITU G.711 μ-law 压缩格式",
                0x0055 => "MP3 封装格式",
                _ => $"编码格式 0x{effectiveFormatTag:X4}",
            };
            return new WavValidationResult(
                false,
                $"不支持的 WAV 编码（{codecName}）；请转换为未压缩 PCM（8/16/24/32-bit）或 IEEE Float（32/64-bit）标准 WAV 后重试。");
        }

        if (channels is < 1 or > 8)
        {
            return new WavValidationResult(false, $"不支持的声道数（{channels} 声道），仅支持 1~8 声道 WAV。");
        }

        if (sampleRate is < 4000 or > 384000)
        {
            return new WavValidationResult(false, $"不支持的采样率（{sampleRate} Hz），仅支持 4000~384000 Hz。");
        }

        if ((effectiveFormatTag == 1 && bitsPerSample is not (8 or 16 or 24 or 32))
            || (effectiveFormatTag == 3 && bitsPerSample is not (32 or 64)))
        {
            string kind = effectiveFormatTag == 3 ? "IEEE Float" : "PCM";
            return new WavValidationResult(false, $"不支持的位深组合（{kind} {bitsPerSample}-bit）。");
        }

        if (!TryDecodeRiffWav(wavBytes, out DecodedStereoWav decoded) || decoded.FrameCount <= 0)
        {
            return new WavValidationResult(false, "WAV 音频数据区为空或已截断损坏，无法完整解码播放。");
        }

        double durationSec = (double)decoded.FrameCount / decoded.SampleRate;
        string formatLabel = effectiveFormatTag == 3 ? "IEEE Float" : "PCM";
        return new WavValidationResult(
            true,
            string.Create(
                CultureInfo.InvariantCulture,
                $"WAV 校验通过（{formatLabel} {bitsPerSample}-bit / {sampleRate} Hz / {channels} 声道，时长 {durationSec:0.00} 秒）。"),
            SampleRate: sampleRate,
            Channels: channels,
            BitsPerSample: bitsPerSample,
            DurationSeconds: durationSec);
    }

    /// <summary>
    /// Returns <see langword="true"/> when a rare (&lt;10%) or 100% Platinum toast under
    /// <see cref="OverlaySoundPreset.SteamAuthentic"/> should use SPAWN's enhanced rare/platinum chime
    /// instead of playing the identical standard Steam clip.
    /// </summary>
    public static bool ShouldUseEnhancedRareOrPlatinumSound(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SoundPreset != OverlaySoundPreset.SteamAuthentic || !request.EnhanceRareAndPlatinumSound)
        {
            return false;
        }

        return request.IsPlatinumCompletion || request.IsRare || request.GlobalPercent is < 10.0;
    }

    /// <summary>
    /// Computes the deterministic audio playback plan (external clip vs. enhanced synthesized chime) for <paramref name="request"/>.
    /// </summary>
    public static ResolvedSoundPlan ResolvePlaybackPlan(OverlayToastRequest request, bool hasExternalClip)
    {
        ArgumentNullException.ThrowIfNull(request);
        ChimePreset chime = ResolveChimePreset(request);
        if (ShouldUseEnhancedRareOrPlatinumSound(request))
        {
            return new ResolvedSoundPlan(UseExternalClip: false, SynthesizedPreset: chime, IsEnhancedRareOrPlatinum: true);
        }

        bool useExternal = request.SoundPreset is OverlaySoundPreset.SteamAuthentic or OverlaySoundPreset.CustomWav
                           && hasExternalClip;
        return new ResolvedSoundPlan(UseExternalClip: useExternal, SynthesizedPreset: chime, IsEnhancedRareOrPlatinum: false);
    }

    public void PlayForToast(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_disposed != 0 || _forceSilentEnvironment || request.MuteSound || request.SoundVolume <= 0.001)
        {
            return;
        }

        _ = Task.Run(() => PlayToastSoundCoreAsync(request));
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="format"/> is 32-bit IEEE Float, including both plain
    /// <see cref="WaveFormatEncoding.IeeeFloat"/> (<c>0x0003</c>) and <see cref="WaveFormatExtensible"/> (<c>0xFFFE</c>)
    /// carrying <c>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</c>.
    /// </summary>
    public static bool Is32BitIeeeFloatWaveFormat(WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.BitsPerSample != 32)
        {
            return false;
        }

        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return true;
        }

        return format is WaveFormatExtensible extensible
               && extensible.SubFormat == KsDataFormatSubtypeIeeeFloat;
    }

    /// <summary>
    /// Decodes a standard RIFF WAVE byte buffer (8/16/24/32-bit PCM or 32/64-bit IEEE Float, mono/stereo/multichannel,
    /// including <c>WAVE_FORMAT_EXTENSIBLE</c>) into interleaved stereo normalized floats <c>[-1.0f, 1.0f]</c>.
    /// </summary>
    public static bool TryDecodeRiffWav(ReadOnlySpan<byte> wavBytes, out DecodedStereoWav decoded)
    {
        decoded = default;
        if (wavBytes.Length < 44
            || !wavBytes[..4].SequenceEqual("RIFF"u8)
            || !wavBytes.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        ushort effectiveFormatTag = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort blockAlign = 0;
        ushort bitsPerSample = 0;
        bool hasFmt = false;
        ReadOnlySpan<byte> dataChunk = default;
        bool hasData = false;

        int offset = 12;
        while (offset + 8 <= wavBytes.Length)
        {
            ReadOnlySpan<byte> chunkId = wavBytes.Slice(offset, 4);
            uint rawChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.Slice(offset + 4, 4));
            offset += 8;

            int available = wavBytes.Length - offset;
            int chunkLen = rawChunkSize > (uint)available ? available : (int)rawChunkSize;
            ReadOnlySpan<byte> payload = wavBytes.Slice(offset, chunkLen);

            if (chunkId.SequenceEqual("fmt "u8) && chunkLen >= 16)
            {
                ushort rawFormat = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(14, 2));

                if (rawFormat == 0xFFFE && chunkLen >= 40)
                {
                    effectiveFormatTag = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(24, 2));
                }
                else
                {
                    effectiveFormatTag = rawFormat;
                }

                hasFmt = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataChunk = payload;
                hasData = true;
                if (hasFmt)
                {
                    break;
                }
            }

            long advance = (long)chunkLen + (chunkLen & 1);
            if (advance <= 0 || offset + advance > wavBytes.Length)
            {
                break;
            }

            offset += (int)advance;
        }

        if (!hasFmt || !hasData)
        {
            return false;
        }

        if (channels is < 1 or > 8
            || sampleRate is < 4000 or > 384000
            || bitsPerSample is not (8 or 16 or 24 or 32 or 64)
            || effectiveFormatTag is not (1 or 3))
        {
            return false;
        }

        if (effectiveFormatTag == 3 && bitsPerSample is not (32 or 64))
        {
            return false;
        }

        if (effectiveFormatTag == 1 && bitsPerSample == 64)
        {
            return false;
        }

        int bytesPerSample = bitsPerSample / 8;
        int minStride = channels * bytesPerSample;
        int frameStride = blockAlign >= minStride ? blockAlign : minStride;
        if (frameStride <= 0)
        {
            return false;
        }

        int frameCount = dataChunk.Length / frameStride;
        if (frameCount <= 0)
        {
            return false;
        }

        float[] stereo = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            ReadOnlySpan<byte> frameSlice = dataChunk.Slice(frame * frameStride, minStride);
            float left = DecodeSingleSample(frameSlice[..bytesPerSample], effectiveFormatTag, bitsPerSample);
            float right = channels >= 2
                ? DecodeSingleSample(frameSlice.Slice(bytesPerSample, bytesPerSample), effectiveFormatTag, bitsPerSample)
                : left;

            stereo[frame * 2] = Math.Clamp(left, -1f, 1f);
            stereo[frame * 2 + 1] = Math.Clamp(right, -1f, 1f);
        }

        decoded = new DecodedStereoWav(sampleRate, frameCount, stereo);
        return true;
    }

    /// <summary>
    /// Linearly boosts quiet pre-mastered WAV clips (such as Steam's <c>-14 dBFS</c> <c>desktop_toast_default.wav</c>
    /// or <c>-10 dBFS</c> <c>deck_ui_achievement_toast.wav</c>) when raw peak lies within <c>[minTriggerPeak, maxTriggerPeak]</c>
    /// so the baseline peak reaches <paramref name="targetPeak"/> (<c>0.85f</c>) without clipping before user volume scaling.
    /// </summary>
    public static DecodedStereoWav NormalizeGentlePeak(
        DecodedStereoWav source,
        float minTriggerPeak = GentleNormalizeMinPeak,
        float maxTriggerPeak = GentleNormalizeMaxPeak,
        float targetPeak = GentleNormalizeTargetPeak)
    {
        if (source.FrameCount <= 0 || source.InterleavedStereoSamples is not { Length: >= 2 } src)
        {
            return source;
        }

        float peak = ComputePeakAbs(src);
        if (peak < minTriggerPeak || peak > maxTriggerPeak || targetPeak <= peak)
        {
            return source;
        }

        float boost = targetPeak / peak;
        float[] boosted = new float[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            boosted[i] = Math.Clamp(src[i] * boost, -1f, 1f);
        }

        return new DecodedStereoWav(source.SampleRate, source.FrameCount, boosted);
    }

    /// <summary>
    /// Prepends pure zero lead-in frames and appends pure zero tail frames (preserved for pure-zero unit tests).
    /// </summary>
    public static float[] PadWithGuardSilence(
        ReadOnlySpan<float> interleavedSamples,
        int sampleRate,
        int channels,
        int leadInSilenceMs = DefaultLeadInSilenceMs,
        int tailSilenceMs = DefaultTailSilenceMs)
    {
        int safeRate = Math.Clamp(sampleRate, 8000, 384000);
        int safeChannels = Math.Clamp(channels, 1, 8);

        int leadFrames = leadInSilenceMs > 0
            ? (int)Math.Round(safeRate * (leadInSilenceMs / 1000.0))
            : 0;
        int tailFrames = tailSilenceMs > 0
            ? (int)Math.Round(safeRate * (tailSilenceMs / 1000.0))
            : 0;

        int leadSamples = leadFrames * safeChannels;
        int tailSamples = tailFrames * safeChannels;
        if (leadSamples == 0 && tailSamples == 0)
        {
            return interleavedSamples.ToArray();
        }

        float[] padded = new float[leadSamples + interleavedSamples.Length + tailSamples];
        interleavedSamples.CopyTo(padded.AsSpan(leadSamples));
        return padded;
    }

    /// <summary>
    /// Prepends a <paramref name="wakePrimerMs"/> (<c>160ms</c>) Hann-windowed <c>35 Hz</c> sub-audible (<c>-67 dBFS</c>, <c>0.00045f</c>)
    /// zero-crossing micro-excitation signal that wakes sleeping DisplayPort / HDMI audio packetizers and monitor 3.5mm external speaker DACs
    /// without any audible click, preserves <paramref name="interleavedSamples"/> verbatim in the middle, and appends
    /// <paramref name="tailSilenceMs"/> (<c>240ms</c>) of zero frames so <c>WasapiOut</c> drains full reverb tails before <c>Stop()</c>.
    /// </summary>
    public static float[] PrependSubAudibleWakePrimerAndTailSilence(
        ReadOnlySpan<float> interleavedSamples,
        int sampleRate,
        int channels,
        int wakePrimerMs = DefaultWakePrimerMs,
        int tailSilenceMs = DefaultWakeTailSilenceMs,
        float primerAmplitude = DefaultWakePrimerAmplitude)
    {
        int safeRate = Math.Clamp(sampleRate, 8000, 384000);
        int safeChannels = Math.Clamp(channels, 1, 8);

        int primerFrames = wakePrimerMs > 0
            ? (int)Math.Round(safeRate * (wakePrimerMs / 1000.0))
            : 0;
        int tailFrames = tailSilenceMs > 0
            ? (int)Math.Round(safeRate * (tailSilenceMs / 1000.0))
            : 0;

        int primerSamples = primerFrames * safeChannels;
        int tailSamples = tailFrames * safeChannels;
        float[] output = new float[primerSamples + interleavedSamples.Length + tailSamples];

        float clampedAmp = Math.Clamp(MathF.Abs(primerAmplitude), 0f, 0.001f);
        if (primerFrames > 1 && clampedAmp > 0f)
        {
            double windowDenom = primerFrames - 1;
            double twoPiFreqOverRate = 2.0 * Math.PI * DefaultWakePrimerFrequencyHz / safeRate;

            for (int frame = 0; frame < primerFrames; frame++)
            {
                // Hann / raised-cosine envelope guarantees exact 0.0f at frame 0 and frame (primerFrames - 1) for zero pop/click.
                double window = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * frame / windowDenom));
                float sample = (float)(clampedAmp * window * Math.Sin(twoPiFreqOverRate * frame));

                int baseIdx = frame * safeChannels;
                if (safeChannels == 1)
                {
                    output[baseIdx] = sample;
                }
                else
                {
                    output[baseIdx] = sample;
                    output[baseIdx + 1] = sample;
                    for (int ch = 2; ch < safeChannels; ch++)
                    {
                        output[baseIdx + ch] = 0f;
                    }
                }
            }
        }

        interleavedSamples.CopyTo(output.AsSpan(primerSamples));
        return output;
    }

    /// <summary>
    /// Whether an external clip of <paramref name="preset"/> gets the gentle peak boost before volume scaling.
    /// The Steam authentic toast is played at the Steam client's original sample level (100% volume == Steam), so it is
    /// never boosted; user-imported custom WAV files keep the boost because they are often mastered very quietly.
    /// </summary>
    public static bool ShouldNormalizeExternalClip(OverlaySoundPreset preset) => preset != OverlaySoundPreset.SteamAuthentic;

    /// <summary>
    /// Pure-memory conditioning pipeline (preserved signature and zero-padding behavior for existing unit tests).
    /// </summary>
    public static float[] PrepareExternalClipForWasapiPlayback(
        DecodedStereoWav source,
        float volumeScale,
        int targetSampleRate,
        int targetChannels = 2,
        bool normalizeGentlePeak = true,
        int leadInSilenceMs = DefaultLeadInSilenceMs,
        int tailSilenceMs = DefaultTailSilenceMs)
    {
        DecodedStereoWav conditioned = normalizeGentlePeak
            ? NormalizeGentlePeak(source)
            : source;
        float[] resampled = ResampleStereoFrames(conditioned, volumeScale, targetSampleRate, targetChannels);
        if (resampled.Length == 0)
        {
            return resampled;
        }

        return PadWithGuardSilence(resampled, targetSampleRate, targetChannels, leadInSilenceMs, tailSilenceMs);
    }

    /// <summary>
    /// Resamples decoded stereo samples from <see cref="DecodedStereoWav.SampleRate"/> to <paramref name="targetSampleRate"/>
    /// and <paramref name="targetChannels"/> using 4-point Catmull-Rom cubic Hermite interpolation and scales amplitude by <paramref name="volumeScale"/>.
    /// Pure in-memory calculation with zero audio hardware touch.
    /// </summary>
    public static float[] ResampleStereoFrames(
        DecodedStereoWav source,
        float volumeScale,
        int targetSampleRate,
        int targetChannels = 2)
    {
        if (source.FrameCount <= 0 || source.SampleRate <= 0 || source.InterleavedStereoSamples is not { Length: >= 2 } src)
        {
            return [];
        }

        int safeTargetRate = Math.Clamp(targetSampleRate, 8000, 384000);
        int safeTargetChannels = Math.Clamp(targetChannels, 1, 8);
        float clampedGain = Math.Clamp(volumeScale, 0f, 2f);

        int srcFrames = Math.Min(source.FrameCount, src.Length / 2);
        int targetFrames = source.SampleRate == safeTargetRate
            ? srcFrames
            : Math.Max(1, (int)Math.Round((double)srcFrames * safeTargetRate / source.SampleRate));

        float[] output = new float[targetFrames * safeTargetChannels];
        if (clampedGain <= 0f)
        {
            return output;
        }

        if (source.SampleRate == safeTargetRate)
        {
            for (int frame = 0; frame < targetFrames; frame++)
            {
                float fL = Math.Clamp(src[frame * 2] * clampedGain, -1f, 1f);
                float fR = Math.Clamp(src[frame * 2 + 1] * clampedGain, -1f, 1f);
                WriteChannelFrame(output, frame, safeTargetChannels, fL, fR);
            }

            return output;
        }

        int lastSrcFrame = srcFrames - 1;
        double step = (double)source.SampleRate / safeTargetRate;

        for (int frame = 0; frame < targetFrames; frame++)
        {
            double srcPos = frame * step;
            int idx1 = (int)srcPos;
            double frac = srcPos - idx1;

            int idx0 = Math.Clamp(idx1 - 1, 0, lastSrcFrame);
            int clampedIdx1 = Math.Clamp(idx1, 0, lastSrcFrame);
            int idx2 = Math.Clamp(idx1 + 1, 0, lastSrcFrame);
            int idx3 = Math.Clamp(idx1 + 2, 0, lastSrcFrame);

            double rawL = InterpolateCatmullRom(
                src[idx0 * 2],
                src[clampedIdx1 * 2],
                src[idx2 * 2],
                src[idx3 * 2],
                frac);
            double rawR = InterpolateCatmullRom(
                src[idx0 * 2 + 1],
                src[clampedIdx1 * 2 + 1],
                src[idx2 * 2 + 1],
                src[idx3 * 2 + 1],
                frac);

            float fL = (float)Math.Clamp(rawL * clampedGain, -1.0, 1.0);
            float fR = (float)Math.Clamp(rawR * clampedGain, -1.0, 1.0);
            WriteChannelFrame(output, frame, safeTargetChannels, fL, fR);
        }

        return output;
    }

    /// <summary>
    /// Convenience pure-memory pipeline that decodes a RIFF WAV buffer (e.g. 44,100 Hz 16-bit Stereo),
    /// applies <paramref name="volumeScale"/>, and resamples to <paramref name="targetSampleRate"/> IEEE 32-bit Float.
    /// </summary>
    public static float[] ResampleAndScaleWavToIeeeFloat(
        ReadOnlySpan<byte> wavBytes,
        float volumeScale,
        int targetSampleRate,
        int targetChannels = 2)
    {
        if (!TryDecodeRiffWav(wavBytes, out DecodedStereoWav decoded))
        {
            throw new InvalidDataException("Invalid or unsupported RIFF WAVE audio stream.");
        }

        return ResampleStereoFrames(decoded, volumeScale, targetSampleRate, targetChannels);
    }

    /// <summary>
    /// Converts a decoded stereo clip into a gain-scaled 16-bit little-endian RIFF PCM WAV byte array.
    /// </summary>
    public static byte[] ScaleDecodedWavToPcm16Bytes(DecodedStereoWav source, float volumeScale)
    {
        int srcFrames = Math.Max(0, Math.Min(source.FrameCount, (source.InterleavedStereoSamples?.Length ?? 0) / 2));
        int sampleRate = source.SampleRate > 0 ? source.SampleRate : DefaultSampleRate;
        int dataBytesLength = srcFrames * DefaultChannels * sizeof(short);
        byte[] wav = new byte[44 + dataBytesLength];

        WriteWavHeader(wav.AsSpan(0, 44), sampleRate, DefaultChannels, bitsPerSample: 16, dataBytesLength);
        if (srcFrames == 0 || source.InterleavedStereoSamples is null)
        {
            return wav;
        }

        float gain = Math.Clamp(volumeScale, 0f, 2f);
        Span<byte> pcm = wav.AsSpan(44);
        float[] src = source.InterleavedStereoSamples;

        for (int frame = 0; frame < srcFrames; frame++)
        {
            short sL = QuantizeLinearPcm16(src[frame * 2] * gain);
            short sR = QuantizeLinearPcm16(src[frame * 2 + 1] * gain);
            int byteOffset = frame * 4;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(byteOffset, 2), sL);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(byteOffset + 2, 2), sR);
        }

        return wav;
    }

    /// <summary>
    /// Builds a valid 16-bit stereo RIFF PCM WAV byte buffer for a polyphonic harmonic chime.
    /// </summary>
    public static byte[] SynthesizeWavBytes(ChimePreset preset, float masterVolume)
    {
        NoteVoice[] voices = GetPresetVoices(preset);
        double totalSeconds = ComputeTotalDurationSec(voices);

        int totalFrames = (int)(totalSeconds * DefaultSampleRate);
        int dataBytesLength = totalFrames * DefaultChannels * sizeof(short);
        byte[] wav = new byte[44 + dataBytesLength];

        WriteWavHeader(wav.AsSpan(0, 44), DefaultSampleRate, DefaultChannels, bitsPerSample: 16, dataBytesLength);

        float clampedMaster = Math.Clamp(masterVolume, 0f, 1f) * 0.68f;
        Span<byte> pcm = wav.AsSpan(44);

        for (int frame = 0; frame < totalFrames; frame++)
        {
            double t = (double)frame / DefaultSampleRate;
            var (left, right) = SampleVoicesStereo(voices, t);

            short sL = FloatToPcm16(left * clampedMaster);
            short sR = FloatToPcm16(right * clampedMaster);

            int byteOffset = frame * 4;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(byteOffset, 2), sL);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(byteOffset + 2, 2), sR);
        }

        return wav;
    }

    internal static ISampleProvider CreateIeeeFloatSampleProvider(
        ChimePreset preset,
        float masterVolume,
        int sampleRate,
        int channels,
        int leadInSilenceMs = DefaultLeadInSilenceMs,
        int tailSilenceMs = DefaultTailSilenceMs)
    {
        int safeRate = Math.Clamp(sampleRate, 8000, 192000);
        int safeChannels = Math.Clamp(channels, 1, 8);
        float[] buffer = SynthesizeInterleavedFloatFrames(preset, masterVolume, safeRate, safeChannels);
        float[] padded = PadWithGuardSilence(buffer, safeRate, safeChannels, leadInSilenceMs, tailSilenceMs);
        return new BufferedFloatSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(safeRate, safeChannels), padded);
    }

    /// <summary>
    /// Helper classifier retained for API compatibility; default audio routing strictly respects the Windows default output device.
    /// </summary>
    public static bool IsLikelyGpuDisplayAudioEndpoint(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("NVIDIA High Definition Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("AMD High Definition Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("AMD Streaming Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("Intel(R) Display Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("Intel Display Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("HD Audio Driver for Display Audio", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("显示器音频", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedAuxiliaryOrOpticalEndpoint(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return true;
        }

        return friendlyName.Contains("- Chat", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("(Chat)", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("数字输出", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("Digital Output", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("S/PDIF", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("SPDIF", StringComparison.OrdinalIgnoreCase)
               || friendlyName.Contains("Optical", StringComparison.OrdinalIgnoreCase);
    }

    public static int ScorePhysicalAudioEndpoint(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)
            || IsLikelyGpuDisplayAudioEndpoint(friendlyName)
            || IsExcludedAuxiliaryOrOpticalEndpoint(friendlyName))
        {
            return -1;
        }

        int score = 10;
        if (friendlyName.Contains("- Game", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("(Game)", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (friendlyName.Contains("耳机", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Headset", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("Earphone", StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }
        else if (friendlyName.Contains("扬声器", StringComparison.OrdinalIgnoreCase)
                 || friendlyName.Contains("Speaker", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }

    private async Task PlayToastSoundCoreAsync(OverlayToastRequest request)
    {
        if (_disposed != 0 || _forceSilentEnvironment)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        float gain = (float)Math.Clamp(request.SoundVolume, 0.0, 1.0);
        ChimePreset fallbackPreset = ResolveChimePreset(request);
        DecodedStereoWav? externalClip = TryResolveDecodedExternalClip(request);
        bool normalizeExternalClip = ShouldNormalizeExternalClip(request.SoundPreset);

        string requestedModeLabel = string.IsNullOrWhiteSpace(request.AudioOutputDeviceId)
                                    || string.Equals(request.AudioOutputDeviceId, OverlayAudioOutputModes.SystemDefault, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(request.AudioOutputDeviceId, OverlayAudioOutputModes.AutoSmart, StringComparison.OrdinalIgnoreCase)
            ? OverlayAudioOutputModes.SystemDefault
            : "ExplicitEndpoint";

        string resolvedDeviceName = "None";
        string mixFormatSummary = "N/A";
        float renderedPeak = 0f;
        string? wasapiFailureReason = null;

        // Primary path: WASAPI Shared Mode preserving exact WaveFormatExtensible (dwChannelMask) on system default endpoint
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice? targetDevice = ResolveTargetAudioDevice(enumerator, request.AudioOutputDeviceId);
            if (targetDevice is null)
            {
                throw new InvalidOperationException("No active render MMDevice available.");
            }

            resolvedDeviceName = targetDevice.FriendlyName;
            (mixFormatSummary, renderedPeak) = await PlayOnSingleWasapiDeviceAsync(
                targetDevice,
                externalClip,
                normalizeExternalClip,
                fallbackPreset,
                gain).ConfigureAwait(false);

            sw.Stop();
            AppendOverlayAudioDiagnosticLog(
                request.SoundPreset,
                gain,
                requestedModeLabel,
                resolvedDeviceName,
                mixFormatSummary,
                renderedPeak,
                sw.ElapsedMilliseconds,
                "WASAPI-OK");
            return;
        }
        catch (Exception ex)
        {
            wasapiFailureReason = $"{ex.GetType().Name}: {ex.Message}";
        }

        // Secondary fallback: Windows Waveform Audio Mapper (PlaySoundW in memory with 160ms DP wake primer + 240ms tail)
        if (_disposed != 0 || _forceSilentEnvironment)
        {
            return;
        }

        try
        {
            byte[] wavBytes = BuildWakePrimedPcm16FallbackWav(externalClip, normalizeExternalClip, fallbackPreset, gain, out renderedPeak);
            unsafe
            {
                fixed (byte* pWav = wavBytes)
                {
                    _ = PlaySoundW((nint)pWav, nint.Zero, SndMemory | SndSync | SndNodefault);
                }
            }

            sw.Stop();
            AppendOverlayAudioDiagnosticLog(
                request.SoundPreset,
                gain,
                requestedModeLabel,
                resolvedDeviceName,
                mixFormatSummary,
                renderedPeak,
                sw.ElapsedMilliseconds,
                $"WINMM-FALLBACK ({wasapiFailureReason ?? "Unknown"})");
        }
        catch (Exception fallbackEx)
        {
            sw.Stop();
            AppendOverlayAudioDiagnosticLog(
                request.SoundPreset,
                gain,
                requestedModeLabel,
                resolvedDeviceName,
                mixFormatSummary,
                renderedPeak,
                sw.ElapsedMilliseconds,
                $"AUDIO-FAILED (WASAPI={wasapiFailureReason}; WINMM={fallbackEx.GetType().Name})");
        }
    }

    private static MMDevice? ResolveTargetAudioDevice(
        MMDeviceEnumerator enumerator,
        string? requestedDeviceId)
    {
        // 1. Only use an explicit endpoint when the user manually selected a concrete MMDevice.ID
        //    (null, empty, "system-default", and legacy "auto-smart" ALL strictly follow Windows System Default!).
        if (!string.IsNullOrWhiteSpace(requestedDeviceId)
            && !string.Equals(requestedDeviceId.Trim(), OverlayAudioOutputModes.SystemDefault, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requestedDeviceId.Trim(), OverlayAudioOutputModes.AutoSmart, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                MMDevice explicitDev = enumerator.GetDevice(requestedDeviceId.Trim());
                if (explicitDev.State == DeviceState.Active)
                {
                    return explicitDev;
                }

                explicitDev.Dispose();
            }
            catch
            {
                // Unplugged or stale device ID -> fall back to Windows System Default below.
            }
        }

        // 2. Strictly follow Windows System Default output endpoint (Multimedia -> Console),
        //    including HDMI / DisplayPort monitors with external speakers!
        MMDevice? defaultDevice = TryGetDefaultActiveEndpoint(enumerator, Role.Multimedia)
                                  ?? TryGetDefaultActiveEndpoint(enumerator, Role.Console);
        if (defaultDevice is not null)
        {
            return defaultDevice;
        }

        // 3. Last resort only when Windows reports no active default device at all.
        try
        {
            var activeEndpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            MMDevice? firstActive = null;
            foreach (MMDevice candidate in activeEndpoints)
            {
                if (firstActive is null)
                {
                    firstActive = candidate;
                    if (!IsDeviceMutedSafe(candidate))
                    {
                        break;
                    }
                }
                else if (!IsDeviceMutedSafe(candidate))
                {
                    firstActive.Dispose();
                    firstActive = candidate;
                    break;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            return firstActive;
        }
        catch
        {
            return null;
        }
    }

    private static MMDevice? TryGetDefaultActiveEndpoint(MMDeviceEnumerator enumerator, Role role)
    {
        try
        {
            MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            if (dev.State == DeviceState.Active)
            {
                return dev;
            }

            dev.Dispose();
        }
        catch
        {
            // Endpoint unavailable for this role.
        }

        return null;
    }

    private static async Task<(string MixFormatSummary, float Peak)> PlayOnSingleWasapiDeviceAsync(
        MMDevice device,
        DecodedStereoWav? externalClip,
        bool normalizeExternalClip,
        ChimePreset fallbackPreset,
        float gain)
    {
        WaveFormat mix = device.AudioClient.MixFormat;
        int mixRate = Math.Clamp(mix.SampleRate, 8000, 384000);
        int mixChannels = Math.Clamp(mix.Channels, 1, 8);
        string mixSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{mix.Encoding}/{mixRate}Hz/{mixChannels}ch/{mix.BitsPerSample}bit");

        float[] coreSamples;
        double clipDurationSec;

        if (externalClip is { FrameCount: > 0 } clip)
        {
            DecodedStereoWav conditioned = normalizeExternalClip ? NormalizeGentlePeak(clip) : clip;
            coreSamples = ResampleStereoFrames(conditioned, gain, mixRate, mixChannels);
            clipDurationSec = (double)clip.FrameCount / Math.Max(1, clip.SampleRate);
        }
        else
        {
            coreSamples = SynthesizeInterleavedFloatFrames(fallbackPreset, gain, mixRate, mixChannels);
            clipDurationSec = (double)(coreSamples.Length / mixChannels) / mixRate;
        }

        float peak = ComputePeakAbs(coreSamples);
        float[] wakePrimedSamples = PrependSubAudibleWakePrimerAndTailSilence(
            coreSamples,
            mixRate,
            mixChannels,
            wakePrimerMs: DefaultWakePrimerMs,
            tailSilenceMs: DefaultWakeTailSilenceMs,
            primerAmplitude: DefaultWakePrimerAmplitude);

        double maxWaitSeconds = Math.Clamp(clipDurationSec + 2.2, 3.8, 12.0);

        using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, latency: 80);
        Exception? stoppedException = null;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) =>
        {
            stoppedException = e.Exception;
            done.TrySetResult();
        };

        MemoryStream? fallbackPcmStream = null;
        RawSourceWaveStream? fallbackRawWaveStream = null;
        try
        {
            if (Is32BitIeeeFloatWaveFormat(mix))
            {
                // Pass exact MixFormat (WaveFormatExtensible with dwChannelMask = 3) directly without degrading to 0x0003!
                output.Init(new ExactMixFormatFloatWaveProvider(mix, wakePrimedSamples));
            }
            else
            {
                // Rare 16/24-bit integer shared mix format -> feed 16-bit PCM stream for WASAPI DMO conversion.
                byte[] pcm16Bytes = ConvertInterleavedFloatToPcm16RawBytes(wakePrimedSamples);
                fallbackPcmStream = new MemoryStream(pcm16Bytes, writable: false);
                fallbackRawWaveStream = new RawSourceWaveStream(fallbackPcmStream, new WaveFormat(mixRate, 16, mixChannels));
                output.Init(fallbackRawWaveStream);
            }

            output.Play();
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(maxWaitSeconds))).ConfigureAwait(false);
            output.Stop();
        }
        finally
        {
            fallbackRawWaveStream?.Dispose();
            fallbackPcmStream?.Dispose();
        }

        if (stoppedException is not null)
        {
            throw new InvalidOperationException($"WASAPI playback error: {stoppedException.Message}", stoppedException);
        }

        return (mixSummary, peak);
    }

    private static byte[] BuildWakePrimedPcm16FallbackWav(
        DecodedStereoWav? externalClip,
        bool normalizeExternalClip,
        ChimePreset fallbackPreset,
        float gain,
        out float peak)
    {
        int sampleRate;
        float[] coreStereo;

        if (externalClip is { FrameCount: > 0 } clip)
        {
            sampleRate = Math.Clamp(clip.SampleRate, 8000, 192000);
            DecodedStereoWav conditioned = normalizeExternalClip ? NormalizeGentlePeak(clip) : clip;
            coreStereo = ResampleStereoFrames(conditioned, gain, sampleRate, DefaultChannels);
        }
        else
        {
            sampleRate = DefaultSampleRate;
            coreStereo = SynthesizeInterleavedFloatFrames(fallbackPreset, gain, sampleRate, DefaultChannels);
        }

        peak = ComputePeakAbs(coreStereo);
        float[] primedStereo = PrependSubAudibleWakePrimerAndTailSilence(
            coreStereo,
            sampleRate,
            DefaultChannels,
            wakePrimerMs: DefaultWakePrimerMs,
            tailSilenceMs: DefaultWakeTailSilenceMs,
            primerAmplitude: DefaultWakePrimerAmplitude);

        int dataBytesLength = primedStereo.Length * sizeof(short);
        byte[] wav = new byte[44 + dataBytesLength];
        WriteWavHeader(wav.AsSpan(0, 44), sampleRate, DefaultChannels, bitsPerSample: 16, dataBytesLength);

        Span<byte> pcm = wav.AsSpan(44);
        for (int i = 0; i < primedStereo.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 2, 2), QuantizeLinearPcm16(primedStereo[i]));
        }

        return wav;
    }

    private static byte[] ConvertInterleavedFloatToPcm16RawBytes(ReadOnlySpan<float> samples)
    {
        byte[] raw = new byte[samples.Length * sizeof(short)];
        Span<byte> dst = raw.AsSpan();
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(i * 2, 2), QuantizeLinearPcm16(samples[i]));
        }

        return raw;
    }

    private static float[] SynthesizeInterleavedFloatFrames(
        ChimePreset preset,
        float masterVolume,
        int sampleRate,
        int channels)
    {
        NoteVoice[] voices = GetPresetVoices(preset);
        double totalSeconds = ComputeTotalDurationSec(voices);
        int totalFrames = (int)(totalSeconds * sampleRate);
        float[] buffer = new float[totalFrames * channels];
        float clampedMaster = Math.Clamp(masterVolume, 0f, 1f) * 0.68f;

        for (int frame = 0; frame < totalFrames; frame++)
        {
            double t = (double)frame / sampleRate;
            var (left, right) = SampleVoicesStereo(voices, t);
            float fL = (float)Math.Tanh(left * clampedMaster);
            float fR = (float)Math.Tanh(right * clampedMaster);
            WriteChannelFrame(buffer, frame, channels, fL, fR);
        }

        return buffer;
    }

    private static float ComputePeakAbs(ReadOnlySpan<float> samples)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            float abs = MathF.Abs(samples[i]);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        return peak;
    }

    private void AppendOverlayAudioDiagnosticLog(
        OverlaySoundPreset preset,
        float volume,
        string requestedMode,
        string deviceFriendlyName,
        string mixFormatSummary,
        float peak,
        long elapsedMs,
        string resultSummary)
    {
        if (_forceSilentEnvironment)
        {
            return;
        }

        try
        {
            string logsDir = SpawnPaths.Resolve().LogsDirectory;
            Directory.CreateDirectory(logsDir);
            string logPath = Path.Combine(logsDir, "overlay-audio.log");
            string safeDevice = SanitizeSingleLineLogField(deviceFriendlyName);
            string safeResult = SanitizeSingleLineLogField(resultSummary);

            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} | preset={preset} | vol={volume:0.00} | mode={requestedMode} | device=\"{safeDevice}\" | mix={mixFormatSummary} | peak={peak:0.000} | ms={elapsedMs} | result={safeResult}{Environment.NewLine}");

            lock (DiagnosticLogLock)
            {
                var info = new FileInfo(logPath);
                if (info.Exists && info.Length > MaxDiagnosticLogBytes)
                {
                    byte[] existing = File.ReadAllBytes(logPath);
                    int keepOffset = existing.Length - (MaxDiagnosticLogBytes / 2);
                    while (keepOffset < existing.Length && existing[keepOffset] != (byte)'\n')
                    {
                        keepOffset++;
                    }

                    keepOffset = Math.Clamp(keepOffset + 1, 0, existing.Length);
                    using var truncatedStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                    truncatedStream.Write(existing, keepOffset, existing.Length - keepOffset);
                }

                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Never let diagnostic logging disrupt audio playback.
        }
    }

    private static string SanitizeSingleLineLogField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "N/A";
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static bool IsDeviceMutedSafe(MMDevice device)
    {
        try
        {
            return device.AudioEndpointVolume?.Mute == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the decoded external WAV clip (if applicable) for <paramref name="request"/>, respecting the
    /// rare/platinum enhancement switch and non-permanent Steam probe caching.
    /// </summary>
    public DecodedStereoWav? TryResolveDecodedExternalClip(OverlayToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ShouldUseEnhancedRareOrPlatinumSound(request))
        {
            return null;
        }

        return request.SoundPreset switch
        {
            OverlaySoundPreset.SteamAuthentic => GetOrLoadSteamAuthenticClip(forceReprobe: request.ReplaceActivePreview),
            OverlaySoundPreset.CustomWav => GetOrLoadCustomWavClip(request.CustomSoundPath),
            _ => null,
        };
    }

    private DecodedStereoWav? GetOrLoadSteamAuthenticClip(bool forceReprobe = false)
    {
        lock (_cacheLock)
        {
            DateTimeOffset now = _utcNowProvider();

            if (!forceReprobe && _cachedSteamClip is not null)
            {
                if (!string.IsNullOrWhiteSpace(_cachedSteamPath))
                {
                    try
                    {
                        if (File.Exists(_cachedSteamPath)
                            && File.GetLastWriteTimeUtc(_cachedSteamPath).Ticks == _cachedSteamWriteTicks)
                        {
                            return _cachedSteamClip;
                        }
                    }
                    catch
                    {
                        // Re-probe below if the previously cached path became unreadable.
                    }
                }
                else
                {
                    return _cachedSteamClip;
                }
            }

            // When Steam was previously absent, allow re-probing after the cooldown elapses or when explicitly requested
            // (e.g., settings preview / InvalidateSteamSoundCache) instead of caching absence permanently.
            if (!forceReprobe
                && _cachedSteamClip is null
                && _lastSteamProbeUtc != DateTimeOffset.MinValue
                && (now - _lastSteamProbeUtc) < _missingSteamReprobeInterval)
            {
                return null;
            }

            _lastSteamProbeUtc = now;
            _cachedSteamClip = null;
            _cachedSteamPath = null;
            _cachedSteamWriteTicks = 0;

            try
            {
                SteamClientSoundProbeResult probe = _steamProbe();
                if (!string.IsNullOrWhiteSpace(probe.EffectiveSoundPath) && File.Exists(probe.EffectiveSoundPath))
                {
                    string soundPath = probe.EffectiveSoundPath;
                    long writeTicks = File.GetLastWriteTimeUtc(soundPath).Ticks;
                    byte[] rawBytes = File.ReadAllBytes(soundPath);
                    if (TryDecodeRiffWav(rawBytes, out DecodedStereoWav decoded))
                    {
                        _cachedSteamPath = soundPath;
                        _cachedSteamWriteTicks = writeTicks;
                        _cachedSteamClip = decoded;
                    }
                }
            }
            catch
            {
                _cachedSteamClip = null;
                _cachedSteamPath = null;
                _cachedSteamWriteTicks = 0;
            }

            return _cachedSteamClip;
        }
    }

    private DecodedStereoWav? GetOrLoadCustomWavClip(string? customPath)
    {
        if (string.IsNullOrWhiteSpace(customPath))
        {
            return null;
        }

        try
        {
            string normalized = Path.GetFullPath(customPath.Trim());
            if (!File.Exists(normalized))
            {
                return null;
            }

            var fileInfo = new FileInfo(normalized);
            if (fileInfo.Length is <= 44 or > 20 * 1024 * 1024)
            {
                return null;
            }

            long writeTicks = fileInfo.LastWriteTimeUtc.Ticks;

            lock (_cacheLock)
            {
                if (_cachedCustomClip is not null
                    && string.Equals(_cachedCustomPath, normalized, StringComparison.OrdinalIgnoreCase)
                    && _cachedCustomWriteTicks == writeTicks)
                {
                    return _cachedCustomClip;
                }

                byte[] rawBytes = File.ReadAllBytes(normalized);
                if (TryDecodeRiffWav(rawBytes, out DecodedStereoWav decoded))
                {
                    _cachedCustomPath = normalized;
                    _cachedCustomWriteTicks = writeTicks;
                    _cachedCustomClip = decoded;
                    return decoded;
                }
            }
        }
        catch
        {
            // Fall back to synthesized chime on unreadable or malformed custom WAV.
        }

        return null;
    }

    public static ChimePreset ResolveChimePreset(OverlayToastRequest request) =>
        request.IsPlatinumCompletion
            ? ChimePreset.PlatinumCelebration
            : request.IsRare || request.GlobalPercent is < 10.0
                ? ChimePreset.RareDiamond
                : request.Theme == OverlayThemeKind.Ps5Trophy
                    ? ChimePreset.Ps5Trophy
                    : ChimePreset.SteamCrystal;

    private static float DecodeSingleSample(ReadOnlySpan<byte> sampleSlice, ushort formatTag, ushort bitsPerSample)
    {
        if (formatTag == 3)
        {
            return bitsPerSample == 64
                ? (float)BinaryPrimitives.ReadDoubleLittleEndian(sampleSlice)
                : BinaryPrimitives.ReadSingleLittleEndian(sampleSlice);
        }

        return bitsPerSample switch
        {
            8 => (sampleSlice[0] - 128) / 128f,
            16 => BinaryPrimitives.ReadInt16LittleEndian(sampleSlice) / 32768f,
            24 => ((sampleSlice[0] | (sampleSlice[1] << 8) | ((sbyte)sampleSlice[2] << 16))) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(sampleSlice) / 2147483648f,
            _ => 0f,
        };
    }

    private static double InterpolateCatmullRom(double y0, double y1, double y2, double y3, double frac)
    {
        double c0 = y1;
        double c1 = 0.5 * (y2 - y0);
        double c2 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
        double c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
        return ((c3 * frac + c2) * frac + c1) * frac + c0;
    }

    private static void WriteChannelFrame(float[] buffer, int frameIndex, int channels, float left, float right)
    {
        int baseIdx = frameIndex * channels;
        if (channels == 1)
        {
            buffer[baseIdx] = (left + right) * 0.5f;
            return;
        }

        buffer[baseIdx] = left;
        buffer[baseIdx + 1] = right;
        for (int ch = 2; ch < channels; ch++)
        {
            buffer[baseIdx + ch] = 0f;
        }
    }

    private static short QuantizeLinearPcm16(float value)
    {
        float clamped = Math.Clamp(value, -1f, 1f);
        int quantized = (int)Math.Round(clamped * 32767.0f);
        return (short)Math.Clamp(quantized, short.MinValue, short.MaxValue);
    }

    private static NoteVoice[] GetPresetVoices(ChimePreset preset) => preset switch
    {
        ChimePreset.PlatinumCelebration =>
        [
            new(523.25, 0.00, 0.85, 0.28, -0.25),
            new(659.25, 0.09, 0.85, 0.26, 0.25),
            new(783.99, 0.18, 0.90, 0.28, -0.15),
            new(987.77, 0.27, 0.95, 0.25, 0.20),
            new(1174.66, 0.36, 1.05, 0.26, -0.10),
            new(1567.98, 0.46, 1.15, 0.32, 0.00),
        ],
        ChimePreset.RareDiamond =>
        [
            new(293.66, 0.00, 0.95, 0.24, 0.00),
            new(587.33, 0.05, 0.90, 0.25, -0.30),
            new(739.99, 0.14, 0.92, 0.26, 0.30),
            new(880.00, 0.23, 0.95, 0.26, -0.20),
            new(1108.73, 0.33, 1.05, 0.30, 0.15),
            new(1318.51, 0.42, 1.10, 0.24, 0.00),
        ],
        ChimePreset.Ps5Trophy =>
        [
            new(587.33, 0.00, 0.55, 0.28, -0.15),
            new(880.00, 0.08, 0.65, 0.32, 0.15),
            new(1174.66, 0.17, 0.80, 0.34, 0.00),
        ],
        _ =>
        [
            new(523.25, 0.00, 0.48, 0.28, -0.20),
            new(659.25, 0.07, 0.52, 0.28, 0.20),
            new(783.99, 0.14, 0.60, 0.30, -0.10),
            new(1174.66, 0.21, 0.72, 0.30, 0.10),
        ],
    };

    private static double ComputeTotalDurationSec(ReadOnlySpan<NoteVoice> voices)
    {
        double total = 0.2;
        foreach (var v in voices)
        {
            total = Math.Max(total, v.StartSec + v.DurationSec + 0.05);
        }

        return total;
    }

    private static (double Left, double Right) SampleVoicesStereo(ReadOnlySpan<NoteVoice> voices, double t)
    {
        double left = 0.0;
        double right = 0.0;

        foreach (var voice in voices)
        {
            double localT = t - voice.StartSec;
            if (localT < 0.0 || localT > voice.DurationSec)
            {
                continue;
            }

            double attack = localT < 0.008 ? 0.5 * (1.0 - Math.Cos(Math.PI * localT / 0.008)) : 1.0;
            double decay = Math.Exp(-4.6 * (localT / voice.DurationSec));
            double env = attack * decay * voice.Amplitude;

            double phase = 2.0 * Math.PI * voice.FrequencyHz * localT;
            double wave = Math.Sin(phase)
                          + 0.22 * Math.Sin(phase * 2.0) * Math.Exp(-6.0 * (localT / voice.DurationSec))
                          + 0.07 * Math.Sin(phase * 3.0) * Math.Exp(-9.0 * (localT / voice.DurationSec));

            double sample = wave * env;
            double panAngle = (voice.Pan + 1.0) * (Math.PI * 0.25);
            left += sample * Math.Cos(panAngle);
            right += sample * Math.Sin(panAngle);
        }

        return (left, right);
    }

    private static short FloatToPcm16(double value)
    {
        double limited = Math.Tanh(value);
        int quantized = (int)Math.Round(limited * 32760.0);
        return (short)Math.Clamp(quantized, short.MinValue, short.MaxValue);
    }

    private static void WriteWavHeader(Span<byte> header, int sampleRate, short channels, short bitsPerSample, int dataBytes)
    {
        "RIFF"u8.CopyTo(header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(4, 4), 36 + dataBytes);
        "WAVE"u8.CopyTo(header.Slice(8, 4));
        "fmt "u8.CopyTo(header.Slice(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), sampleRate);
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), byteRate);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.Slice(34, 2), bitsPerSample);
        "data"u8.CopyTo(header.Slice(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(40, 4), dataBytes);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>
    /// <see cref="IWaveProvider"/> that preserves the exact <see cref="WaveFormat"/> instance from
    /// <c>AudioClient.MixFormat</c> (including <see cref="WaveFormatExtensible"/> and <c>dwChannelMask</c>)
    /// instead of stripping channel masks via <c>SampleToWaveProvider</c> (<c>0x0003</c>), copying float bytes aligned to <c>BlockAlign</c>.
    /// </summary>
    public sealed class ExactMixFormatFloatWaveProvider : IWaveProvider
    {
        private readonly float[] _interleavedSamples;
        private readonly int _blockAlign;
        private int _bytePosition;

        public ExactMixFormatFloatWaveProvider(WaveFormat exactMixFormat, float[] interleavedSamples)
        {
            ArgumentNullException.ThrowIfNull(exactMixFormat);
            ArgumentNullException.ThrowIfNull(interleavedSamples);

            WaveFormat = exactMixFormat;
            _interleavedSamples = interleavedSamples;
            _blockAlign = Math.Max(sizeof(float), exactMixFormat.BlockAlign);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count <= 0)
            {
                return 0;
            }

            ReadOnlySpan<byte> allBytes = MemoryMarshal.AsBytes(_interleavedSamples.AsSpan());
            int available = allBytes.Length - _bytePosition;
            if (available < _blockAlign)
            {
                return 0;
            }

            int requested = Math.Min(available, count);
            int aligned = (requested / _blockAlign) * _blockAlign;
            if (aligned <= 0)
            {
                return 0;
            }

            allBytes.Slice(_bytePosition, aligned).CopyTo(buffer.AsSpan(offset, aligned));
            _bytePosition += aligned;
            return aligned;
        }
    }

    public sealed record WavValidationResult(
        bool IsValid,
        string Message,
        int SampleRate = 0,
        int Channels = 0,
        int BitsPerSample = 0,
        double DurationSeconds = 0.0);

    public readonly record struct ResolvedSoundPlan(
        bool UseExternalClip,
        ChimePreset SynthesizedPreset,
        bool IsEnhancedRareOrPlatinum);

    public readonly record struct DecodedStereoWav(
        int SampleRate,
        int FrameCount,
        float[] InterleavedStereoSamples);

    public enum ChimePreset
    {
        SteamCrystal = 0,
        Ps5Trophy = 1,
        RareDiamond = 2,
        PlatinumCelebration = 3,
    }

    private readonly record struct NoteVoice(
        double FrequencyHz,
        double StartSec,
        double DurationSec,
        double Amplitude,
        double Pan);

    private sealed class BufferedFloatSampleProvider(WaveFormat waveFormat, float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = samples.Length - _position;
            if (available <= 0)
            {
                return 0;
            }

            int toCopy = Math.Min(available, count);
            Array.Copy(samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(nint pszSound, nint hmod, uint fdwSound);
}
