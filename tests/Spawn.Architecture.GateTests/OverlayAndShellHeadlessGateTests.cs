using System.Buffers.Binary;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using NAudio.Wave;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;
using Spawn.OverlayHost.Audio;
using Spawn.OverlayHost.Services;
using Spawn.OverlayHost.Views;
using Xunit;

namespace Spawn.Architecture.GateTests;

/// <summary>
/// Headless zero-window, zero-speaker verification of <c>Spawn.OverlayHost</c> queue coordination,
/// STA <see cref="RenderTargetBitmap"/> non-transparent pixel rendering across all 3 themes,
/// XAML <c>&lt;Popup&gt;</c> cross-tree binding hygiene, and in-memory RIFF PCM WAV synthesis.
/// </summary>
public sealed class OverlayAndShellHeadlessGateTests
{
    [Fact]
    public async Task OverlayToastQueueCoordinator_Ps5FinalCompletion_AppendsSyntheticPlatinumCelebrationCard()
    {
        var delivered = new List<OverlayToastRequest>();
        var twoDeliveredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new OverlayToastQueueCoordinator((toast, _) =>
        {
            lock (delivered)
            {
                delivered.Add(toast);
                if (delivered.Count >= 2)
                {
                    twoDeliveredTcs.TrySetResult(true);
                }
            }

            return Task.CompletedTask;
        });

        coordinator.Enqueue(new OverlayToastRequest
        {
            GameId = "limbo-id",
            GameTitle = "LIMBO",
            AchievementApiName = "ACH_13",
            DisplayName = "叮当声！",
            Description = "逃跑是没用的",
            Theme = OverlayThemeKind.Ps5Trophy,
            IsPlatinumCompletion = true,
            MuteSound = true,
            SoundVolume = 0.0,
            DurationMs = 100,
        });

        await twoDeliveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

        lock (delivered)
        {
            Assert.Equal(2, delivered.Count);
            Assert.Equal("ACH_13", delivered[0].AchievementApiName);
            Assert.False(delivered[0].IsPlatinumCompletion);

            Assert.Equal(OverlayToastQueueCoordinator.SyntheticPlatinumApiName, delivered[1].AchievementApiName);
            Assert.True(delivered[1].IsPlatinumCompletion);
            Assert.Contains("白金奖杯", delivered[1].DisplayName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OverlayToastWindow_StaHeadlessRenderAndShowLifecycle_RendersNonTransparentPixelsAcrossAllThemes()
    {
        // 1. Static source guard: PlayToastAsync must call WPF Window.Show() and Hide() so UpdateLayeredWindow commits pixels!
        string repoRoot = LocateRepositoryRoot();
        string windowCsPath = Path.Combine(repoRoot, "src", "Spawn.OverlayHost", "Views", "OverlayToastWindow.xaml.cs");
        Assert.True(File.Exists(windowCsPath));
        string windowSource = File.ReadAllText(windowCsPath, Encoding.UTF8);
        Assert.Matches(new Regex(@"\bShow\s*\(\s*\)"), windowSource);
        Assert.Matches(new Regex(@"\bHide\s*\(\s*\)"), windowSource);

        // 2. Headless STA visual tree & RenderTargetBitmap pixel verification (zero desktop window shown!)
        Exception? staException = null;
        var staThread = new Thread(() =>
        {
            try
            {
                using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
                var window = new OverlayToastWindow(silentPlayer);
                try
                {
                    Assert.False(window.IsVisible);

                    (OverlayThemeKind Theme, bool IsPlatinum, bool IsRare)[] variants =
                    [
                        (OverlayThemeKind.Steam, false, true),
                        (OverlayThemeKind.Ps5Trophy, false, false),
                        (OverlayThemeKind.Ps5Trophy, true, true),
                        (OverlayThemeKind.XboxRare, false, true),
                    ];

                    const int renderW = 388;
                    const int renderH = 96;

                    foreach (var (theme, isPlatinum, isRare) in variants)
                    {
                        window.PrepareForHeadlessRender(new OverlayToastRequest
                        {
                            GameId = "headless-render-check",
                            GameTitle = "LIMBO",
                            AchievementApiName = "ACH_RENDER_CHECK",
                            DisplayName = "全渠道实时跳杯测试",
                            Description = "验证分层窗口卡片非全透明像素渲染",
                            Theme = theme,
                            Position = OverlayPositionKind.BottomRight,
                            IsPlatinumCompletion = isPlatinum,
                            IsRare = isRare,
                            GlobalPercent = isRare ? 3.5 : 42.0,
                            MuteSound = true,
                            SoundVolume = 0.0,
                        });

                        FrameworkElement stage = window.VisualStage;
                        stage.Measure(new Size(renderW, renderH));
                        stage.Arrange(new Rect(0, 0, renderW, renderH));
                        stage.UpdateLayout();

                        Assert.True(stage.ActualWidth >= 320, $"[{theme}] Expected ActualWidth >= 320, got {stage.ActualWidth}");
                        Assert.True(stage.ActualHeight >= 70, $"[{theme}] Expected ActualHeight >= 70, got {stage.ActualHeight}");

                        var rtb = new RenderTargetBitmap(renderW, renderH, 96.0, 96.0, PixelFormats.Pbgra32);
                        rtb.Render(stage);

                        byte[] pixels = new byte[renderW * renderH * 4];
                        rtb.CopyPixels(pixels, renderW * 4, 0);

                        int nonTransparentPixels = 0;
                        for (int idx = 3; idx < pixels.Length; idx += 4)
                        {
                            if (pixels[idx] > 0)
                            {
                                nonTransparentPixels++;
                            }
                        }

                        Assert.True(
                            nonTransparentPixels > 1000,
                            $"[{theme}/Platinum={isPlatinum}] Expected > 1000 non-transparent pixels (Alpha > 0), actual: {nonTransparentPixels}");
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                staException = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        Assert.True(staThread.Join(TimeSpan.FromSeconds(15)), "STA headless render test timed out.");

        if (staException is not null)
        {
            ExceptionDispatchInfo.Capture(staException).Throw();
        }
    }

    [Fact]
    public void XamlPopupDataContextAudit_AllHostAndOverlayXamlFiles_NeverUseBrokenAncestorTypeWindowInsidePopup()
    {
        string repoRoot = LocateRepositoryRoot();
        string[] xamlRoots =
        [
            Path.Combine(repoRoot, "src", "Spawn.Host"),
            Path.Combine(repoRoot, "src", "Spawn.OverlayHost"),
        ];

        var brokenAncestorPattern = new Regex(
            @"RelativeSource[\s\S]*?AncestorType\s*=\s*(?:\{\s*x:Type\s+)?Window\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int popupCount = 0;
        var violations = new List<string>();

        foreach (string root in xamlRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (string xamlFile in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            {
                string norm = xamlFile.Replace('\\', '/');
                if (norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    || norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relPath = Path.GetRelativePath(repoRoot, xamlFile).Replace('\\', '/');
                XDocument doc = XDocument.Load(xamlFile, LoadOptions.SetLineInfo);

                foreach (XElement popup in doc.Descendants().Where(e => e.Name.LocalName == "Popup"))
                {
                    popupCount++;
                    foreach (XElement node in popup.DescendantsAndSelf())
                    {
                        foreach (XAttribute attr in node.Attributes())
                        {
                            if (brokenAncestorPattern.IsMatch(attr.Value))
                            {
                                int line = (attr as System.Xml.IXmlLineInfo)?.LineNumber ?? 0;
                                violations.Add(
                                    $"{relPath}:{line} <Popup> subtree uses broken AncestorType=Window binding ({node.Name.LocalName}.{attr.Name.LocalName}=\"{attr.Value}\"). Use PlacementTarget.Tag or x:Reference instead.");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(popupCount >= 2, $"Expected at least 2 <Popup> elements in MainWindow.xaml, found {popupCount}.");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.SteamCrystal)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.Ps5Trophy)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.RareDiamond)]
    [InlineData(SynthesizedUnlockSoundPlayer.ChimePreset.PlatinumCelebration)]
    public void SynthesizedUnlockSoundPlayer_InMemoryWavSynthesisAndSilentGuard_ProducesValidRiffPcmWithoutAudioOutput(
        SynthesizedUnlockSoundPlayer.ChimePreset preset)
    {
        byte[] wav = SynthesizedUnlockSoundPlayer.SynthesizeWavBytes(preset, masterVolume: 0.75f);
        Assert.True(wav.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        short audioFormat = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2));
        short channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4));
        Assert.Equal(1, audioFormat);
        Assert.Equal(2, channels);
        Assert.Equal(48000, sampleRate);

        // Verify forceSilent=true never touches audio hardware
        using var silentPlayer = new SynthesizedUnlockSoundPlayer(forceSilent: true);
        silentPlayer.PlayForToast(new OverlayToastRequest
        {
            GameTitle = "LIMBO",
            AchievementApiName = "ACH_1",
            DisplayName = "方向错误",
            SoundPreset = OverlaySoundPreset.SteamAuthentic,
            MuteSound = true,
            SoundVolume = 0.0,
        });
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_RiffWav44100Hz16BitStereoResampleAndScaleTo48000HzIeeeFloat_PreservesFrameCountAmplitudeAndStereoSeparationInMemory()
    {
        // 1. Construct a standard 44,100 Hz, 16-bit, Stereo RIFF PCM WAV in memory (0.20s = 8,820 frames; L=440Hz@0.80, R=880Hz@0.40)
        const int srcRate = 44100;
        const int srcFrames = 8820;
        const int dstRate = 48000;
        byte[] wav44k = BuildSyntheticStereoPcm16WavWithJunkChunk(srcRate, srcFrames, leftAmp: 0.80, rightAmp: 0.40);

        Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(wav44k, out var decoded));
        Assert.Equal(srcRate, decoded.SampleRate);
        Assert.Equal(srcFrames, decoded.FrameCount);
        Assert.Equal(srcFrames * 2, decoded.InterleavedStereoSamples.Length);

        // 2. Resample 44,100 Hz -> 48,000 Hz IEEE Float with 0.50 volume scale (expected: 9,600 stereo frames = 19,200 floats)
        float[] resampled48k = SynthesizedUnlockSoundPlayer.ResampleAndScaleWavToIeeeFloat(
            wav44k,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2);

        int expectedTargetFrames = (int)Math.Round((double)srcFrames * dstRate / srcRate);
        Assert.Equal(9600, expectedTargetFrames);
        Assert.Equal(expectedTargetFrames * 2, resampled48k.Length);

        float peakLeft = 0f;
        float peakRight = 0f;
        int nonSilentCount = 0;

        for (int frame = 0; frame < expectedTargetFrames; frame++)
        {
            float l = resampled48k[frame * 2];
            float r = resampled48k[frame * 2 + 1];
            Assert.False(float.IsNaN(l) || float.IsInfinity(l));
            Assert.False(float.IsNaN(r) || float.IsInfinity(r));

            float absL = Math.Abs(l);
            float absR = Math.Abs(r);
            if (absL > peakLeft)
            {
                peakLeft = absL;
            }

            if (absR > peakRight)
            {
                peakRight = absR;
            }

            if (absL > 0.001f || absR > 0.001f)
            {
                nonSilentCount++;
            }
        }

        // Original L=0.80 * 0.50 gain -> ~0.40; Original R=0.40 * 0.50 gain -> ~0.20
        Assert.InRange(peakLeft, 0.385f, 0.415f);
        Assert.InRange(peakRight, 0.188f, 0.212f);
        Assert.True(nonSilentCount > 8500, $"Expected non-silent resampled frames > 8500, actual: {nonSilentCount}");

        // Zero gain produces pure silence of exact resampled length
        float[] muted48k = SynthesizedUnlockSoundPlayer.ResampleStereoFrames(decoded, volumeScale: 0.0f, targetSampleRate: dstRate);
        Assert.Equal(expectedTargetFrames * 2, muted48k.Length);
        Assert.All(muted48k, s => Assert.Equal(0f, s));

        // 3. Verify 16-bit PCM fallback scaler (`PlaySoundW` memory buffer) preserves RIFF header and scaled amplitude
        byte[] scaledPcm16 = SynthesizedUnlockSoundPlayer.ScaleDecodedWavToPcm16Bytes(decoded, volumeScale: 0.50f);
        Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(scaledPcm16, out var reDecoded));
        Assert.Equal(srcRate, reDecoded.SampleRate);
        Assert.Equal(srcFrames, reDecoded.FrameCount);
        Assert.InRange(reDecoded.InterleavedStereoSamples.Max(Math.Abs), 0.39f, 0.41f);

        // 4. Read-only in-memory verification against local Steam client WAV if installed on this machine (0 audio output!)
        var probe = Spawn.Core.SteamClientSoundLocator.Probe();
        if (!string.IsNullOrWhiteSpace(probe.EffectiveSoundPath) && File.Exists(probe.EffectiveSoundPath))
        {
            byte[] realSteamWav = File.ReadAllBytes(probe.EffectiveSoundPath);
            Assert.True(SynthesizedUnlockSoundPlayer.TryDecodeRiffWav(realSteamWav, out var realDecoded));
            Assert.True(realDecoded.FrameCount > 1000);
            float[] realResampled = SynthesizedUnlockSoundPlayer.ResampleStereoFrames(realDecoded, volumeScale: 0.65f, targetSampleRate: 48000);
            Assert.True(realResampled.Length > 2000);
            Assert.Contains(realResampled, s => Math.Abs(s) > 0.01f);
        }
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_GentlePeakNormalizationAndGuardSilencePadding_BoostsMinus10DbfsClipAndWrapsLeadInTailZerosInMemory()
    {
        const int srcRate = 44100;
        const int srcFrames = 8820; // 0.20s
        const int dstRate = 48000;
        const float steamLikePeak = 0.313f; // ~ -10 dBFS (Steam deck_ui_achievement_toast.wav raw peak)

        var quietClip = BuildDecodedStereoSine(srcRate, srcFrames, steamLikePeak, steamLikePeak);

        // 1. NormalizeGentlePeak: peak inside [0.05, 0.65] is boosted linearly to 0.85 without clipping, frame count preserved
        var boosted = SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(quietClip);
        Assert.Equal(srcRate, boosted.SampleRate);
        Assert.Equal(srcFrames, boosted.FrameCount);
        float boostedPeak = boosted.InterleavedStereoSamples.Max(MathF.Abs);
        Assert.InRange(boostedPeak, 0.845f, 0.851f);
        Assert.All(boosted.InterleavedStereoSamples, s => Assert.InRange(s, -1f, 1f));

        // Ratio between boosted and original samples is uniform (pure linear gain, no waveshaping)
        float expectedGain = boostedPeak / quietClip.InterleavedStereoSamples.Max(MathF.Abs);
        for (int i = 0; i < 64; i++)
        {
            Assert.Equal(quietClip.InterleavedStereoSamples[i] * expectedGain, boosted.InterleavedStereoSamples[i], 4);
        }

        // Too-quiet (< 0.05) and already-loud (> 0.65) clips must stay untouched
        var nearSilentClip = BuildDecodedStereoSine(srcRate, 2000, 0.02f, 0.02f);
        var loudClip = BuildDecodedStereoSine(srcRate, 2000, 0.90f, 0.90f);
        Assert.Equal(
            nearSilentClip.InterleavedStereoSamples.Max(MathF.Abs),
            SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(nearSilentClip).InterleavedStereoSamples.Max(MathF.Abs));
        Assert.Equal(
            loudClip.InterleavedStereoSamples.Max(MathF.Abs),
            SynthesizedUnlockSoundPlayer.NormalizeGentlePeak(loudClip).InterleavedStereoSamples.Max(MathF.Abs));

        // 2. PadWithGuardSilence: 45ms lead-in + 160ms tail of exact zero frames, payload copied byte-for-byte in the middle
        float[] payload = [0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f];
        float[] padded = SynthesizedUnlockSoundPlayer.PadWithGuardSilence(payload, dstRate, channels: 2);
        int leadSamples = (int)Math.Round(dstRate * 0.045) * 2; // 2160 frames * 2 = 4320
        int tailSamples = (int)Math.Round(dstRate * 0.160) * 2; // 7680 frames * 2 = 15360
        Assert.Equal(4320, leadSamples);
        Assert.Equal(15360, tailSamples);
        Assert.Equal(leadSamples + payload.Length + tailSamples, padded.Length);
        Assert.All(padded.AsSpan(0, leadSamples).ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(payload, padded.AsSpan(leadSamples, payload.Length).ToArray());
        Assert.All(padded.AsSpan(leadSamples + payload.Length).ToArray(), s => Assert.Equal(0f, s));

        // Zero padding requested returns an identical copy
        Assert.Equal(payload, SynthesizedUnlockSoundPlayer.PadWithGuardSilence(payload, dstRate, 2, leadInSilenceMs: 0, tailSilenceMs: 0));

        // 3. Full PrepareExternalClipForWasapiPlayback pipeline: normalize -> resample 44.1k->48k -> x0.50 volume -> guard silence
        float[] prepared = SynthesizedUnlockSoundPlayer.PrepareExternalClipForWasapiPlayback(
            quietClip,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2);

        int resampledSamples = 9600 * 2;
        Assert.Equal(leadSamples + resampledSamples + tailSamples, prepared.Length);
        Assert.All(prepared.AsSpan(0, leadSamples).ToArray(), s => Assert.Equal(0f, s));
        Assert.All(prepared.AsSpan(leadSamples + resampledSamples).ToArray(), s => Assert.Equal(0f, s));

        float[] body = prepared.AsSpan(leadSamples, resampledSamples).ToArray();
        Assert.All(body, s => Assert.False(float.IsNaN(s) || float.IsInfinity(s)));
        float bodyPeak = body.Max(MathF.Abs);
        // 0.85 normalized target x 0.50 user volume ~= 0.425 (Catmull-Rom tolerance)
        Assert.InRange(bodyPeak, 0.41f, 0.44f);
        Assert.True(body.Count(s => MathF.Abs(s) > 0.001f) > 15000);

        // Normalization disabled keeps original quiet level (0.313 x 0.50 ~= 0.157)
        float[] unnormalized = SynthesizedUnlockSoundPlayer.PrepareExternalClipForWasapiPlayback(
            quietClip,
            volumeScale: 0.50f,
            targetSampleRate: dstRate,
            targetChannels: 2,
            normalizeGentlePeak: false);
        Assert.InRange(unnormalized.AsSpan(leadSamples, resampledSamples).ToArray().Max(MathF.Abs), 0.150f, 0.165f);
    }

    [Fact]
    public void SynthesizedUnlockSoundPlayer_SmartEndpointClassification_BypassesSpeakerlessGpuDisplayAndPrefersGameHeadset()
    {
        const string gpuMonitor = "XG27UCS (NVIDIA High Definition Audio)";
        const string gameHeadset = "耳机 (INZONE H9 / INZONE H7 - Game)";
        const string chatHeadset = "耳机 (INZONE H9 / INZONE H7 - Chat)";
        const string realtekSpeakers = "扬声器 (Realtek(R) Audio)";
        const string spdifOut = "数字输出 (Realtek(R) Audio)";

        // 1. GPU display audio channels are detected (NVIDIA / AMD / Intel)
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(gpuMonitor));
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint("DELL U2723QE (AMD High Definition Audio Device)"));
        Assert.True(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint("LG HDR 4K (Intel(R) Display Audio)"));
        Assert.False(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(gameHeadset));
        Assert.False(SynthesizedUnlockSoundPlayer.IsLikelyGpuDisplayAudioEndpoint(realtekSpeakers));

        // 2. Narrowband chat sub-endpoints and S/PDIF optical outputs are excluded from auto selection
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(chatHeadset));
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(spdifOut));
        Assert.True(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint("Headset (Hands-Free AG Audio)"));
        Assert.False(SynthesizedUnlockSoundPlayer.IsExcludedAuxiliaryOrOpticalEndpoint(gameHeadset));

        // 3. Scoring ranks game headset > speakers > generic, and disqualifies GPU / chat / optical endpoints
        int headsetScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(gameHeadset);
        int speakerScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(realtekSpeakers);
        int genericScore = SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint("Virtual Audio Cable");

        Assert.True(headsetScore > speakerScore, $"Game headset ({headsetScore}) must outrank speakers ({speakerScore}).");
        Assert.True(speakerScore > genericScore, $"Speakers ({speakerScore}) must outrank generic ({genericScore}).");
        Assert.True(genericScore >= 0);
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(gpuMonitor));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(chatHeadset));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(spdifOut));
        Assert.Equal(-1, SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(null));

        // 4. Simulated reproduction of the user's machine: default = speakerless NVIDIA monitor -> best candidate is the Game headset
        string[] activeEndpoints = [gpuMonitor, chatHeadset, spdifOut, gameHeadset, realtekSpeakers];
        string best = activeEndpoints
            .Select(name => (Name: name, Score: SynthesizedUnlockSoundPlayer.ScorePhysicalAudioEndpoint(name)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .First().Name;
        Assert.Equal(gameHeadset, best);
    }

    [Fact]
    public async Task WasapiExtensibleMixFormatWakePrimerAndSystemDefaultRouting_PreservesChannelMaskZeroCrossingPrimerAndDefaultMode()
    {
        // 1. Is32BitIeeeFloatWaveFormat recognizes both plain 0x0003 IeeeFloat and 0xFFFE WaveFormatExtensible (KSDATAFORMAT_SUBTYPE_IEEE_FLOAT),
        //    while rejecting 16-bit / 32-bit integer PCM WaveFormat / WaveFormatExtensible.
        var plainFloat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var extensibleFloat = new WaveFormatExtensible(48000, 32, 2);
        var pcm16 = new WaveFormat(48000, 16, 2);
        var pcm24Extensible = new WaveFormatExtensible(48000, 24, 2);

        Assert.True(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(plainFloat));
        Assert.True(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(extensibleFloat));
        Assert.False(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(pcm16));
        Assert.False(SynthesizedUnlockSoundPlayer.Is32BitIeeeFloatWaveFormat(pcm24Extensible));

        // 2. ExactMixFormatFloatWaveProvider preserves exact WaveFormatExtensible instance reference (retaining 0xFFFE + dwChannelMask)
        //    and reads strictly in BlockAlign (8-byte stereo float frame) multiples until EOF returns 0.
        float[] sourceSamples = [0.125f, -0.125f, 0.25f, -0.25f, 0.5f, -0.5f]; // 3 stereo frames = 24 bytes
        var provider = new SynthesizedUnlockSoundPlayer.ExactMixFormatFloatWaveProvider(extensibleFloat, sourceSamples);
        Assert.Same(extensibleFloat, provider.WaveFormat);
        Assert.Equal(WaveFormatEncoding.Extensible, provider.WaveFormat.Encoding);
        Assert.Equal(8, provider.WaveFormat.BlockAlign);

        byte[] readAccum = new byte[24];
        // Request unaligned 11 bytes -> must return exact BlockAlign multiple (8 bytes = 1 stereo frame)
        int n1 = provider.Read(readAccum, offset: 0, count: 11);
        Assert.Equal(8, n1);
        // Request remaining -> returns remaining 16 bytes
        int n2 = provider.Read(readAccum, offset: 8, count: 64);
        Assert.Equal(16, n2);
        // Subsequent read at EOF -> returns 0
        int n3 = provider.Read(readAccum, offset: 0, count: 16);
        Assert.Equal(0, n3);

        float[] roundTripped = MemoryMarshal.Cast<byte, float>(readAccum.AsSpan()).ToArray();
        Assert.Equal(sourceSamples, roundTripped);

        // 3. PrependSubAudibleWakePrimerAndTailSilence generates 160ms sub-audible Hann-windowed 35Hz zero-crossing wake primer
        //    (0 < maxAbs <= 0.001f, smooth start/end without DC jump), preserves exact middle payload, and appends 240ms zero tail.
        const int rate = 48000;
        const int channels = 2;
        float[] bodyPayload = [0.42f, -0.42f, 0.84f, -0.84f, 0.21f, -0.21f, -0.63f, 0.63f];
        float[] wakePrimed = SynthesizedUnlockSoundPlayer.PrependSubAudibleWakePrimerAndTailSilence(
            bodyPayload,
            sampleRate: rate,
            channels: channels,
            wakePrimerMs: 160,
            tailSilenceMs: 240,
            primerAmplitude: 0.00045f);

        int primerSamples = (int)Math.Round(rate * 0.160) * channels; // 7680 * 2 = 15360
        int tailSamples = (int)Math.Round(rate * 0.240) * channels;   // 11520 * 2 = 23040
        Assert.Equal(15360, primerSamples);
        Assert.Equal(23040, tailSamples);
        Assert.Equal(primerSamples + bodyPayload.Length + tailSamples, wakePrimed.Length);

        ReadOnlySpan<float> primerSpan = wakePrimed.AsSpan(0, primerSamples);
        float maxPrimerAbs = 0f;
        for (int i = 0; i < primerSpan.Length; i++)
        {
            float a = MathF.Abs(primerSpan[i]);
            if (a > maxPrimerAbs)
            {
                maxPrimerAbs = a;
            }
        }

        Assert.True(maxPrimerAbs > 0f && maxPrimerAbs <= 0.001f, $"Expected 0 < maxPrimerAbs <= 0.001f, got {maxPrimerAbs}");
        Assert.Equal(0f, primerSpan[0], precision: 6);
        Assert.Equal(0f, primerSpan[1], precision: 6);
        Assert.True(MathF.Abs(primerSpan[^2]) < 1e-5f, $"Expected smooth zero-crossing end on primer L, got {primerSpan[^2]}");
        Assert.True(MathF.Abs(primerSpan[^1]) < 1e-5f, $"Expected smooth zero-crossing end on primer R, got {primerSpan[^1]}");

        Assert.Equal(bodyPayload, wakePrimed.AsSpan(primerSamples, bodyPayload.Length).ToArray());
        Assert.All(wakePrimed.AsSpan(primerSamples + bodyPayload.Length).ToArray(), s => Assert.Equal(0f, s));

        // 4. SettingsViewModel defaults to OverlayAudioOutputModes.SystemDefault and normalizes legacy "auto-smart" DB rows to SystemDefault!
        string tempDir = Path.Combine(Path.GetTempPath(), $"spawn-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = SpawnPaths.CreateIsolated(tempDir, workspaceRootHint: tempDir);
            paths.EnsureDirectoriesExist();
            await using var db = new SpawnDatabase(paths);
            await db.InitializeAsync();

            // Persist a legacy "auto-smart" row to verify smooth migration back to SystemDefault on InitializeAsync
            await db.SaveOverlaySettingsAsync(new OverlaySettingsDto
            {
                AudioOutputDeviceId = OverlayAudioOutputModes.AutoSmart,
                MuteSound = true,
                SoundVolume = 0.0,
            });

            var keyProvider = new SteamApiKeyProvider(paths, allowLocalDevFallback: false);
            await using var ipcClient = new OverlayIpcClient(paths);
            using var guard = new AntiCheatGuardService();
            var themeMgr = new ThemeManagerService();

            var vm = new SettingsViewModel(paths, db, keyProvider, ipcClient, guard, themeMgr);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.SelectedAudioOutputDeviceId);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.AudioOutputDeviceOptions[0].Value);
            Assert.DoesNotContain(vm.AudioOutputDeviceOptions, o => o.Label.Contains("绕开", StringComparison.Ordinal));

            await vm.InitializeAsync();
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.SelectedAudioOutputDeviceId);
            Assert.Equal(OverlayAudioOutputModes.SystemDefault, vm.AudioOutputDeviceOptions[0].Value);
        }
        finally
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static SynthesizedUnlockSoundPlayer.DecodedStereoWav BuildDecodedStereoSine(int sampleRate, int frameCount, float leftAmp, float rightAmp)
    {
        float[] interleaved = new float[frameCount * 2];
        for (int i = 0; i < frameCount; i++)
        {
            double t = (double)i / sampleRate;
            interleaved[i * 2] = (float)(Math.Sin(2.0 * Math.PI * 440.0 * t) * leftAmp);
            interleaved[i * 2 + 1] = (float)(Math.Sin(2.0 * Math.PI * 660.0 * t) * rightAmp);
        }

        return new SynthesizedUnlockSoundPlayer.DecodedStereoWav(sampleRate, frameCount, interleaved);
    }

    private static byte[] BuildSyntheticStereoPcm16WavWithJunkChunk(int sampleRate, int frameCount, double leftAmp, double rightAmp)
    {
        const int channels = 2;
        const int bitsPerSample = 16;
        const int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataBytes = frameCount * blockAlign;
        const int junkPayloadBytes = 12;

        // RIFF(12) + fmt (24) + JUNK(8 + 12) + data(8 + dataBytes)
        int totalLen = 12 + 24 + (8 + junkPayloadBytes) + (8 + dataBytes);
        byte[] buf = new byte[totalLen];
        Span<byte> span = buf.AsSpan();

        "RIFF"u8.CopyTo(span[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)(totalLen - 8));
        "WAVE"u8.CopyTo(span.Slice(8, 4));

        "fmt "u8.CopyTo(span.Slice(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 16u);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(34, 2), bitsPerSample);

        "JUNK"u8.CopyTo(span.Slice(36, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40, 4), junkPayloadBytes);

        int dataHeaderOffset = 44 + junkPayloadBytes;
        "data"u8.CopyTo(span.Slice(dataHeaderOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(dataHeaderOffset + 4, 4), (uint)dataBytes);

        Span<byte> pcm = span.Slice(dataHeaderOffset + 8, dataBytes);
        for (int i = 0; i < frameCount; i++)
        {
            double t = (double)i / sampleRate;
            short sL = (short)Math.Round(Math.Sin(2.0 * Math.PI * 440.0 * t) * leftAmp * 32767.0);
            short sR = (short)Math.Round(Math.Sin(2.0 * Math.PI * 880.0 * t) * rightAmp * 32767.0);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 4, 2), sL);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(i * 4 + 2, 2), sR);
        }

        return buf;
    }

    private static string LocateRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spawn.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing Spawn.sln.");
    }
}
