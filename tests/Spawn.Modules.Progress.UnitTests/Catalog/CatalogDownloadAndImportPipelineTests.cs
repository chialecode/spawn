using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Catalog;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Catalog;

/// <summary>
/// M3-A-07 automated verification: explicit user-triggered single download &amp; unified local import safety pipeline:
/// manual redirects (10-hop cap, HTTPS-only no downgrade, whitelist host, same-origin Referer),
/// Challenge / HTML immediate abort with staging cleanup, streaming size cap &amp; stall timeout,
/// magic sniffing (PE / ZIP / RAR4 / RAR5 / Solid RAR / 7z / unknown), Zip-Slip / device name / symlink rejection,
/// post-extraction manifest byte verification (missing/truncated =&gt; AV quarantine), Win32 225/226/4551 classification,
/// Mode C <c>7z.exe</c> fallback, multi-EXE user selection, <c>Zone.Identifier</c> preservation,
/// non-ASCII 8.3 short path helper, and pre-launch SHA-256 TOCTOU tamper detection.
/// Zero real network requests and zero real trainer binaries are used.
/// </summary>
public sealed class CatalogDownloadAndImportPipelineTests
{
    [Fact]
    public async Task Downloader_ManualRedirects_Enforces10HopCapHttpsOnlyWhitelistRefererAndSucceedsOnValidChain()
    {
        await using var sandbox = new ProgressTestSandbox();
        byte[] syntheticPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: false);

        // 1. Valid 3-hop HTTPS redirect chain enforcing same-origin Referer on every hop (403 if Referer missing).
        using (var handler = new RecordingMockHttpHandler(req =>
        {
            if (req.Headers.Referrer is null || !req.Headers.Referrer.Host.Equals(FlingUrlPolicy.MainHost, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            string path = req.RequestUri!.AbsolutePath;
            if (path == "/downloads/hop0")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("/downloads/hop1", UriKind.Relative);
                return r;
            }

            if (path == "/downloads/hop1")
            {
                var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                r.Headers.Location = new Uri("https://flingtrainer.com/downloads/hop2", UriKind.Absolute);
                return r;
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(syntheticPe),
            };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            ok.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "EldenRing_Trainer.exe" };
            return ok;
        }))
        {
            using var http = new HttpClient(handler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-ok");

            TrainerDownloadOutcome result = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/hop0",
                "https://flingtrainer.com/trainer/elden-ring-trainer/",
                staging);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(3, handler.Calls.Count);
            Assert.All(handler.Calls, c => Assert.NotNull(c.Referer));
            Assert.Equal("EldenRing_Trainer.exe", result.SuggestedFileName);
            Assert.Equal(syntheticPe.Length, result.BytesReceived);
            Assert.Equal(syntheticPe, await File.ReadAllBytesAsync(result.StagedFilePath!));
        }

        // 2. Exceeding 10 redirect hops is rejected.
        using (var loopHandler = new RecordingMockHttpHandler(req =>
        {
            int idx = int.Parse(req.RequestUri!.AbsolutePath.Split('/').Last());
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri($"https://flingtrainer.com/downloads/{idx + 1}", UriKind.Absolute);
            return r;
        }))
        {
            using var http = new HttpClient(loopHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-11hops");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/0",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.NetworkError, res.FailureKind);
            Assert.Equal(11, loopHandler.Calls.Count); // hop 0 + 10 redirects, fails on 11th redirect
        }

        // 3. HTTPS -> HTTP downgrade redirect is strictly rejected.
        using (var downgradeHandler = new RecordingMockHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://flingtrainer.com/downloads/insecure.exe", UriKind.Absolute);
            return r;
        }))
        {
            using var http = new HttpClient(downgradeHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-downgrade");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/start",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.NetworkError, res.FailureKind);
            Assert.Single(downgradeHandler.Calls);
        }

        // 4. Redirect to untrusted domain or promo URL is strictly rejected.
        using (var untrustedHandler = new RecordingMockHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil-mirror.example/downloads/trainer.exe", UriKind.Absolute);
            return r;
        }))
        {
            using var http = new HttpClient(untrustedHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-untrusted");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/start",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.NetworkError, res.FailureKind);
            Assert.Single(untrustedHandler.Calls);
        }
    }

    [Fact]
    public async Task Downloader_ChallengeHtmlPayloadStreamingSizeLimitAndStallTimeout_AbortAndDeleteStaging()
    {
        await using var sandbox = new ProgressTestSandbox();

        // 1. Disguised HTML payload served as application/octet-stream (Just a moment...) -> immediate abort & cleanup.
        using (var htmlDisguiseHandler = new RecordingMockHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<!DOCTYPE html><html><title>Just a moment...</title></html>")),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return resp;
        }))
        {
            using var http = new HttpClient(htmlDisguiseHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-html");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/fake.exe",
                "https://flingtrainer.com/trainer/elden-ring-trainer/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Equal("https://flingtrainer.com/trainer/elden-ring-trainer/", res.FallbackBrowserUrl);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 2. Missing Referer / 403 -> ChallengeOrHtmlFallback immediately.
        using (var forbiddenHandler = new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))
        {
            using var http = new HttpClient(forbiddenHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-403");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/blocked.exe",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 3. Streaming payload without Content-Length exceeding byte cap -> truncated immediately & staging deleted.
        using (var oversizeHandler = new RecordingMockHttpHandler(_ =>
        {
            // Chunked/streaming content without Content-Length exceeding a 4 KiB test cap.
            var payload = new byte[12 * 1024];
            payload[0] = 0x4D;
            payload[1] = 0x5A;
            var content = new PushStreamContent(payload);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }))
        {
            using var http = new HttpClient(oversizeHandler);
            var downloader = new TrainerSafeDownloader(http, new TrainerCatalogOptions { MaxDownloadBytes = 4096 });
            string staging = sandbox.CreateSubDirectory("staging-oversize");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/big.exe",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.SizeLimitExceeded, res.FailureKind);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 4. Stalled read stream triggers ReadStallTimeout and deletes partial staging file.
        using (var stallHandler = new RecordingMockHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StallingStreamContent([0x4D, 0x5A, 0x90, 0x00]),
            };
            return resp;
        }))
        {
            using var http = new HttpClient(stallHandler);
            var downloader = new TrainerSafeDownloader(http, new TrainerCatalogOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ReadStallTimeout = TimeSpan.FromMilliseconds(120),
            });
            string staging = sandbox.CreateSubDirectory("staging-stall");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(
                "https://flingtrainer.com/downloads/stall.exe",
                "https://flingtrainer.com/trainer/test/",
                staging);

            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.NetworkError, res.FailureKind);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }
    }

    [Fact]
    public async Task UnifiedPipeline_SniffsAndImportsRawPeZipRar4Rar5SolidAnd7z_AndRejectsUnknownMagic()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "EldenRing");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "eldenring.exe"), SyntheticFixtureFactory.CreateSyntheticPeExecutable());
        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "艾尔登法环",
            InstallDir = gameDir,
            ExecutableRelPath = "eldenring.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 1245620,
            SteamBuildId = "17991234",
            SteamLastUpdatedUtc = new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero),
        });

        byte[] adminPe64 = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: true, salt: 1);
        byte[] invokerPe86 = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: false, requireAdmin: false, salt: 2);
        byte[] readmeBytes = Encoding.UTF8.GetBytes("Harmless synthetic trainer readme\n");

        // Build all 5 container types + unknown magic payload.
        string inputDir = sandbox.CreateSubDirectory("local-inputs");
        string rawPePath = Path.Combine(inputDir, "BareAdminTrainer.exe");
        string zipPath = Path.Combine(inputDir, "ZipTrainer.zip");
        string rar4Path = Path.Combine(inputDir, "Rar4Trainer.rar");
        string rar5Path = Path.Combine(inputDir, "Rar5Trainer.rar");
        string rarSolidPath = Path.Combine(inputDir, "Rar4SolidTrainer.rar");
        string sevenZipPath = ProgressTestSandbox.GetCatalogFixturePath("synthetic-stub-trainer.7z");
        string badMagicPath = Path.Combine(inputDir, "FakeTrainer.bin");

        await File.WriteAllBytesAsync(rawPePath, adminPe64);
        await File.WriteAllBytesAsync(zipPath, SyntheticFixtureFactory.CreateZipBytes(
            ("InvokerX86.exe", invokerPe86, 0),
            ("readme.txt", readmeBytes, 0)));
        await File.WriteAllBytesAsync(rar4Path, SyntheticFixtureFactory.CreateRar4ArchiveBytes(
            isSolid: false,
            ("Rar4Trainer.exe", adminPe64),
            ("readme.txt", readmeBytes)));
        await File.WriteAllBytesAsync(rar5Path, SyntheticFixtureFactory.CreateRar5ArchiveBytes(
            isSolid: false,
            ("Rar5Trainer.exe", invokerPe86),
            ("readme.txt", readmeBytes)));
        await File.WriteAllBytesAsync(rarSolidPath, SyntheticFixtureFactory.CreateRar4ArchiveBytes(
            isSolid: true,
            ("notes.txt", readmeBytes),
            ("SolidRarTrainer.exe", adminPe64)));
        await File.WriteAllBytesAsync(badMagicPath, Encoding.UTF8.GetBytes("%PDF-1.7 not a trainer"));

        Assert.Equal(TrainerPayloadFormat.RawPeExecutable, PeBinaryInspector.SniffMagicFormat(rawPePath));
        Assert.Equal(TrainerPayloadFormat.ZipArchive, PeBinaryInspector.SniffMagicFormat(zipPath));
        Assert.Equal(TrainerPayloadFormat.RarArchive, PeBinaryInspector.SniffMagicFormat(rar4Path));
        Assert.Equal(TrainerPayloadFormat.RarArchive, PeBinaryInspector.SniffMagicFormat(rar5Path));
        Assert.Equal(TrainerPayloadFormat.RarArchive, PeBinaryInspector.SniffMagicFormat(rarSolidPath));
        Assert.Equal(TrainerPayloadFormat.SevenZipArchive, PeBinaryInspector.SniffMagicFormat(sevenZipPath));
        Assert.Equal(TrainerPayloadFormat.Unknown, PeBinaryInspector.SniffMagicFormat(badMagicPath));

        using var http = new HttpClient(new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var titleResolver = new CanonicalTitleResolver(sandbox.Database, http);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, http);
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            titleResolver,
            discovery,
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        string expectedAdminSha = Convert.ToHexStringLower(SHA256.HashData(adminPe64));
        string expectedInvokerSha = Convert.ToHexStringLower(SHA256.HashData(invokerPe86));

        // 1. Raw PE (x64 requireAdministrator)
        var rPe = await catalog.ImportLocalAsync(gameId, rawPePath);
        Assert.True(rPe.Succeeded, rPe.Message);
        Assert.Equal("x64", rPe.Trainer!.PeMachine);
        Assert.True(rPe.Trainer.RequiresAdmin);
        Assert.Equal(TrainerAuthenticodeStatus.Unsigned, rPe.Trainer.AuthenticodeStatus);
        Assert.Equal(expectedAdminSha, rPe.Trainer.FileSha256);
        Assert.Equal("17991234", rPe.Trainer.BoundBuildId);
        Assert.True((await catalog.VerifyAsync(rPe.Trainer.TrainerId)).IsValid);

        // 2. .zip (x86 asInvoker via BCL ZipArchive)
        var rZip = await catalog.ImportLocalAsync(gameId, zipPath);
        Assert.True(rZip.Succeeded, rZip.Message);
        Assert.Equal("x86", rZip.Trainer!.PeMachine);
        Assert.False(rZip.Trainer.RequiresAdmin);
        Assert.Equal(expectedInvokerSha, rZip.Trainer.FileSha256);

        // 3. .rar (RAR4 via SharpCompress 0.50.9)
        var rRar4 = await catalog.ImportLocalAsync(gameId, rar4Path);
        Assert.True(rRar4.Succeeded, rRar4.Message);
        Assert.Equal(expectedAdminSha, rRar4.Trainer!.FileSha256);
        Assert.True(rRar4.Trainer.RequiresAdmin);

        // 4. .rar (RAR5 via SharpCompress 0.50.9)
        var rRar5 = await catalog.ImportLocalAsync(gameId, rar5Path);
        Assert.True(rRar5.Succeeded, rRar5.Message);
        Assert.Equal(expectedInvokerSha, rRar5.Trainer!.FileSha256);
        Assert.False(rRar5.Trainer.RequiresAdmin);

        // 5. .rar (Solid RAR4 via SharpCompress 0.50.9 ExtractAllEntries reader)
        var rSolid = await catalog.ImportLocalAsync(gameId, rarSolidPath);
        Assert.True(rSolid.Succeeded, rSolid.Message);
        Assert.Equal(expectedAdminSha, rSolid.Trainer!.FileSha256);

        // 5b. .rar (Solid RAR5 via SharpCompress 0.50.9 ExtractAllEntries reader)
        string rar5SolidPath = Path.Combine(inputDir, "Rar5SolidTrainer.rar");
        await File.WriteAllBytesAsync(rar5SolidPath, SyntheticFixtureFactory.CreateRar5ArchiveBytes(
            isSolid: true,
            ("notes.txt", readmeBytes),
            ("SolidRar5Trainer.exe", invokerPe86)));
        var rSolid5 = await catalog.ImportLocalAsync(gameId, rar5SolidPath);
        Assert.True(rSolid5.Succeeded, rSolid5.Message);
        Assert.Equal(expectedInvokerSha, rSolid5.Trainer!.FileSha256);

        // 6. .7z (static synthetic fixture via SharpCompress 0.50.9)
        var r7z = await catalog.ImportLocalAsync(gameId, sevenZipPath);
        Assert.True(r7z.Succeeded, r7z.Message);
        Assert.Equal("x64", r7z.Trainer!.PeMachine);
        Assert.True(r7z.Trainer.RequiresAdmin);
        Assert.True((await catalog.VerifyAsync(r7z.Trainer.TrainerId)).IsValid);

        // 7. Unknown magic and truncated MZ header rejected cleanly
        var rBad = await catalog.ImportLocalAsync(gameId, badMagicPath);
        Assert.False(rBad.Succeeded);
        Assert.Equal(TrainerImportFailureKind.InvalidMagicOrFormat, rBad.FailureKind);

        string truncatedPePath = Path.Combine(inputDir, "TruncatedMz.exe");
        await File.WriteAllBytesAsync(truncatedPePath, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00]);
        var rTruncPe = await catalog.ImportLocalAsync(gameId, truncatedPePath);
        Assert.False(rTruncPe.Succeeded);
        Assert.Equal(TrainerImportFailureKind.InvalidMagicOrFormat, rTruncPe.FailureKind);

        // 8. Corrupt PE Certificate Table reports InvalidOrUntrustedSignature (read-only offline check)
        string corruptCertPath = Path.Combine(inputDir, "CorruptCert.exe");
        await File.WriteAllBytesAsync(corruptCertPath, SyntheticFixtureFactory.CreateSyntheticPeExecutable(corruptCertificateTable: true));
        PeInspectionSummary corruptSummary = PeBinaryInspector.InspectExecutable(corruptCertPath);
        Assert.True(corruptSummary.IsValidPe);
        Assert.Equal(TrainerAuthenticodeStatus.InvalidOrUntrustedSignature, corruptSummary.AuthenticodeStatus);

        // Staging root must be empty after all operations.
        Assert.Empty(Directory.EnumerateDirectories(sandbox.Paths.TrainerStagingDirectory));
    }

    [Fact]
    public async Task ArchiveSecurity_ZipSlipDeviceNameSymlinkPostExtractMissingTruncatedAndWin32Errors_HandledAccurately()
    {
        await using var sandbox = new ProgressTestSandbox();
        byte[] syntheticPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable();
        var extractor = new TrainerArchiveExtractor();

        // 1. Zip-Slip traversal, absolute paths, Windows reserved device names, and Unix symlinks MUST all be rejected.
        (string Name, string EntryName, int ExtAttrs)[] maliciousCases =
        [
            ("traversal", "../escape.exe", 0),
            ("nested-traversal", "sub/../../escape.exe", 0),
            ("absolute-drive", @"C:\Windows\evil.exe", 0),
            ("device-con", "CON.exe", 0),
            ("device-nul", "sub/NUL.txt", 0),
            ("ads-colon", "trainer.exe:hidden", 0),
            ("unix-symlink", "link_trainer.exe", 0xA1FF << 16),
        ];

        foreach (var (caseName, entryName, extAttrs) in maliciousCases)
        {
            string zipPath = Path.Combine(sandbox.RootDirectory, $"malicious-{caseName}.zip");
            string destDir = sandbox.CreateSubDirectory($"dest-{caseName}");
            await File.WriteAllBytesAsync(zipPath, SyntheticFixtureFactory.CreateZipBytes((entryName, syntheticPe, extAttrs)));

            ArchiveExtractionOutcome res = await extractor.ExtractAndVerifyAsync(zipPath, TrainerPayloadFormat.ZipArchive, destDir);
            Assert.False(res.Succeeded, $"Expected '{caseName}' ({entryName}) to be rejected.");
            Assert.Equal(TrainerImportFailureKind.ZipSlipRejected, res.FailureKind);
            Assert.Empty(Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories));
        }

        // Verify RAR4 Zip-Slip traversal is also blocked before writing anything to disk.
        string maliciousRar = Path.Combine(sandbox.RootDirectory, "malicious-traversal.rar");
        string destRar = sandbox.CreateSubDirectory("dest-rar-traversal");
        await File.WriteAllBytesAsync(maliciousRar, SyntheticFixtureFactory.CreateRar4ArchiveBytes(false, ("../escaped_rar.exe", syntheticPe)));
        ArchiveExtractionOutcome rarSlipRes = await extractor.ExtractAndVerifyAsync(maliciousRar, TrainerPayloadFormat.RarArchive, destRar);
        Assert.False(rarSlipRes.Succeeded);
        Assert.Equal(TrainerImportFailureKind.ZipSlipRejected, rarSlipRes.FailureKind);
        Assert.Empty(Directory.EnumerateFiles(destRar, "*", SearchOption.AllDirectories));

        // 2. Post-extraction missing file (simulating AV silently swallowing extracted .exe) -> AntivirusQuarantined.
        string goodZip = Path.Combine(sandbox.RootDirectory, "good-av-test.zip");
        await File.WriteAllBytesAsync(goodZip, SyntheticFixtureFactory.CreateZipBytes(("Trainer.exe", syntheticPe, 0)));

        var deleteSimExtractor = new TrainerArchiveExtractor
        {
            OnBeforePostExtractVerification = dest => File.Delete(Path.Combine(dest, "Trainer.exe")),
        };
        string destMissing = sandbox.CreateSubDirectory("dest-av-missing");
        ArchiveExtractionOutcome missingRes = await deleteSimExtractor.ExtractAndVerifyAsync(goodZip, TrainerPayloadFormat.ZipArchive, destMissing);
        Assert.False(missingRes.Succeeded);
        Assert.Equal(TrainerImportFailureKind.AntivirusQuarantined, missingRes.FailureKind);

        // 3. Post-extraction truncated file -> AntivirusQuarantined.
        var truncateSimExtractor = new TrainerArchiveExtractor
        {
            OnBeforePostExtractVerification = dest =>
            {
                string exe = Path.Combine(dest, "Trainer.exe");
                using var fs = new FileStream(exe, FileMode.Open, FileAccess.Write);
                fs.SetLength(syntheticPe.Length - 16);
            },
        };
        string destTruncated = sandbox.CreateSubDirectory("dest-av-truncated");
        ArchiveExtractionOutcome truncRes = await truncateSimExtractor.ExtractAndVerifyAsync(goodZip, TrainerPayloadFormat.ZipArchive, destTruncated);
        Assert.False(truncRes.Succeeded);
        Assert.Equal(TrainerImportFailureKind.AntivirusQuarantined, truncRes.FailureKind);

        // 4. Win32 225 (ERROR_VIRUS_INFECTED), 226 (ERROR_VIRUS_DELETED), and 4551 (Smart App Control) classification.
        Assert.Equal(225, PeBinaryInspector.TryExtractWin32ErrorCode(new Win32Exception(225)));
        Assert.Equal(226, PeBinaryInspector.TryExtractWin32ErrorCode(new IOException("Virus deleted", unchecked((int)0x800700E2))));
        Assert.Equal(4551, PeBinaryInspector.TryExtractWin32ErrorCode(new Win32Exception(4551)));
        Assert.Equal(TrainerImportFailureKind.AntivirusQuarantined, PeBinaryInspector.ClassifyOsSecurityError(225));
        Assert.Equal(TrainerImportFailureKind.AntivirusQuarantined, PeBinaryInspector.ClassifyOsSecurityError(226));
        Assert.Equal(TrainerImportFailureKind.SmartAppControlBlocked, PeBinaryInspector.ClassifyOsSecurityError(4551));
    }

    [Fact]
    public async Task ArchiveExtractor_SharpCompressUnsupportedFormat_FallsBackToModeCSevenZipRunnerWithZipSlipAndByteChecks()
    {
        await using var sandbox = new ProgressTestSandbox();
        byte[] syntheticPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: false);

        // Create a file with valid 7z magic (37 7A BC AF 27 1C) followed by an unsupported/corrupted header body
        // so SharpCompress throws InvalidFormatException / ArchiveException and triggers Mode C 7z.exe fallback.
        string exotic7zPath = Path.Combine(sandbox.RootDirectory, "exotic-codec.7z");
        await File.WriteAllBytesAsync(exotic7zPath, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x99, 0xFF, 0xFF, 0x01, 0x02]);

        var mockRunner = new FakeExternalSevenZipRunner(
            entries:
            [
                new ArchiveManifestEntry("FallbackTrainer.exe", "FallbackTrainer.exe", syntheticPe.Length, IsDirectory: false, IsSymlinkOrReparsePoint: false, IsEncrypted: false),
            ],
            onExtract: dest => File.WriteAllBytes(Path.Combine(dest, "FallbackTrainer.exe"), syntheticPe));

        var extractor = new TrainerArchiveExtractor(sevenZipRunner: mockRunner);
        string destDir = sandbox.CreateSubDirectory("dest-modec");

        ArchiveExtractionOutcome res = await extractor.ExtractAndVerifyAsync(exotic7zPath, TrainerPayloadFormat.SevenZipArchive, destDir);
        Assert.True(res.Succeeded, res.Message);
        Assert.True(res.UsedSevenZipFallback);
        Assert.Equal(1, mockRunner.ListCalls);
        Assert.Equal(1, mockRunner.ExtractCalls);
        Assert.Single(res.ExtractedFileRelativePaths);
        Assert.Equal(syntheticPe, await File.ReadAllBytesAsync(Path.Combine(destDir, "FallbackTrainer.exe")));

        // Verify Mode C fallback blocks Zip-Slip during ListEntriesAsync BEFORE ExtractAsync is ever invoked!
        var slipRunner = new FakeExternalSevenZipRunner(
            entries:
            [
                new ArchiveManifestEntry("../evil_modec.exe", "../evil_modec.exe", syntheticPe.Length, IsDirectory: false, IsSymlinkOrReparsePoint: false, IsEncrypted: false),
            ],
            onExtract: _ => throw new InvalidOperationException("ExtractAsync must never be called when listing fails Zip-Slip check."));
        var slipExtractor = new TrainerArchiveExtractor(sevenZipRunner: slipRunner);
        string destSlip = sandbox.CreateSubDirectory("dest-modec-slip");
        ArchiveExtractionOutcome slipRes = await slipExtractor.ExtractAndVerifyAsync(exotic7zPath, TrainerPayloadFormat.SevenZipArchive, destSlip);
        Assert.False(slipRes.Succeeded);
        Assert.Equal(TrainerImportFailureKind.ZipSlipRejected, slipRes.FailureKind);
        Assert.Equal(1, slipRunner.ListCalls);
        Assert.Equal(0, slipRunner.ExtractCalls);

        // Verify ProcessExternalSevenZipRunner technical listing parser handles Path/Size/Folder/Symbolic Link accurately.
        const string sampleSlt = """
            Path = SubDir
            Size = 0
            Folder = +
            Attributes = D

            Path = SubDir\Trainer.exe
            Size = 1024
            Folder = -
            Encrypted = -
            Attributes = A

            Path = bad_link.exe
            Size = 0
            Folder = -
            Symbolic Link = ..\evil.exe
            """;
        var parsedEntries = ProcessExternalSevenZipRunner.ParseSevenZipTechnicalListing(sampleSlt);
        Assert.Equal(3, parsedEntries.Count);
        Assert.True(parsedEntries[0].IsDirectory);
        Assert.Equal(1024, parsedEntries[1].ExpectedSize);
        Assert.True(parsedEntries[2].IsSymlinkOrReparsePoint);
    }

    [Fact]
    public async Task CatalogService_MultiExeDownload_RequiresUserSelectionPersistsHotkeysAndNeverExecutesAuxiliaries()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "EldenRing");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "eldenring.exe"), SyntheticFixtureFactory.CreateSyntheticPeExecutable());
        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "艾尔登法环",
            InstallDir = gameDir,
            ExecutableRelPath = "eldenring.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 1245620,
            SteamBuildId = "17991234",
        });

        string detailHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-detail-elden-ring.html"));
        byte[] mainPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: true, salt: 11);
        byte[] auxPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: false, salt: 22);
        byte[] multiExeZip = SyntheticFixtureFactory.CreateZipBytes(
            ("EldenRing_MainTrainer.exe", mainPe, 0),
            ("tools/AuxiliaryHelper.exe", auxPe, 0));

        using var handler = new RecordingMockHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Equals("https://flingtrainer.com/trainer/elden-ring-trainer/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(detailHtml, Encoding.UTF8, "text/html"),
                };
            }

            if (url.StartsWith("https://flingtrainer.com/downloads/", StringComparison.OrdinalIgnoreCase))
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(multiExeZip),
                };
                ok.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                return ok;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var titleResolver = new CanonicalTitleResolver(sandbox.Database, http);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, http);
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            titleResolver,
            discovery,
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        var baseCandidate = new TrainerCandidate
        {
            CandidateId = "cand_er",
            Provider = TrainerProviderKind.FlingMain,
            Title = "Elden Ring Trainer",
            PageUrl = "https://flingtrainer.com/trainer/elden-ring-trainer/",
        };

        // 1. Download without executable choice -> returns MultipleExecutablesSelectionRequired with both choices.
        TrainerImportResult firstAttempt = await catalog.DownloadAsync(gameId, baseCandidate);
        Assert.False(firstAttempt.Succeeded);
        Assert.Equal(TrainerImportFailureKind.MultipleExecutablesSelectionRequired, firstAttempt.FailureKind);
        Assert.Equal(2, firstAttempt.CandidateExecutables.Count);
        Assert.Contains(firstAttempt.CandidateExecutables, e => e.RelativePath.Equals("EldenRing_MainTrainer.exe", StringComparison.OrdinalIgnoreCase) && e.RequiresAdmin);
        Assert.Contains(firstAttempt.CandidateExecutables, e => e.RelativePath.Contains("AuxiliaryHelper.exe", StringComparison.OrdinalIgnoreCase) && !e.RequiresAdmin);

        // 2. Download again with user's explicit selection -> installs cleanly, pins SHA-256, persists parsed hotkeys!
        TrainerCandidate loadedCandidate = await catalog.LoadCandidateDetailsAsync(gameId, baseCandidate);
        TrainerImportResult secondAttempt = await catalog.DownloadWithExecutableSelectionAsync(
            gameId,
            loadedCandidate,
            selectedExecutableRelPath: "EldenRing_MainTrainer.exe");

        Assert.True(secondAttempt.Succeeded, secondAttempt.Message);
        Assert.NotNull(secondAttempt.Trainer);
        Assert.Equal(TrainerParseMode.Structured, secondAttempt.Trainer!.ParseMode);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(mainPe)), secondAttempt.Trainer.FileSha256);
        Assert.Equal(34, secondAttempt.Hotkeys.Count);

        var dbHotkeys = await catalog.GetHotkeysAsync(secondAttempt.Trainer.TrainerId);
        Assert.Equal(34, dbHotkeys.Count);
        Assert.Equal("Num 1", dbHotkeys[0].OriginalChord);
    }

    [Fact]
    public async Task CatalogService_LocalImportPreservesZoneIdentifier_AndVerifyCatchesToctouTamperingAndNonAsciiPath()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "LocalGame");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "game.exe"), SyntheticFixtureFactory.CreateSyntheticPeExecutable());
        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "单机示例",
            InstallDir = gameDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.Standalone,
        });

        string importDir = sandbox.CreateSubDirectory("user-downloads");
        string localExe = Path.Combine(importDir, "UserLocalTrainer.exe");
        byte[] peBytes = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: false, salt: 77);
        await File.WriteAllBytesAsync(localExe, peBytes);

        // Attach NTFS Zone.Identifier ADS (Mark-of-the-Web) to prove local import NEVER strips Zone.Identifier.
        const string motwPayload = "[ZoneTransfer]\r\nZoneId=3\r\n";
        bool adsSupported = false;
        try
        {
            await File.WriteAllTextAsync(localExe + ":Zone.Identifier", motwPayload, Encoding.ASCII);
            adsSupported = File.Exists(localExe + ":Zone.Identifier");
        }
        catch
        {
            adsSupported = false;
        }

        using var http = new HttpClient(new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var titleResolver = new CanonicalTitleResolver(sandbox.Database, http);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, http);
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            titleResolver,
            discovery,
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        TrainerImportResult imported = await catalog.ImportLocalAsync(gameId, localExe);
        Assert.True(imported.Succeeded, imported.Message);
        Assert.NotNull(imported.Trainer);
        Assert.Equal(TrainerProviderKind.LocalImport, imported.Trainer!.Provider);
        Assert.Equal(TrainerParseMode.FreeHosted, imported.Trainer.ParseMode);

        string committedPath = Path.Combine(sandbox.Paths.TrainerSandboxDirectory, imported.Trainer.SandboxRelativePath);
        Assert.True(File.Exists(committedPath));

        if (adsSupported)
        {
            string committedAds = await File.ReadAllTextAsync(committedPath + ":Zone.Identifier", Encoding.ASCII);
            Assert.Contains("ZoneId=3", committedAds, StringComparison.Ordinal);
        }

        // 1. Pre-launch verification passes before tampering.
        TrainerIntegrityReport okReport = await catalog.VerifyAsync(imported.Trainer.TrainerId);
        Assert.True(okReport.IsValid);
        Assert.True(okReport.FileExists);
        Assert.True(okReport.HashMatches);
        Assert.True(okReport.IsInsideSandbox);
        Assert.False(okReport.GameFilesChangedSinceBinding);

        // 2. Simulate TOCTOU tampering on committed binary -> VerifyAsync MUST reject immediately!
        byte[] tampered = await File.ReadAllBytesAsync(committedPath);
        tampered[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(committedPath, tampered);

        TrainerIntegrityReport tamperedReport = await catalog.VerifyAsync(imported.Trainer.TrainerId);
        Assert.False(tamperedReport.IsValid);
        Assert.True(tamperedReport.FileExists);
        Assert.False(tamperedReport.HashMatches);
        Assert.NotEqual(tamperedReport.ExpectedSha256, tamperedReport.ActualSha256);

        // 3. Verify RemoveAsync cleans only the trainer's sandbox folder and never touches game files.
        await catalog.RemoveAsync(imported.Trainer.TrainerId);
        Assert.False(File.Exists(committedPath));
        Assert.True(File.Exists(Path.Combine(gameDir, "game.exe")));
        Assert.Empty(await catalog.ListForGameAsync(gameId));

        // 4. Verify non-ASCII sandbox path 8.3 short-path helper works cleanly without throwing.
        string nonAsciiDir = sandbox.CreateSubDirectory("中文沙箱目录");
        string nonAsciiFile = Path.Combine(nonAsciiDir, "stub.exe");
        await File.WriteAllBytesAsync(nonAsciiFile, peBytes);
        var (resolvedLaunchPath, _) = PeBinaryInspector.TryResolveLaunchPath(nonAsciiFile);
        Assert.True(File.Exists(resolvedLaunchPath));
    }

    [Fact]
    public async Task Downloader_RealFling3HopChain_ForwardsCookiesRefererHonestHeadersAndResolvesRawUnescapedLocation()
    {
        await using var sandbox = new ProgressTestSandbox();
        byte[] syntheticPe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: false, salt: 9);
        const string pageUrl = "https://flingtrainer.com/trainer/black-myth-wukong-trainer/";
        const string hop0Url = "https://flingtrainer.com/downloads/J0ZX9_hiI3QYHi9EG5A7vw,,";

        using var handler = new RecordingMockHttpHandler(req =>
        {
            // Enforce standard honest headers on every hop.
            Assert.NotNull(req.Headers.Referrer);
            Assert.Equal(FlingUrlPolicy.MainHost, req.Headers.Referrer!.Host, ignoreCase: true);
            Assert.True(req.Headers.TryGetValues("Accept", out var accepts) && accepts.Any(v => v.Contains("*/*", StringComparison.Ordinal)));
            Assert.True(req.Headers.TryGetValues("Accept-Language", out var langs) && langs.Any(v => v.Contains("en-US", StringComparison.OrdinalIgnoreCase)));

            string path = req.RequestUri!.AbsolutePath;
            if (path.StartsWith("/downloads/", StringComparison.OrdinalIgnoreCase))
            {
                var r0 = new HttpResponseMessage(HttpStatusCode.Found);
                r0.Headers.Location = new Uri("https://flingtrainer.com/download-trainer.php?path=%2Fwp-content%2Fuploads%2F2024%2F08%2FGame.zip", UriKind.Absolute);
                // Real FLiNG WordPress Set-Cookie headers with commas in RFC1123 Expires dates.
                r0.Headers.TryAddWithoutValidation("Set-Cookie", "slHoIPaRtqAhj=n68PkLXN9Jt%5Dxzq4; expires=Sun, 22 Nov 2026 17:31:31 GMT; Max-Age=86400; path=/; secure");
                r0.Headers.TryAddWithoutValidation("Set-Cookie", "pVyAsTbi=%40lqSt0vc_%2AH; expires=Sun, 22 Nov 2026 17:31:31 GMT; Max-Age=86400; path=/; secure");
                return r0;
            }

            if (path.Equals("/download-trainer.php", StringComparison.OrdinalIgnoreCase))
            {
                // Verify Hop 0 cookies propagated cleanly to Hop 1.
                string cookieHdr = req.Headers.TryGetValues("Cookie", out var cVals) ? string.Join("; ", cVals) : string.Empty;
                if (!cookieHdr.Contains("slHoIPaRtqAhj=n68PkLXN9Jt%5Dxzq4", StringComparison.Ordinal)
                    || !cookieHdr.Contains("pVyAsTbi=%40lqSt0vc_%2AH", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                // Raw Location header containing unescaped spaces and brackets (where HttpResponseHeaders.Location may be null).
                var r1 = new HttpResponseMessage(HttpStatusCode.Found);
                r1.Headers.TryAddWithoutValidation(
                    "Location",
                    "https://flingtrainer.com/wp-content/uploads/trainer-files/2024/08/Game.zip/Black Myth [v1.0] Trainer.exe");
                return r1;
            }

            // Hop 2: enforce both same-origin Referer (Cloudflare edge hotlink rule) and propagated session cookies.
            if (req.Headers.Referrer is null
                || !req.Headers.Referrer.Host.Equals(FlingUrlPolicy.MainHost, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(" ", Encoding.UTF8, "text/plain"),
                };
            }

            string hop2Cookie = req.Headers.TryGetValues("Cookie", out var hop2Vals) ? string.Join("; ", hop2Vals) : string.Empty;
            if (!hop2Cookie.Contains("slHoIPaRtqAhj=n68PkLXN9Jt%5Dxzq4", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(syntheticPe),
            };
            ok.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msdownload");
            return ok;
        });

        using var http = new HttpClient(handler);
        var downloader = new TrainerSafeDownloader(http);
        string staging = sandbox.CreateSubDirectory("staging-3hop-cookies");

        TrainerDownloadOutcome outcome = await downloader.DownloadToStagingAsync(hop0Url, pageUrl, staging);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(3, handler.Calls.Count);
        Assert.All(handler.Calls, c =>
        {
            Assert.Equal(new Uri(pageUrl), c.Referer);
            Assert.Contains("SPAWN-TrainerCatalog", c.UserAgent ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Mozilla/", c.UserAgent ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("*/*", c.Accept);
            Assert.Contains("en-US", c.AcceptLanguage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("q=0.9", c.AcceptLanguage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Null(handler.Calls[0].Cookie);
        Assert.Contains("slHoIPaRtqAhj=", handler.Calls[1].Cookie ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("slHoIPaRtqAhj=", handler.Calls[2].Cookie ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("Black Myth [v1.0] Trainer.exe", outcome.SuggestedFileName);
        Assert.Equal(syntheticPe, await File.ReadAllBytesAsync(outcome.StagedFilePath!));

        // Verify a second independent download does NOT receive leaked cookies from the first download.
        string staging2 = sandbox.CreateSubDirectory("staging-cookie-isolation");
        TrainerDownloadOutcome second = await downloader.DownloadToStagingAsync(hop0Url, pageUrl, staging2);
        Assert.True(second.Succeeded, second.Message);
        Assert.Null(handler.Calls[3].Cookie);
    }

    [Fact]
    public async Task Downloader_ArchiveDirectFile_NormalizesBinaryRefererToArchiveIndexOrigin()
    {
        await using var sandbox = new ProgressTestSandbox();
        byte[] rarBytes = SyntheticFixtureFactory.CreateRar4ArchiveBytes(
            isSolid: false,
            ("ArchiveTrainer.exe", SyntheticFixtureFactory.CreateSyntheticPeExecutable()));

        const string directArchiveUrl = "https://archive.flingtrainer.com/files/Ace%20Combat%207%20v1.0%20Trainer.rar";

        using var handler = new RecordingMockHttpHandler(req =>
        {
            // Referer must point to the archive index origin, NOT the .rar file itself.
            Assert.Equal(new Uri(FlingUrlPolicy.ArchiveIndexUrl), req.Headers.Referrer);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(rarBytes),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-rar-compressed");
            return resp;
        });

        using var http = new HttpClient(handler);
        var downloader = new TrainerSafeDownloader(http);
        string staging = sandbox.CreateSubDirectory("staging-archive-direct");

        TrainerDownloadOutcome outcome = await downloader.DownloadToStagingAsync(directArchiveUrl, directArchiveUrl, staging);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Single(handler.Calls);
        Assert.Equal("Ace Combat 7 v1.0 Trainer.rar", outcome.SuggestedFileName);
        Assert.Equal(rarBytes, await File.ReadAllBytesAsync(outcome.StagedFilePath!));
    }

    [Fact]
    public async Task CatalogService_DirectArchiveCandidateWithoutPreloadedAttachments_DownloadsAndInstallsCleanly()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameDir = sandbox.CreateSubDirectory("games", "AceCombat7");
        await File.WriteAllBytesAsync(Path.Combine(gameDir, "Ace7Game.exe"), SyntheticFixtureFactory.CreateSyntheticPeExecutable());
        string gameId = SpawnId.NewUlid();
        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "皇牌空战 7",
            InstallDir = gameDir,
            ExecutableRelPath = "Ace7Game.exe",
            PlatformSource = PlatformSource.SteamLocal,
            SteamAppId = 502500,
        });

        byte[] archivePe = SyntheticFixtureFactory.CreateSyntheticPeExecutable(is64Bit: true, requireAdmin: true, salt: 33);
        byte[] rarPayload = SyntheticFixtureFactory.CreateRar4ArchiveBytes(isSolid: false, ("Ace7_Trainer.exe", archivePe));
        const string directArchiveUrl = "https://archive.flingtrainer.com/files/Ace%20Combat%207%20v1.0%20Plus%2011%20Trainer.rar";

        using var handler = new RecordingMockHttpHandler(req =>
        {
            Assert.Equal(directArchiveUrl, req.RequestUri!.AbsoluteUri);
            Assert.Equal(new Uri(FlingUrlPolicy.ArchiveIndexUrl), req.Headers.Referrer);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(rarPayload),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-rar-compressed");
            return resp;
        });

        using var http = new HttpClient(handler);
        var titleResolver = new CanonicalTitleResolver(sandbox.Database, http);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, http);
        var catalog = new TrainerCatalogService(
            sandbox.Database,
            sandbox.Paths,
            titleResolver,
            discovery,
            new TrainerSafeDownloader(http),
            new TrainerArchiveExtractor());

        var directArchiveCandidate = new TrainerCandidate
        {
            CandidateId = "cand_arch_ac7",
            Provider = TrainerProviderKind.FlingArchive,
            Title = "Ace Combat 7 Skies Unknown v1.0 Plus 11 Trainer",
            PageUrl = directArchiveUrl,
            VersionLabel = "v1.0",
        };

        TrainerImportResult imported = await catalog.DownloadAsync(gameId, directArchiveCandidate);
        Assert.True(imported.Succeeded, imported.Message);
        Assert.NotNull(imported.Trainer);
        Assert.Equal(TrainerProviderKind.FlingArchive, imported.Trainer!.Provider);
        Assert.Equal(TrainerParseMode.FreeHosted, imported.Trainer.ParseMode);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archivePe)), imported.Trainer.FileSha256);
        // Only ONE HTTP call (direct download); archive binary was never fetched as HTML detail text.
        Assert.Single(handler.Calls);
        Assert.True((await catalog.VerifyAsync(imported.Trainer.TrainerId)).IsValid);
    }

    [Fact]
    public async Task Downloader_Differentiated403Challenge503AndHtmlFallback_AbortsImmediatelyWithZeroRetries()
    {
        await using var sandbox = new ProgressTestSandbox();
        const string dlUrl = "https://flingtrainer.com/downloads/sample-token,,";
        const string pageUrl = "https://flingtrainer.com/trainer/sample-trainer/";

        // 1. Cloudflare Managed Challenge: HTTP 403 + cf-mitigated: challenge -> ChallengeOrHtmlFallback, 0 retries.
        using (var cfHandler = new RecordingMockHttpHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><title>Just a moment...</title></html>", Encoding.UTF8, "text/html"),
            };
            r.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            return r;
        }))
        {
            using var http = new HttpClient(cfHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-cf-403");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(dlUrl, pageUrl, staging);
            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Contains("Cloudflare 人机验证", res.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(pageUrl, res.FallbackBrowserUrl);
            Assert.Single(cfHandler.Calls);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 2. Cloudflare Edge Hotlink/WAF HTTP 403 without cf-mitigated header -> ChallengeOrHtmlFallback, 0 retries, clear 403 guidance.
        using (var hotlinkHandler = new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(" ", Encoding.UTF8, "text/plain"),
        }))
        {
            using var http = new HttpClient(hotlinkHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-hotlink-403");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(dlUrl, pageUrl, staging);
            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Contains("HTTP 403", res.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Single(hotlinkHandler.Calls);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 3. HTTP 503 -> ChallengeOrHtmlFallback, 0 retries.
        using (var unavailableHandler = new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            using var http = new HttpClient(unavailableHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-503");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(dlUrl, pageUrl, staging);
            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Contains("HTTP 503", res.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Single(unavailableHandler.Calls);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }

        // 4. HTTP 200 with Content-Type: text/html -> ChallengeOrHtmlFallback, 0 retries.
        using (var htmlCtHandler = new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html><body>Not a binary</body></html>", Encoding.UTF8, "text/html"),
        }))
        {
            using var http = new HttpClient(htmlCtHandler);
            var downloader = new TrainerSafeDownloader(http);
            string staging = sandbox.CreateSubDirectory("staging-200-html");

            TrainerDownloadOutcome res = await downloader.DownloadToStagingAsync(dlUrl, pageUrl, staging);
            Assert.False(res.Succeeded);
            Assert.Equal(TrainerImportFailureKind.ChallengeOrHtmlFallback, res.FailureKind);
            Assert.Contains("HTML", res.Message ?? string.Empty, StringComparison.Ordinal);
            Assert.Single(htmlCtHandler.Calls);
            Assert.Empty(Directory.EnumerateFiles(staging));
        }
    }

    private sealed class PushStreamContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(data, 0, data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false; // Deliberately omit Content-Length to test streaming counter cap.
        }
    }

    private sealed class StallingStreamContent(byte[] initialChunk) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingReadStream(initialChunk));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class StallingReadStream(byte[] firstChunk) : Stream
    {
        private bool _sentFirst;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                firstChunk.CopyTo(buffer.Span);
                return firstChunk.Length;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }

    private sealed class FakeExternalSevenZipRunner(
        IReadOnlyList<ArchiveManifestEntry> entries,
        Action<string> onExtract) : IExternalSevenZipRunner
    {
        public int ListCalls { get; private set; }
        public int ExtractCalls { get; private set; }
        public bool IsAvailable => true;

        public Task<ExternalSevenZipListResult> ListEntriesAsync(string archivePath, CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(new ExternalSevenZipListResult(true, entries));
        }

        public Task<ExternalSevenZipExtractResult> ExtractAsync(string archivePath, string destinationDirectory, CancellationToken ct = default)
        {
            ExtractCalls++;
            Directory.CreateDirectory(destinationDirectory);
            onExtract(destinationDirectory);
            return Task.FromResult(new ExternalSevenZipExtractResult(true, 0));
        }
    }
}
