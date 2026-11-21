using System.Text;
using Spawn.Modules.Progress.Audio;
using Xunit;
using Xunit.Abstractions;

namespace Spawn.Modules.Progress.UnitTests.Audio;

public sealed class SilentMidiAndA0Tests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempRoot;

    public SilentMidiAndA0Tests(ITestOutputHelper output)
    {
        _output = output;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"spawn-w06-a0-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void SilentSmf_Exact27Bytes_MatchesSpecHeaderAndFiveSecondSilentTrack()
    {
        byte[] expected =
        [
            0x4D, 0x54, 0x68, 0x64, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x01, 0x00, 0x60,
            0x4D, 0x54, 0x72, 0x6B, 0x00, 0x00, 0x00, 0x05, 0x87, 0x40, 0xFF, 0x2F, 0x00,
        ];

        Assert.Equal(27, SilentMidiAssets.SilentSmfByteLength);
        Assert.Equal(27, SilentMidiAssets.SilentSmfBytes.Length);
        Assert.Equal(expected, SilentMidiAssets.SilentSmfBytes.ToArray());
        Assert.True(SilentMidiAssets.IsExactSilentSmf(expected));

        // Verify SMF header fields: "MThd", length 6, format 0, 1 track, 96 PPQ.
        Assert.Equal("MThd", Encoding.ASCII.GetString(expected, 0, 4));
        Assert.Equal(96, (expected[12] << 8) | expected[13]);

        // Verify track fields: "MTrk", length 5, VLQ 87 40 = 960 ticks (10 quarter notes = 5.0s @ 120 BPM), FF 2F 00 End-of-Track.
        Assert.Equal("MTrk", Encoding.ASCII.GetString(expected, 14, 4));
        int vlqTicks = ((expected[22] & 0x7F) << 7) | (expected[23] & 0x7F);
        Assert.Equal(960, vlqTicks);
        Assert.Equal(TimeSpan.FromSeconds(5.0), SilentMidiAssets.NominalDuration);
        Assert.Equal(new byte[] { 0xFF, 0x2F, 0x00 }, expected[24..27]);

        // Mutated copy must not pass IsExactSilentSmf.
        byte[] mutated = SilentMidiAssets.GetSilentSmfCopy();
        mutated[22] = 0x00;
        Assert.False(SilentMidiAssets.IsExactSilentSmf(mutated));
        Assert.True(SilentMidiAssets.IsExactSilentSmf(SilentMidiAssets.SilentSmfBytes));
    }

    [Fact]
    public void SilentSmf_MciSendStringPlaybackAndReplay_ZeroErrorsAndNoBusyWait()
    {
        string midiPath = Path.Combine(_tempRoot, "TrainerBGM.mid");
        File.WriteAllBytes(midiPath, SilentMidiAssets.GetSilentSmfCopy());

        SilentMidiMciVerificationResult result = SilentMidiMciVerifier.VerifyPlaybackAndReplay(midiPath, settleDelayMs: 60);

        if (result.SkippedNoMidiDevice)
        {
            _output.WriteLine($"[not-run] Skipping silent MCI SMF playback check: {result.DiagnosticMessage}");
            return;
        }

        _output.WriteLine(result.DiagnosticMessage ?? "MCI silent SMF verification completed.");
        Assert.True(result.Succeeded, result.DiagnosticMessage);
        Assert.Equal("playing", result.InitialMode, ignoreCase: true);
        Assert.Equal("playing", result.ReplayMode, ignoreCase: true);
    }

    [Fact]
    public void KnownDocumentsFolder_ReadOnlyResolve_UsesFolderIdDocumentsGuid()
    {
        Assert.Equal(
            new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
            WindowsTrainerAudioA0DirectoryResolver.FolderIdDocuments);

        // Read-only P/Invoke verification; never writes to real user Documents.
        string? resolved = WindowsTrainerAudioA0DirectoryResolver.TryResolveKnownDocumentsFolder();
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.True(Path.IsPathRooted(resolved!));
    }

    [Fact]
    public void DirectoryResolver_HandlesOneDriveRedirect_ExistingLegacyOnly_AndExcludesAppData()
    {
        string userProfile = Path.Combine(_tempRoot, "UserProfile");
        string oneDriveDocs = Path.Combine(_tempRoot, "OneDrive", "Documents");
        string localAppData = Path.Combine(_tempRoot, "LocalAppData");
        string legacyDocs = Path.Combine(userProfile, "Documents");
        string legacyFling = Path.Combine(legacyDocs, "FLiNGTrainer");

        Directory.CreateDirectory(oneDriveDocs);
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(legacyDocs);

        var resolver = new WindowsTrainerAudioA0DirectoryResolver(
            () => oneDriveDocs,
            () => userProfile,
            () => localAppData);

        // Legacy %USERPROFILE%\Documents\FLiNGTrainer does NOT exist yet -> only 2 targets.
        IReadOnlyList<string> withoutLegacy = resolver.ResolveTargetDirectories();
        Assert.Equal(2, withoutLegacy.Count);
        Assert.Contains(Path.Combine(oneDriveDocs, "FLiNGTrainer"), withoutLegacy, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(localAppData, "FLiNGTrainer"), withoutLegacy, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(legacyFling, withoutLegacy, StringComparer.OrdinalIgnoreCase);

        // Once legacy %USERPROFILE%\Documents\FLiNGTrainer exists -> all 3 targets are included.
        Directory.CreateDirectory(legacyFling);
        IReadOnlyList<string> withLegacy = resolver.ResolveTargetDirectories();
        Assert.Equal(3, withLegacy.Count);
        Assert.Contains(legacyFling, withLegacy, StringComparer.OrdinalIgnoreCase);

        // Non-redirected Documents == UserProfile\Documents -> deduplicated to 2 targets.
        var nonRedirected = new WindowsTrainerAudioA0DirectoryResolver(
            () => legacyDocs,
            () => userProfile,
            () => localAppData);
        IReadOnlyList<string> deduped = nonRedirected.ResolveTargetDirectories();
        Assert.Equal(2, deduped.Count);
        Assert.Contains(legacyFling, deduped, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine(localAppData, "FLiNGTrainer"), deduped, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A0Preparer_FreshDirectories_CreatesExactFiles_IsIdempotent_AndRestoreCleansUp()
    {
        string docsDir = Path.Combine(_tempRoot, "Docs");
        string localAppDataDir = Path.Combine(_tempRoot, "LocalAppData");
        string roamingAppDataDir = Path.Combine(_tempRoot, "RoamingAppData");
        string trainerExeDir = Path.Combine(_tempRoot, "Sandbox", "Game1", "Trainer1");
        Directory.CreateDirectory(docsDir);
        Directory.CreateDirectory(localAppDataDir);
        Directory.CreateDirectory(roamingAppDataDir);
        Directory.CreateDirectory(trainerExeDir);

        var resolver = new WindowsTrainerAudioA0DirectoryResolver(
            () => docsDir,
            () => Path.Combine(_tempRoot, "OtherProfile"),
            () => localAppDataDir);
        var preparer = new TrainerAudioA0Preparer(resolver);

        // First prepare creates FLiNGTrainer directories + 3 files in each.
        TrainerAudioA0PreparationReport first = await preparer.PrepareAsync();
        Assert.Equal(2, first.ProcessedDirectories.Count);
        Assert.Equal(6, first.CreatedFileCount);
        Assert.Equal(0, first.BackedUpFileCount);

        foreach (string dir in first.ProcessedDirectories)
        {
            string flingIni = Path.Combine(dir, TrainerAudioA0Preparer.FlingTSettingsFileName);
            string trainerIni = Path.Combine(dir, TrainerAudioA0Preparer.TrainerSettingsFileName);
            string midi = Path.Combine(dir, TrainerAudioA0Preparer.TrainerBgmMidiFileName);

            Assert.Equal("[Settings]\r\nOnLoadMusic = False\r\n", File.ReadAllText(flingIni, Encoding.UTF8));
            Assert.Equal("[Settings]\r\nOnLoadMusic=False\r\n", File.ReadAllText(trainerIni, Encoding.UTF8));
            Assert.True(SilentMidiAssets.IsExactSilentSmf(File.ReadAllBytes(midi)));

            // Newly created files must not generate .spawn_bak files.
            Assert.False(File.Exists(flingIni + TrainerAudioA0Preparer.BackupExtension));
            Assert.False(File.Exists(trainerIni + TrainerAudioA0Preparer.BackupExtension));
            Assert.False(File.Exists(midi + TrainerAudioA0Preparer.BackupExtension));
        }

        // Second prepare (even on a brand-new preparer instance) must be strictly idempotent.
        var secondPreparer = new TrainerAudioA0Preparer(resolver);
        TrainerAudioA0PreparationReport second = await secondPreparer.PrepareAsync();
        Assert.Equal(0, second.CreatedFileCount);
        Assert.Equal(0, second.UpdatedFileCount);
        Assert.Equal(0, second.BackedUpFileCount);

        // Must never touch trainer EXE directory or %APPDATA%.
        Assert.Empty(Directory.GetFileSystemEntries(trainerExeDir));
        Assert.Empty(Directory.GetFileSystemEntries(roamingAppDataDir));

        // Restore from a fresh preparer instance cleans up all created files and empty FLiNGTrainer dirs.
        var restorePreparer = new TrainerAudioA0Preparer(resolver);
        TrainerAudioA0RestoreReport restore = await restorePreparer.RestoreBackupsAsync();
        Assert.Equal(0, restore.RestoredFileCount);
        Assert.Equal(6, restore.CleanedCreatedFileCount);

        foreach (string dir in first.ProcessedDirectories)
        {
            Assert.False(Directory.Exists(dir));
        }
    }

    [Fact]
    public async Task A0Preparer_ExistingFiles_PreservesOtherKeysAndEncoding_DoesNotOverwriteBackup_AndRestores()
    {
        string docsDir = Path.Combine(_tempRoot, "DocsExisting");
        string localAppDataDir = Path.Combine(_tempRoot, "LocalExisting");
        string docsFlingDir = Path.Combine(docsDir, "FLiNGTrainer");
        string localFlingDir = Path.Combine(localAppDataDir, "FLiNGTrainer");
        Directory.CreateDirectory(docsFlingDir);
        Directory.CreateDirectory(localFlingDir);

        // 1. UTF-16 LE BOM FLiNGTSettings.ini with comments, other keys, other sections, and OnLoadMusic = True.
        string originalFlingIniText =
            "; User comment: OnLoadMusic = True\r\n" +
            "[Settings]\r\n" +
            "Language = English\r\n" +
            "OnLoadMusic = True\r\n" +
            "Volume = 80\r\n" +
            "\r\n" +
            "[Hotkeys]\r\n" +
            "Num1 = GodMode\r\n";
        Encoding utf16LeBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        byte[] originalFlingBytes = CombinePreambleAndPayload(utf16LeBom, originalFlingIniText);
        string docsFlingIniPath = Path.Combine(docsFlingDir, TrainerAudioA0Preparer.FlingTSettingsFileName);
        File.WriteAllBytes(docsFlingIniPath, originalFlingBytes);

        // 2. UTF-8 BOM TrainerSettings.ini with [Settings] lacking OnLoadMusic + Unix LF line endings.
        string originalTrainerIniText =
            "[Settings]\n" +
            "Theme=Dark\n" +
            "\n" +
            "[Audio]\n" +
            "Cues=1\n";
        Encoding utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] originalTrainerBytes = CombinePreambleAndPayload(utf8Bom, originalTrainerIniText);
        string docsTrainerIniPath = Path.Combine(docsFlingDir, TrainerAudioA0Preparer.TrainerSettingsFileName);
        File.WriteAllBytes(docsTrainerIniPath, originalTrainerBytes);

        // 3. Existing TrainerBGM.mid with arbitrary original bytes.
        byte[] originalMidiBytes = [0x4D, 0x54, 0x68, 0x64, 0xDE, 0xAD, 0xBE, 0xEF];
        string docsMidiPath = Path.Combine(docsFlingDir, TrainerAudioA0Preparer.TrainerBgmMidiFileName);
        File.WriteAllBytes(docsMidiPath, originalMidiBytes);

        // 4. Existing legacy non-UTF-8 ANSI INI in %LOCALAPPDATA%\FLiNGTrainer\FLiNGTSettings.ini.
        // Byte 0xC4 0xE3 (invalid UTF-8 sequence, e.g., GBK/ANSI text) must survive untouched.
        byte[] ansiHeader = [0x3B, 0x20, 0xC4, 0xE3, 0x0D, 0x0A]; // "; <raw bytes>\r\n"
        byte[] ansiBody = Encoding.ASCII.GetBytes("[Settings]\r\nOnLoadMusic = True\r\nOtherKey = KeepMe\r\n");
        byte[] originalAnsiBytes = [.. ansiHeader, .. ansiBody];
        string localFlingIniPath = Path.Combine(localFlingDir, TrainerAudioA0Preparer.FlingTSettingsFileName);
        File.WriteAllBytes(localFlingIniPath, originalAnsiBytes);

        // 5. Existing INI without any [Settings] section in %LOCALAPPDATA%\FLiNGTrainer\TrainerSettings.ini.
        string originalNoSettingsIni = "[Custom]\r\nKey=Val\r\n";
        string localTrainerIniPath = Path.Combine(localFlingDir, TrainerAudioA0Preparer.TrainerSettingsFileName);
        File.WriteAllText(localTrainerIniPath, originalNoSettingsIni, new UTF8Encoding(false));

        var resolver = new WindowsTrainerAudioA0DirectoryResolver(
            () => docsDir,
            () => Path.Combine(_tempRoot, "OtherProfile"),
            () => localAppDataDir);
        var preparer = new TrainerAudioA0Preparer(resolver);

        // Run PrepareAsync #1.
        TrainerAudioA0PreparationReport first = await preparer.PrepareAsync();
        Assert.Equal(5, first.BackedUpFileCount);
        Assert.Equal(5, first.UpdatedFileCount);
        Assert.Equal(1, first.CreatedFileCount); // LocalAppData TrainerBGM.mid was newly created.

        // Verify UTF-16 LE BOM preserved + only OnLoadMusic changed.
        byte[] updatedFlingBytes = File.ReadAllBytes(docsFlingIniPath);
        Assert.Equal((byte)0xFF, updatedFlingBytes[0]);
        Assert.Equal((byte)0xFE, updatedFlingBytes[1]);
        string updatedFlingText = utf16LeBom.GetString(updatedFlingBytes, 2, updatedFlingBytes.Length - 2);
        Assert.Contains("; User comment: OnLoadMusic = True", updatedFlingText);
        Assert.Contains("Language = English", updatedFlingText);
        Assert.Contains("OnLoadMusic = False", updatedFlingText);
        Assert.Contains("Volume = 80", updatedFlingText);
        Assert.Contains("Num1 = GodMode", updatedFlingText);

        // Verify UTF-8 BOM + LF preserved and OnLoadMusic=False inserted inside [Settings].
        byte[] updatedTrainerBytes = File.ReadAllBytes(docsTrainerIniPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, updatedTrainerBytes[..3]);
        string updatedTrainerText = Encoding.UTF8.GetString(updatedTrainerBytes, 3, updatedTrainerBytes.Length - 3);
        Assert.Equal("[Settings]\nTheme=Dark\nOnLoadMusic=False\n\n[Audio]\nCues=1\n", updatedTrainerText);

        // Verify legacy non-UTF-8 bytes preserved verbatim.
        byte[] updatedAnsiBytes = File.ReadAllBytes(localFlingIniPath);
        Assert.Equal(ansiHeader, updatedAnsiBytes[..ansiHeader.Length]);
        string updatedAnsiAscii = Encoding.Latin1.GetString(updatedAnsiBytes, ansiHeader.Length, updatedAnsiBytes.Length - ansiHeader.Length);
        Assert.Contains("OnLoadMusic = False", updatedAnsiAscii);
        Assert.Contains("OtherKey = KeepMe", updatedAnsiAscii);

        // Verify INI without [Settings] gained appended [Settings] section.
        string updatedNoSettingsText = File.ReadAllText(localTrainerIniPath, Encoding.UTF8);
        Assert.Contains("[Custom]\r\nKey=Val\r\n", updatedNoSettingsText);
        Assert.Contains("[Settings]\r\nOnLoadMusic=False\r\n", updatedNoSettingsText);

        // Verify TrainerBGM.mid replaced with exact 27-byte SMF.
        Assert.True(SilentMidiAssets.IsExactSilentSmf(File.ReadAllBytes(docsMidiPath)));

        // Run PrepareAsync #2 -> must NOT overwrite .spawn_bak files and must be idempotent!
        TrainerAudioA0PreparationReport second = await preparer.PrepareAsync();
        Assert.Equal(0, second.BackedUpFileCount);
        Assert.Equal(0, second.UpdatedFileCount);
        Assert.Equal(0, second.CreatedFileCount);
        Assert.Equal(originalFlingBytes, File.ReadAllBytes(docsFlingIniPath + TrainerAudioA0Preparer.BackupExtension));
        Assert.Equal(originalMidiBytes, File.ReadAllBytes(docsMidiPath + TrainerAudioA0Preparer.BackupExtension));

        // Restore -> exact original bytes back, .spawn_bak removed, newly created LocalAppData TrainerBGM.mid deleted.
        TrainerAudioA0RestoreReport restore = await preparer.RestoreBackupsAsync();
        Assert.Equal(5, restore.RestoredFileCount);
        Assert.Equal(1, restore.CleanedCreatedFileCount);

        Assert.Equal(originalFlingBytes, File.ReadAllBytes(docsFlingIniPath));
        Assert.Equal(originalTrainerBytes, File.ReadAllBytes(docsTrainerIniPath));
        Assert.Equal(originalMidiBytes, File.ReadAllBytes(docsMidiPath));
        Assert.Equal(originalAnsiBytes, File.ReadAllBytes(localFlingIniPath));
        Assert.Equal(originalNoSettingsIni, File.ReadAllText(localTrainerIniPath, Encoding.UTF8));

        Assert.False(File.Exists(docsFlingIniPath + TrainerAudioA0Preparer.BackupExtension));
        Assert.False(File.Exists(docsTrainerIniPath + TrainerAudioA0Preparer.BackupExtension));
        Assert.False(File.Exists(docsMidiPath + TrainerAudioA0Preparer.BackupExtension));
        Assert.False(File.Exists(Path.Combine(localFlingDir, TrainerAudioA0Preparer.TrainerBgmMidiFileName)));
    }

    [Fact]
    public void Unv02LiveSilentEndpointProbe_OnlyRunsWhenExplicitlyOptedIn()
    {
        if (!SilentMidiEndpointProbe.IsOptInEnabled())
        {
            _output.WriteLine(
                $"[not-run] UNV-02 live silent endpoint probe is gated behind {SilentMidiEndpointProbe.OptInEnvironmentVariable}=1.");
            return;
        }

        SilentMidiEndpointProbeReport report = SilentMidiEndpointProbe.RunSilentProbe(_tempRoot);
        _output.WriteLine(
            $"UNV-02 probe: endpoints={report.ActiveRenderEndpointCount}, currentPid={report.CurrentProcessId}, " +
            $"observedCurrentPid={report.ObservedCurrentProcessAudioSession}, observedPids=[{string.Join(',', report.ObservedSessionProcessIds)}], " +
            $"mci={report.MciResult.DiagnosticMessage}");
    }

    private static byte[] CombinePreambleAndPayload(Encoding encoding, string text)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] payload = encoding.GetBytes(text);
        byte[] combined = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, combined, preamble.Length, payload.Length);
        return combined;
    }
}
