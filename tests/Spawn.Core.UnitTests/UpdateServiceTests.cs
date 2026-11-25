using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core.Update;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// Comprehensive 100%-offline unit tests for the signed in-app auto-updater (<c>ADR-0007 §3.3</c> / <c>M3-A-22</c>).
/// Zero real HTTP traffic, zero real installers, zero UAC prompts, zero popups, and ephemeral test-only ECDSA keys.
/// </summary>
public sealed class UpdateServiceTests
{
    [Fact]
    public void CompiledInPublicKey_HasValidEcdsaP256CurveAndExactFrozenFingerprints()
    {
        byte[] pemBytes = Encoding.UTF8.GetBytes(UpdateSigningPublicKey.Pem);
        Assert.DoesNotContain((byte)'\r', pemBytes);
        Assert.Equal(
            UpdateSigningPublicKey.ExpectedPemSha256Hex,
            Convert.ToHexStringLower(SHA256.HashData(pemBytes)));

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(UpdateSigningPublicKey.Pem.AsSpan());
        Assert.Equal(256, ecdsa.KeySize);
        Assert.Equal("1.2.840.10045.3.1.7", ecdsa.ExportParameters(false).Curve.Oid?.Value);

        byte[] spkiDer = ecdsa.ExportSubjectPublicKeyInfo();
        Assert.Equal(
            UpdateSigningPublicKey.ExpectedSpkiDerSha256Hex,
            Convert.ToHexStringLower(SHA256.HashData(spkiDer)));
    }

    [Fact]
    public async Task CheckForUpdates_ValidSignatureWithAllowedRedirects_DownloadsVerifiesAtomicallyPromotesAndPrunesOldCache()
    {
        await using var sandbox = new TestSandbox();
        using var testKey = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 4096);
        string installerSha256 = Sha256Hex(installerBytes);
        (byte[] manifestBytes, byte[] sigBytes) = BuildSignedManifest(testKey, version: "0.1.2", installerBytes);

        // Seed an older version directory + stray file under cache\updates\ to verify pruning leaves only the latest.
        string staleVersionDir = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.0");
        Directory.CreateDirectory(staleVersionDir);
        await File.WriteAllTextAsync(Path.Combine(staleVersionDir, "spawn-setup-0.1.0-x64.exe"), "old");
        string strayFile = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "stray.tmp");
        await File.WriteAllTextAsync(strayFile, "stray");

        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(req =>
        {
            requests.Add(req);
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl)
            {
                return Redirect(HttpStatusCode.Found, "https://objects.githubusercontent.com/r/manifest.json");
            }

            if (url == "https://objects.githubusercontent.com/r/manifest.json")
            {
                return OkBytes(manifestBytes);
            }

            if (url == UpdateServiceOptions.DefaultSignatureUrl)
            {
                return OkBytes(sigBytes);
            }

            if (url == "https://github.com/chialecode/spawn/releases/download/v0.1.2/spawn-setup-0.1.2-x64.exe")
            {
                return Redirect(HttpStatusCode.Found, "https://release-assets.githubusercontent.com/assets/spawn-setup-0.1.2-x64.exe");
            }

            if (url == "https://release-assets.githubusercontent.com/assets/spawn-setup-0.1.2-x64.exe")
            {
                return OkBytes(installerBytes);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };
        var quit = new FakeUpdateQuitCoordinator();
        var launcher = new FakeUpdateInstallerLauncher();

        await using var service = CreateService(sandbox, testKey.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
        await service.InitializeAsync();

        var notifications = new List<UpdateUserNotificationEvent>();
        service.UserNotificationRequested += (_, ev) => notifications.Add(ev);

        UpdateCheckResult result = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);

        Assert.Equal(UpdateCheckOutcome.UpdateReady, result.Outcome);
        Assert.False(result.AutoInstallTriggered);
        Assert.True(result.ShouldNotifyUser);
        Assert.NotNull(result.ReadyUpdate);
        Assert.Equal("0.1.2", result.ReadyUpdate!.Version);
        Assert.Equal(installerBytes.Length, result.ReadyUpdate.InstallerSize);
        Assert.Equal(installerSha256, result.ReadyUpdate.ExpectedSha256);

        // Verify staged file exists, .partial does not, and old version directory + stray file were pruned.
        string expectedFinalPath = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe");
        Assert.Equal(expectedFinalPath, result.ReadyUpdate.InstallerPath);
        Assert.True(File.Exists(expectedFinalPath));
        Assert.False(File.Exists(expectedFinalPath + ".partial"));
        Assert.False(Directory.Exists(staleVersionDir));
        Assert.False(File.Exists(strayFile));
        Assert.Equal(installerBytes, await File.ReadAllBytesAsync(expectedFinalPath));

        // Verify honest User-Agent and zero telemetry / cookie headers across all requests.
        Assert.NotEmpty(requests);
        foreach (HttpRequestMessage req in requests)
        {
            Assert.Equal("SPAWN-Updater/0.1.1", req.Headers.UserAgent.ToString());
            Assert.False(req.Headers.Contains("Cookie"));
            Assert.False(req.Headers.Contains("X-Machine-Id"));
            Assert.False(req.Headers.Contains("X-User-Id"));
            Assert.Single(req.Headers);
        }

        // Window open -> notified once via event, quit/launcher untouched.
        Assert.Single(notifications);
        Assert.True(notifications[0].IsUpdateReady);
        Assert.False(notifications[0].IsError);
        Assert.Empty(quit.QuitReasons);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task CheckForUpdates_TamperedManifestOrWrongPublicKey_FailsClosedWithZeroInstallerDownload()
    {
        await using var sandbox = new TestSandbox();
        using var trustedKey = CreateTestSigningKey();
        using var attackerKey = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
        (byte[] validManifest, byte[] validSig) = BuildSignedManifest(trustedKey, "0.1.2", installerBytes);

        // Case 1: Tamper 1 byte in manifest after signing.
        byte[] tamperedManifest = (byte[])validManifest.Clone();
        tamperedManifest[^2] = tamperedManifest[^2] == (byte)' ' ? (byte)'\t' : (byte)' ';

        int installerRequests = 0;
        var tamperedHandler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(tamperedManifest);
            if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(validSig);
            installerRequests++;
            return OkBytes(installerBytes);
        });

        var clock = new ManualTimeProvider();
        await using (var svc = CreateService(sandbox, trustedKey.PublicKeyPem, "release", "0.1.1", tamperedHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.VerificationFailed, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.Equal(0, installerRequests);
        }

        // Case 2: Valid signature produced by an untrusted/wrong key.
        (byte[] attackerManifest, byte[] attackerSig) = BuildSignedManifest(attackerKey, "0.1.2", installerBytes);
        var wrongKeyHandler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(attackerManifest);
            if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(attackerSig);
            installerRequests++;
            return OkBytes(installerBytes);
        });

        await using (var svc = CreateService(sandbox, trustedKey.PublicKeyPem, "release", "0.1.1", wrongKeyHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.VerificationFailed, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.Equal(0, installerRequests);
        }
    }

    [Fact]
    public async Task CheckForUpdates_RejectsDowngradeAndSameVersion_AndComparesSemVerNumerically()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.10", 512);
        var clock = new ManualTimeProvider();

        // 1. Downgrade (0.1.1 < 0.1.2) -> explicitly rejected as VerificationFailed, zero download.
        {
            int downloadCalls = 0;
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.1", installerBytes);
            var handler = new StubHttpMessageHandler(req =>
            {
                if (req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifest);
                if (req.RequestUri.AbsoluteUri == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sig);
                downloadCalls++;
                return OkBytes(installerBytes);
            });

            await using var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.2", handler, clock);
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.VerificationFailed, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.Equal(0, downloadCalls);
        }

        // 2. Same version (0.1.2 == 0.1.2) -> UpToDate, never eligible for download.
        {
            int downloadCalls = 0;
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
            var handler = new StubHttpMessageHandler(req =>
            {
                if (req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifest);
                if (req.RequestUri.AbsoluteUri == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sig);
                downloadCalls++;
                return OkBytes(installerBytes);
            });

            await using var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.2", handler, clock);
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.UpToDate, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.Equal(0, downloadCalls);
        }

        // 3. Numeric comparison: 0.1.10 > 0.1.2 (would fail lexicographic comparison!).
        {
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.10", installerBytes);
            ManifestVerificationResult verified = UpdateManifestVerifier.Verify(
                manifest,
                sig,
                key.PublicKeyPem,
                currentVersion: "0.1.2",
                new UpdateServiceOptions());
            Assert.Equal(ManifestVerificationStatus.ValidNewerVersion, verified.Status);
            Assert.Equal(new UpdateSemanticVersion(0, 1, 10), verified.ParsedVersion);
        }
    }

    [Theory]
    [InlineData("http://github.com/chialecode/spawn/releases/latest/download/update-windows-x64.json")]
    [InlineData("https://evil.example.com/update-windows-x64.json")]
    [InlineData("https://github.com.evil.com/update-windows-x64.json")]
    [InlineData("https://evil-github.com/update-windows-x64.json")]
    [InlineData("https://github.com:8443/chialecode/spawn/releases/latest/download/update-windows-x64.json")]
    public async Task NetworkBoundaries_InitialUrlViolations_RejectedBeforeAnyHttpCall(string badInitialUrl)
    {
        int calls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            calls++;
            return OkBytes("{}"u8.ToArray());
        });

        using var fetcher = new UpdateHttpFetcher(new UpdateServiceOptions(), "0.1.1", handler);
        await Assert.ThrowsAsync<UpdatePolicyViolationException>(
            () => fetcher.DownloadBytesAsync(new Uri(badInitialUrl), maxBytes: 4096));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("http://objects.githubusercontent.com/downgrade-http")]
    [InlineData("https://evil.example.com/hijack")]
    [InlineData("https://release-assets.githubusercontent.com.attacker.test/hijack")]
    public async Task NetworkBoundaries_RedirectHopViolations_RejectedOnEveryHop(string badRedirectTarget)
    {
        int calls = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            calls++;
            if (req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl)
            {
                return Redirect(HttpStatusCode.Found, badRedirectTarget);
            }

            return OkBytes("{}"u8.ToArray());
        });

        using var fetcher = new UpdateHttpFetcher(new UpdateServiceOptions(), "0.1.1", handler);
        await Assert.ThrowsAsync<UpdatePolicyViolationException>(
            () => fetcher.DownloadBytesAsync(new Uri(UpdateServiceOptions.DefaultManifestUrl), maxBytes: 4096));
        Assert.Equal(1, calls); // Only the initial request was sent; redirect target was never contacted!
    }

    [Fact]
    public async Task NetworkBoundaries_MaxFiveRedirectsAllowed_SixthRedirectRejected()
    {
        static StubHttpMessageHandler BuildRedirectChain(int redirectCount) =>
            new(req =>
            {
                string url = req.RequestUri!.AbsoluteUri;
                if (url == UpdateServiceOptions.DefaultManifestUrl)
                {
                    return Redirect(HttpStatusCode.Found, "https://objects.githubusercontent.com/hop/1");
                }

                for (int i = 1; i < redirectCount; i++)
                {
                    if (url == $"https://objects.githubusercontent.com/hop/{i}")
                    {
                        return Redirect(HttpStatusCode.TemporaryRedirect, $"https://release-assets.githubusercontent.com/hop/{i + 1}");
                    }

                    if (url == $"https://release-assets.githubusercontent.com/hop/{i}")
                    {
                        return Redirect(HttpStatusCode.Found, $"https://objects.githubusercontent.com/hop/{i + 1}");
                    }
                }

                return OkBytes("ok"u8.ToArray());
            });

        // Exactly 5 redirects -> succeeds.
        using (var okFetcher = new UpdateHttpFetcher(new UpdateServiceOptions { MaxRedirects = 5 }, "0.1.1", BuildRedirectChain(5)))
        {
            byte[] payload = await okFetcher.DownloadBytesAsync(new Uri(UpdateServiceOptions.DefaultManifestUrl), maxBytes: 4096);
            Assert.Equal("ok", Encoding.UTF8.GetString(payload));
        }

        // 6th redirect -> rejected!
        using (var failFetcher = new UpdateHttpFetcher(new UpdateServiceOptions { MaxRedirects = 5 }, "0.1.1", BuildRedirectChain(6)))
        {
            await Assert.ThrowsAsync<UpdatePolicyViolationException>(
                () => failFetcher.DownloadBytesAsync(new Uri(UpdateServiceOptions.DefaultManifestUrl), maxBytes: 4096));
        }
    }

    [Fact]
    public async Task NetworkBoundaries_StreamingSizeCaps_RejectOversizedManifestSignatureAndInstaller()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        var clock = new ManualTimeProvider();

        // 1. Manifest > 64 KiB (even without Content-Length header, chunked streaming enforces cap!).
        byte[] oversizedManifest = new byte[(64 * 1024) + 1];
        Array.Fill(oversizedManifest, (byte)'a');
        var manifestTooBigHandler = new StubHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new UnboundedStreamContent(oversizedManifest),
            });

        await using (var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", manifestTooBigHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.VerificationFailed, res.Outcome);
        }

        // 2. Signature > 4 KiB.
        byte[] oversizedSig = new byte[(4 * 1024) + 1];
        var sigTooBigHandler = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl
                ? OkBytes("{}"u8.ToArray())
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnboundedStreamContent(oversizedSig) });

        await using (var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", sigTooBigHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.VerificationFailed, res.Outcome);
        }

        // 3a. Manifest declaring installer > 300 MiB rejected at verification stage.
        byte[] dummyBody = BuildSyntheticInstallerBytes("0.1.2", 256);
        (byte[] bigManifest, byte[] bigSig) = BuildSignedManifest(
            key,
            "0.1.2",
            dummyBody,
            overrideSize: (300L * 1024 * 1024) + 1);
        ManifestVerificationResult verRes = UpdateManifestVerifier.Verify(
            bigManifest,
            bigSig,
            key.PublicKeyPem,
            "0.1.1",
            new UpdateServiceOptions());
        Assert.Equal(ManifestVerificationStatus.InstallerSizeExceedsCap, verRes.Status);

        // 3b. Streaming installer download exceeding configured cap rejected & .partial deleted!
        byte[] validSmallInstaller = BuildSyntheticInstallerBytes("0.1.2", 2048);
        byte[] oversizedStreamBody = new byte[4096];
        (byte[] capManifest, byte[] capSig) = BuildSignedManifest(key, "0.1.2", validSmallInstaller);
        var installerCapHandler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(capManifest);
            if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(capSig);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnboundedStreamContent(oversizedStreamBody) };
        });

        var tightCapOptions = new UpdateServiceOptions
        {
            ChannelOverride = "release",
            CurrentVersionOverride = "0.1.1",
            PublicKeyPem = key.PublicKeyPem,
            MaxInstallerBytes = 3072, // Stream sends 4096 > 3072 without Content-Length!
        };

        await using (var svc = new UpdateService(
            sandbox.Paths,
            sandbox.Database,
            new FakeUpdateIdleEvaluator(),
            new FakeUpdateQuitCoordinator(),
            new FakeUpdateInstallerLauncher(),
            clock,
            tightCapOptions,
            installerCapHandler))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.DownloadFailed, res.Outcome);
            string partialPath = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe.partial");
            Assert.False(File.Exists(partialPath));
        }
    }

    [Theory]
    [InlineData("spawn-setup-0.1.2-x64.exe", "https://github.com/chialecode/spawn/releases/download/v0.1.1/spawn-setup-0.1.2-x64.exe")]
    [InlineData("spawn-setup-0.1.2-x64.exe", "https://github.com/evil/spawn/releases/download/v0.1.2/spawn-setup-0.1.2-x64.exe")]
    [InlineData("spawn-setup-0.1.2-x64.exe", "https://github.com/chialecode/spawn/releases/download/v0.1.2/spawn-setup-0.1.2-x64.exe?extra=1")]
    [InlineData("..\\escape.exe", "https://github.com/chialecode/spawn/releases/download/v0.1.2/..\\escape.exe")]
    [InlineData("payload.bat", "https://github.com/chialecode/spawn/releases/download/v0.1.2/payload.bat")]
    public void ManifestVerifier_BadUrlPrefixOrUnsafeFileName_Rejected(string installerName, string installerUrl)
    {
        using var key = CreateTestSigningKey();
        byte[] body = BuildSyntheticInstallerBytes("0.1.2", 512);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(
            key,
            "0.1.2",
            body,
            overrideName: installerName,
            overrideUrl: installerUrl);

        ManifestVerificationResult result = UpdateManifestVerifier.Verify(
            manifest,
            sig,
            key.PublicKeyPem,
            "0.1.1",
            new UpdateServiceOptions());

        Assert.False(result.IsEligibleForDownload);
        Assert.True(
            result.Status is ManifestVerificationStatus.InvalidInstallerUrl
                or ManifestVerificationStatus.InvalidInstallerMetadata,
            $"Unexpected verification status: {result.Status}");
    }

    [Fact]
    public async Task CheckForUpdates_SizeOrSha256Mismatch_RejectsAndDeletesPartialFile()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] expectedBody = BuildSyntheticInstallerBytes("0.1.2", 1024);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", expectedBody);
        var clock = new ManualTimeProvider();

        // 1. Size mismatch (truncated body).
        byte[] truncatedBody = expectedBody[..512];
        var truncatedHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifest);
            if (req.RequestUri.AbsoluteUri == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sig);
            return OkBytes(truncatedBody);
        });

        await using (var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", truncatedHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.DownloadFailed, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.False(File.Exists(Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe")));
            Assert.False(File.Exists(Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe.partial")));
        }

        // 2. Same size, corrupted content (SHA-256 mismatch).
        byte[] corruptedBody = (byte[])expectedBody.Clone();
        corruptedBody[128] ^= 0xFF;
        var corruptedHandler = new StubHttpMessageHandler(req =>
        {
            if (req.RequestUri!.AbsoluteUri == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifest);
            if (req.RequestUri.AbsoluteUri == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sig);
            return OkBytes(corruptedBody);
        });

        await using (var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", corruptedHandler, clock))
        {
            UpdateCheckResult res = await svc.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.DownloadFailed, res.Outcome);
            Assert.Null(res.ReadyUpdate);
            Assert.False(File.Exists(Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe")));
        }
    }

    [Fact]
    public async Task InstallReadyUpdate_EnforcesToctouReadLockAcrossQuitAndLaunch_AndPassesSpawnRelaunchArgs()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 2048);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
        var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
        var clock = new ManualTimeProvider();
        var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };

        bool writeDeniedDuringQuit = false;
        bool writeDeniedDuringLaunch = false;
        bool readAllowedDuringLaunch = false;
        string? stagedPath = null;

        var quit = new FakeUpdateQuitCoordinator
        {
            OnQuitRequested = _ =>
            {
                // Write attempt while quit preparation runs MUST fail with IOException (FileShare.Read lock held!).
                writeDeniedDuringQuit = IsWriteLocked(stagedPath!);
            },
        };

        var launcher = new FakeUpdateInstallerLauncher
        {
            OnLaunch = (path, _) =>
            {
                // Write attempt during ShellExecute launch MUST fail, while read share MUST succeed!
                writeDeniedDuringLaunch = IsWriteLocked(path);
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                readAllowedDuringLaunch = reader.Length == installerBytes.Length;
                return true;
            },
        };

        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
        UpdateCheckResult check = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
        Assert.Equal(UpdateCheckOutcome.UpdateReady, check.Outcome);
        stagedPath = check.ReadyUpdate!.InstallerPath;

        UpdateInstallResult install = await service.InstallReadyUpdateAsync(UpdateTriggerKind.Manual);

        Assert.True(install.Succeeded);
        Assert.True(writeDeniedDuringQuit, "Installer handle must remain write-locked during RequestQuitForUpdateAsync.");
        Assert.True(writeDeniedDuringLaunch, "Installer handle must remain write-locked during LaunchSilentInstaller.");
        Assert.True(readAllowedDuringLaunch, "Installer handle must allow concurrent read/image mapping (FileShare.Read).");

        // Handle must be released after launch succeeds!
        Assert.False(IsWriteLocked(stagedPath), "Installer lock handle must be closed after installer process launches.");

        Assert.Single(quit.QuitReasons);
        Assert.Equal(UpdateRestartReason.Update, quit.QuitReasons[0]);
        Assert.Equal(1, quit.CompleteShutdownCount);
        Assert.Equal(0, quit.AbortShutdownCount);

        Assert.Single(launcher.Launches);
        Assert.Equal(stagedPath, launcher.Launches[0].Path);
        Assert.Equal("/S /RELAUNCH", launcher.Launches[0].Arguments);
    }

    [Fact]
    public async Task InstallReadyUpdate_TamperedOnDiskAfterStaging_AbortsBeforeQuitOrLaunch()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
        var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
        var clock = new ManualTimeProvider();
        var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };
        var quit = new FakeUpdateQuitCoordinator();
        var launcher = new FakeUpdateInstallerLauncher();

        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
        UpdateCheckResult check = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
        Assert.Equal(UpdateCheckOutcome.UpdateReady, check.Outcome);

        // Simulate post-download tampering on disk before install!
        byte[] tampered = (byte[])installerBytes.Clone();
        tampered[42] ^= 0x5A;
        await File.WriteAllBytesAsync(check.ReadyUpdate!.InstallerPath, tampered);

        UpdateInstallResult install = await service.InstallReadyUpdateAsync(UpdateTriggerKind.Manual);

        Assert.False(install.Succeeded);
        Assert.Empty(quit.QuitReasons);
        Assert.Empty(launcher.Launches);
        Assert.Null(service.CurrentSnapshot.ReadyUpdate);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("source")]
    public async Task NonReleaseChannels_ZeroHttpCallsAndZeroScheduledChecks(string channel)
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        int httpCalls = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            httpCalls++;
            return OkBytes("{}"u8.ToArray());
        });

        var clock = new ManualTimeProvider();
        await using var service = CreateService(sandbox, key.PublicKeyPem, channel, "0.1.1-dev.202611220000", handler, clock);
        await service.InitializeAsync();

        Assert.False(service.CurrentSnapshot.IsUpdateSupported);
        Assert.Equal(UpdateServiceState.DisabledDevChannel, service.CurrentSnapshot.State);
        Assert.Equal(UpdateService.DevChannelStatusMessage, service.CurrentSnapshot.StatusMessage);

        // Starting scheduler and advancing beyond 60s + 24h MUST NOT issue a single HTTP request!
        service.StartBackgroundScheduler();
        clock.Advance(TimeSpan.FromSeconds(61));
        clock.Advance(TimeSpan.FromHours(25));
        await Task.Delay(50);

        UpdateCheckResult manual = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
        Assert.Equal(UpdateCheckOutcome.DisabledDevChannel, manual.Outcome);
        Assert.Equal(UpdateService.DevChannelStatusMessage, manual.Message);
        Assert.Equal(0, httpCalls);
    }

    [Fact]
    public async Task IdleAndInstallTiming_GatesAutoInstallOnAllFourBusyConditionsWindowOpenAndNotifyOnlyMode()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
        var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
        var clock = new ManualTimeProvider();
        var idle = new FakeUpdateIdleEvaluator();
        var quit = new FakeUpdateQuitCoordinator();
        var launcher = new FakeUpdateInstallerLauncher();

        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
        await service.InitializeAsync();

        // Q-002 defaults: AutoCheckEnabled=true, AutoInstallWhenIdle=true.
        Assert.True(service.CurrentSnapshot.Settings.AutoCheckEnabled);
        Assert.True(service.CurrentSnapshot.Settings.AutoInstallWhenIdle);

        // Stage update while main window is open -> does NOT auto-install!
        idle.Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true };
        UpdateCheckResult check = await service.CheckForUpdatesAsync(UpdateTriggerKind.Auto);
        Assert.Equal(UpdateCheckOutcome.UpdateReady, check.Outcome);
        Assert.False(check.AutoInstallTriggered);
        Assert.Empty(launcher.Launches);

        // Busy Condition 1: tracked game running (even when tray-only).
        idle.Snapshot = new UpdateIdleSnapshot { HasRunningTrackedGame = true, IsMainWindowOpen = false };
        Assert.False((await service.TryAutoInstallIfEligibleAsync()).Succeeded);

        // Busy Condition 2: trainer session active.
        idle.Snapshot = new UpdateIdleSnapshot { HasActiveTrainerSession = true, IsMainWindowOpen = false };
        Assert.False((await service.TryAutoInstallIfEligibleAsync()).Succeeded);

        // Busy Condition 3: save snapshot or rollback in progress.
        idle.Snapshot = new UpdateIdleSnapshot { HasActiveSaveSnapshotOrRollback = true, IsMainWindowOpen = false };
        Assert.False((await service.TryAutoInstallIfEligibleAsync()).Succeeded);

        // Busy Condition 4: game-data cleanup in progress.
        idle.Snapshot = new UpdateIdleSnapshot { HasActiveGameDataCleanup = true, IsMainWindowOpen = false };
        Assert.False((await service.TryAutoInstallIfEligibleAsync()).Succeeded);

        // Condition 5: notify-only mode (AutoInstallWhenIdle == false) even when completely idle & tray-only!
        idle.Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = false };
        await service.SaveSettingsAsync(new UpdateSettingsDto { AutoCheckEnabled = true, AutoInstallWhenIdle = false });
        Assert.False((await service.TryAutoInstallIfEligibleAsync()).Succeeded);
        Assert.Empty(launcher.Launches);

        // Now re-enable AutoInstallWhenIdle == true while idle & tray-only -> DOES auto-install!
        await service.SaveSettingsAsync(new UpdateSettingsDto { AutoCheckEnabled = true, AutoInstallWhenIdle = true });
        UpdateInstallResult autoInstall = await service.TryAutoInstallIfEligibleAsync();
        Assert.True(autoInstall.Succeeded);
        Assert.Single(quit.QuitReasons);
        Assert.Equal(UpdateRestartReason.Update, quit.QuitReasons[0]);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public async Task ActivityTrackingDecorators_MarkBusyDuringSnapshotRollbackAndGameDataCleanup()
    {
        var tracker = new UpdateActivityTracker();
        Assert.False(tracker.IsSaveSnapshotOrRollbackInProgress);
        Assert.False(tracker.IsGameDataCleanupInProgress);

        bool observedSnapshotBusy = false;
        bool observedRestoreBusy = false;
        var snapDecorator = new ActivityTrackingPreTrainerSnapshotService(
            new ProbeSnapshotService(
                onCreate: () => observedSnapshotBusy = tracker.IsSaveSnapshotOrRollbackInProgress,
                onRestore: () => observedRestoreBusy = tracker.IsSaveSnapshotOrRollbackInProgress),
            tracker);

        await snapDecorator.CreateAsync("g1", "t1");
        await snapDecorator.RestoreAsync("s1");
        Assert.True(observedSnapshotBusy);
        Assert.True(observedRestoreBusy);
        Assert.False(tracker.IsSaveSnapshotOrRollbackInProgress);

        bool observedCleanBusy = false;
        bool observedUndoBusy = false;
        var dataDecorator = new ActivityTrackingGameDataManagementService(
            new ProbeGameDataService(
                onClean: () => observedCleanBusy = tracker.IsGameDataCleanupInProgress,
                onUndo: () => observedUndoBusy = tracker.IsGameDataCleanupInProgress),
            tracker);

        await dataDecorator.CleanGameDataAsync(new GameDataCleanRequest { GameId = "g1", EntryIds = [] });
        await dataDecorator.RestoreLastCleanupAsync("g1");
        Assert.True(observedCleanBusy);
        Assert.True(observedUndoBusy);
        Assert.False(tracker.IsGameDataCleanupInProgress);
    }

    [Fact]
    public async Task HistoryPersistence_CapsAtTwentyEntriesInAppSettingsWithoutSchemaUpgrade()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] dummyBody = BuildSyntheticInstallerBytes("0.1.1", 128);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.1", dummyBody);
        var handler = BuildServingHandler(manifest, sig, "0.1.1", dummyBody);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));

        await using (var svc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock))
        {
            await svc.InitializeAsync();
            for (int i = 0; i < 25; i++)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
                UpdateCheckResult res = await svc.CheckForUpdatesAsync(i % 2 == 0 ? UpdateTriggerKind.Auto : UpdateTriggerKind.Manual);
                Assert.Equal(UpdateCheckOutcome.UpToDate, res.Outcome);
            }

            Assert.Equal(20, svc.CurrentSnapshot.History.Count);
            Assert.True(svc.CurrentSnapshot.History[0].TimestampUtc > svc.CurrentSnapshot.History[^1].TimestampUtc);
        }

        // Reload in a fresh UpdateService instance -> settings, check times, and all 20 entries restored from app_settings.
        await using (var reloaded = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock))
        {
            await reloaded.InitializeAsync();
            Assert.Equal(20, reloaded.CurrentSnapshot.History.Count);
            Assert.NotNull(reloaded.CurrentSnapshot.LastCheckUtc);
            Assert.Equal(UpdateCheckOutcome.UpToDate, reloaded.CurrentSnapshot.LastCheckOutcome);
        }

        // Confirm SQLite schema version stayed at v4 (NO schema upgrade!).
        await using var conn = new SqliteConnection($"Data Source={sandbox.Paths.DatabaseFilePath};Mode=ReadOnly;");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        long maxVersion = Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        Assert.Equal(4L, maxVersion);
    }

    [Fact]
    public async Task InstallFailure_RecordsHistoryAbortsQuitAndDoesNotRepeatSpamSameVersion()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
        (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
        var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
        var clock = new ManualTimeProvider();
        var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };
        var quit = new FakeUpdateQuitCoordinator();
        var launcher = new FakeUpdateInstallerLauncher
        {
            OnLaunch = (_, _) => false, // Simulate ShellExecute launch failure!
            ErrorMessage = "Simulated ShellExecute failure",
        };

        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
        await service.InitializeAsync();
        await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);

        var errorNotifications = new List<UpdateUserNotificationEvent>();
        service.UserNotificationRequested += (_, ev) =>
        {
            if (ev.IsError)
            {
                errorNotifications.Add(ev);
            }
        };

        UpdateInstallResult firstFail = await service.InstallReadyUpdateAsync(UpdateTriggerKind.Manual);
        UpdateInstallResult secondFail = await service.InstallReadyUpdateAsync(UpdateTriggerKind.Manual);

        Assert.False(firstFail.Succeeded);
        Assert.True(firstFail.ShouldNotifyUser);
        Assert.False(secondFail.Succeeded);
        Assert.False(secondFail.ShouldNotifyUser);

        // Notified strictly ONCE for version 0.1.2 despite two failures!
        Assert.Single(errorNotifications);
        Assert.Equal("0.1.2", errorNotifications[0].TargetVersion);
        Assert.Equal(2, quit.AbortShutdownCount);
        Assert.Equal(0, quit.CompleteShutdownCount);

        // Both failures are recorded in history.
        Assert.Equal(2, service.CurrentSnapshot.History.Count(h => h.Result == UpdateHistoryResult.Failed && h.ToVersion == "0.1.2"));
    }

    [Fact]
    public async Task BackgroundScheduleAndOfflineDeferral_RespectsSixtySecondStartupAndTwentyFourHourIntervalSilently()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        int httpCalls = 0;
        var firstCheckDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheckDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var offlineHandler = new StubHttpMessageHandler(_ =>
        {
            int callNum = Interlocked.Increment(ref httpCalls);
            if (callNum == 1) firstCheckDone.TrySetResult(true);
            if (callNum == 2) secondCheckDone.TrySetResult(true);
            throw new HttpRequestException("Simulated offline network");
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", offlineHandler, clock);
        await service.InitializeAsync();

        var notifications = new List<UpdateUserNotificationEvent>();
        int deferredStateCount = 0;
        service.UserNotificationRequested += (_, ev) => notifications.Add(ev);
        service.StateChanged += (_, snap) =>
        {
            if (snap.State == UpdateServiceState.DeferredOffline)
            {
                Interlocked.Increment(ref deferredStateCount);
            }
        };

        service.StartBackgroundScheduler();

        // At +59s, startup delay (60s) has not elapsed yet -> 0 calls.
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(30);
        Assert.Equal(0, Volatile.Read(ref httpCalls));

        // At +60s -> first scheduled check fires and silently defers on offline error.
        clock.Advance(TimeSpan.FromSeconds(1));
        await firstCheckDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Wait until the loop arms the next 24h timer and emits the post-arm StateChanged snapshot (count >= 2).
        await WaitUntilAsync(() => Volatile.Read(ref deferredStateCount) >= 2);

        Assert.Equal(UpdateServiceState.DeferredOffline, service.CurrentSnapshot.State);
        Assert.Empty(notifications); // Offline errors silently defer with zero notifications!

        // At +24h -> second scheduled check fires.
        clock.Advance(TimeSpan.FromHours(24));
        await secondCheckDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Volatile.Read(ref httpCalls) >= 2);

        // Disabling AutoCheckEnabled stops future automatic checks.
        await service.SaveSettingsAsync(new UpdateSettingsDto { AutoCheckEnabled = false, AutoInstallWhenIdle = true });
        await Task.Delay(30);
        int callsAfterDisable = Volatile.Read(ref httpCalls);
        clock.Advance(TimeSpan.FromHours(48));
        await Task.Delay(30);
        Assert.Equal(callsAfterDisable, Volatile.Read(ref httpCalls));
    }

    [Fact]
    public async Task BackgroundAutoInstall_ReentrantStopBackgroundScheduler_CompletesFastWithoutDeadlockAndCallsCompleteDeferredShutdown()
    {
        // Scenario 1: Triggered directly from RunBackgroundCheckLoopAsync (60s startup delay -> auto check + auto install).
        {
            await using var sandbox = new TestSandbox();
            using var key = CreateTestSigningKey();
            byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
            var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
            var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = false } };

            var completeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quit = new FakeUpdateQuitCoordinator
            {
                OnCompleteShutdown = () => completeTcs.TrySetResult(true),
            };
            var launcher = new FakeUpdateInstallerLauncher();
            TimeSpan reentrantStopElapsed = TimeSpan.MaxValue;

            await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
            quit.OnQuitRequestedAsync = async _ =>
            {
                // Mirrors App.PrepareGracefulShutdownForUpdateAsync: calls StopBackgroundSchedulerAsync() re-entrantly!
                var stopSw = System.Diagnostics.Stopwatch.StartNew();
                await service.StopBackgroundSchedulerAsync().WaitAsync(TimeSpan.FromSeconds(10));
                stopSw.Stop();
                reentrantStopElapsed = stopSw.Elapsed;
            };

            await service.InitializeAsync();
            service.StartBackgroundScheduler();
            Assert.Equal(2, clock.PendingTimerCount);

            clock.Advance(TimeSpan.FromSeconds(60));
            await completeTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(reentrantStopElapsed < TimeSpan.FromSeconds(5), $"Re-entrant StopBackgroundSchedulerAsync stalled for {reentrantStopElapsed}.");
            Assert.Equal(1, quit.CompleteShutdownCount);
            Assert.Equal(0, quit.AbortShutdownCount);
            Assert.Single(quit.QuitReasons);
            Assert.Equal(UpdateRestartReason.Update, quit.QuitReasons[0]);
            Assert.Single(launcher.Launches);
            Assert.Contains(service.CurrentSnapshot.History, h => h.Result == UpdateHistoryResult.InstallLaunched && h.ToVersion == "0.1.2");

            // Confirm InstallLaunched was actually persisted to SQLite app_settings.
            await using var verifierSvc = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock);
            await verifierSvc.InitializeAsync();
            Assert.Contains(verifierSvc.CurrentSnapshot.History, h => h.Result == UpdateHistoryResult.InstallLaunched && h.ToVersion == "0.1.2");
        }

        // Scenario 2: Triggered from RunIdleInstallLoopAsync after window closes to tray (including failure recovery with cancelled loop token!).
        {
            await using var sandbox = new TestSandbox();
            using var key = CreateTestSigningKey();
            byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
            var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
            var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };

            var completeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quit = new FakeUpdateQuitCoordinator
            {
                OnCompleteShutdown = () => completeTcs.TrySetResult(true),
            };
            var launcher = new FakeUpdateInstallerLauncher();
            TimeSpan reentrantStopElapsed = TimeSpan.MaxValue;

            await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
            quit.OnQuitRequestedAsync = async _ =>
            {
                // Even if ExecutionContext is suppressed (simulating cross-thread dispatcher), StopBackgroundSchedulerAsync must not deadlock!
                var stopSw = System.Diagnostics.Stopwatch.StartNew();
                await Task.Run(() => service.StopBackgroundSchedulerAsync()).WaitAsync(TimeSpan.FromSeconds(10));
                stopSw.Stop();
                reentrantStopElapsed = stopSw.Elapsed;
            };

            await service.InitializeAsync();
            UpdateCheckResult staged = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
            Assert.Equal(UpdateCheckOutcome.UpdateReady, staged.Outcome);

            service.StartBackgroundScheduler();
            Assert.Equal(2, clock.PendingTimerCount);

            // Window closes to tray -> advance 2 minutes so RunIdleInstallLoopAsync fires!
            idle.Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = false };
            clock.Advance(TimeSpan.FromMinutes(2));
            await completeTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(reentrantStopElapsed < TimeSpan.FromSeconds(5), $"Idle-loop re-entrant StopBackgroundSchedulerAsync stalled for {reentrantStopElapsed}.");
            Assert.Equal(1, quit.CompleteShutdownCount);
            Assert.Equal(0, quit.AbortShutdownCount);
            Assert.Single(launcher.Launches);
            Assert.Contains(service.CurrentSnapshot.History, h => h.Result == UpdateHistoryResult.InstallLaunched && h.ToVersion == "0.1.2");
        }

        // Scenario 3: Launch fails after re-entrant StopBackgroundSchedulerAsync cancelled loop token -> AbortDeferredShutdownAsync + Failed history persisted!
        {
            await using var sandbox = new TestSandbox();
            using var key = CreateTestSigningKey();
            byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 1024);
            (byte[] manifest, byte[] sig) = BuildSignedManifest(key, "0.1.2", installerBytes);
            var handler = BuildServingHandler(manifest, sig, "0.1.2", installerBytes);
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
            var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = false } };

            var abortTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quit = new FakeUpdateQuitCoordinator
            {
                OnAbortShutdown = () => abortTcs.TrySetResult(true),
            };
            var launcher = new FakeUpdateInstallerLauncher
            {
                OnLaunch = (_, _) => false,
                ErrorMessage = "Simulated re-entrant launch failure",
            };

            await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle, quit, launcher);
            quit.OnQuitRequestedAsync = async _ =>
            {
                await service.StopBackgroundSchedulerAsync().WaitAsync(TimeSpan.FromSeconds(10));
            };

            await service.InitializeAsync();
            service.StartBackgroundScheduler();
            Assert.Equal(2, clock.PendingTimerCount);

            clock.Advance(TimeSpan.FromSeconds(60));
            await abortTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitUntilAsync(() => service.CurrentSnapshot.History.Any(h => h.Result == UpdateHistoryResult.Failed && h.ToVersion == "0.1.2"));

            Assert.Equal(0, quit.CompleteShutdownCount);
            Assert.Equal(1, quit.AbortShutdownCount);
            Assert.Contains(service.CurrentSnapshot.History, h => h.Result == UpdateHistoryResult.Failed && h.ToVersion == "0.1.2");
        }
    }

    [Fact]
    public async Task AfterUpdateStartup_RestoresWindowOrTrayStateFromRestartMarkerAndLogsCompletedTransition()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        DateTimeOffset now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        // 1. --after-update with valid reason="update" marker (WindowOpen=false -> restores tray-only!).
        UpdateRestartStateStore.SaveMarker(
            sandbox.Paths.UpdateRestartStateFilePath,
            new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: false, FromVersion: "0.1.1", AtUtc: now));

        var afterUpdateOpts = SpawnCommandLineOptions.Parse(["--after-update"]);
        StartupWindowDecision trayDecision = UpdateRestartStateStore.ConsumeAndDecide(
            sandbox.Paths.UpdateRestartStateFilePath,
            afterUpdateOpts,
            now.AddMinutes(2));

        Assert.True(trayDecision.ConsumedValidRestartMarker);
        Assert.Equal(UpdateRestartReason.Update, trayDecision.ConsumedRestartReason);
        Assert.False(trayDecision.OpenWindow);
        Assert.Equal("0.1.1", trayDecision.PreviousVersion);
        Assert.Equal(
            UpdateInstallMethod.AutoUpdate,
            StartupWindowPolicy.ClassifyVersionChange("release", trayDecision.PreviousVersion, "0.1.2", trayDecision));

        // 2. --after-update with valid reason="update" marker (WindowOpen=true -> reopens main window!).
        UpdateRestartStateStore.SaveMarker(
            sandbox.Paths.UpdateRestartStateFilePath,
            new UpdateRestartMarker(UpdateRestartReason.Update, WindowOpen: true, FromVersion: "0.1.1", AtUtc: now));

        StartupWindowDecision windowDecision = UpdateRestartStateStore.ConsumeAndDecide(
            sandbox.Paths.UpdateRestartStateFilePath,
            afterUpdateOpts,
            now.AddMinutes(1));
        Assert.True(windowDecision.OpenWindow);

        // 3. dev-install + --background stays tray-only (existing behavior preserved!).
        UpdateRestartStateStore.SaveMarker(
            sandbox.Paths.UpdateRestartStateFilePath,
            new UpdateRestartMarker(UpdateRestartReason.DevInstall, WindowOpen: true, FromVersion: "0.1.1-dev.1", AtUtc: now));

        StartupWindowDecision devBgDecision = UpdateRestartStateStore.ConsumeAndDecide(
            sandbox.Paths.UpdateRestartStateFilePath,
            SpawnCommandLineOptions.Parse(["--background"]),
            now.AddSeconds(30));
        Assert.False(devBgDecision.OpenWindow);

        // 4. InitializeAsync records Completed history and prunes cached installer after update restart.
        string oldCacheDir = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2");
        Directory.CreateDirectory(oldCacheDir);
        await File.WriteAllBytesAsync(Path.Combine(oldCacheDir, "spawn-setup-0.1.2-x64.exe"), [1, 2, 3]);

        var clock = new ManualTimeProvider(now.AddMinutes(2));
        await using var service = CreateService(
            sandbox,
            key.PublicKeyPem,
            "release",
            currentVersion: "0.1.2",
            new StubHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP expected on post-update init")),
            clock);

        await service.InitializeAsync(
            launchedAfterUpdate: true,
            consumedValidUpdateMarker: true,
            previousVersionFromMarker: "0.1.1");

        Assert.Equal(UpdateServiceState.UpToDate, service.CurrentSnapshot.State);
        Assert.False(Directory.Exists(oldCacheDir));
        Assert.Single(service.CurrentSnapshot.History);
        Assert.Equal(UpdateHistoryResult.Completed, service.CurrentSnapshot.History[0].Result);
        Assert.Equal("0.1.1", service.CurrentSnapshot.History[0].FromVersion);
        Assert.Equal("0.1.2", service.CurrentSnapshot.History[0].ToVersion);
    }

    // -------------------------------------------------------------------------
    // F-01 (M3 v1.1 review): timeouts / cancellation never leave Checking/Downloading stuck or crash.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task F01_ManifestRequestHangs_InternalHttpTimeout_DefersOfflineAndSettlesState()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        int calls = 0;
        var hangingHandler = new HangingHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return null; // Never send headers: hang until the request token is cancelled.
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        await using var service = CreateService(
            sandbox,
            key.PublicKeyPem,
            "release",
            "0.1.1",
            hangingHandler,
            clock,
            httpRequestTimeout: TimeSpan.FromMilliseconds(200));
        await service.InitializeAsync();

        UpdateCheckResult result = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(UpdateCheckOutcome.DeferredOffline, result.Outcome);
        UpdateStateSnapshot snapshot = service.CurrentSnapshot;
        Assert.NotEqual(UpdateServiceState.Checking, snapshot.State);
        Assert.Equal(UpdateServiceState.DeferredOffline, snapshot.State);
        Assert.Null(snapshot.DownloadProgressPercent);
        Assert.Equal(UpdateCheckOutcome.DeferredOffline, snapshot.LastCheckOutcome);
        UpdateHistoryEntryDto entry = Assert.Single(snapshot.History);
        Assert.Equal(UpdateHistoryResult.DeferredOffline, entry.Result);
        Assert.Contains("超时", entry.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F01_InstallerDownloadHangs_InternalDownloadTimeout_DefersOfflineDeletesPartialAndSettlesState()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();
        byte[] installerBytes = BuildSyntheticInstallerBytes("0.1.2", 4096);
        (byte[] manifestBytes, byte[] sigBytes) = BuildSignedManifest(key, version: "0.1.2", installerBytes);
        string installerUrl = "https://github.com/chialecode/spawn/releases/download/v0.1.2/spawn-setup-0.1.2-x64.exe";
        string finalPath = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.2", "spawn-setup-0.1.2-x64.exe");
        string partialPath = finalPath + ".partial";
        bool partialObservedDuringDownload = false;

        var handler = new HangingHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifestBytes);
            if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sigBytes);
            if (url == installerUrl)
            {
                // Headers arrive immediately; the body streams a 1 KiB prefix then stalls until cancelled.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StallingStreamContent(
                        installerBytes.AsSpan(0, 1024).ToArray(),
                        onStall: () => partialObservedDuringDownload = File.Exists(partialPath)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        await using var service = CreateService(
            sandbox,
            key.PublicKeyPem,
            "release",
            "0.1.1",
            handler,
            clock,
            downloadTimeout: TimeSpan.FromMilliseconds(300));
        await service.InitializeAsync();

        var observedStates = new List<UpdateServiceState>();
        service.StateChanged += (_, snap) =>
        {
            lock (observedStates)
            {
                observedStates.Add(snap.State);
            }
        };

        UpdateCheckResult result = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UpdateCheckOutcome.DeferredOffline, result.Outcome);
        Assert.True(partialObservedDuringDownload, ".partial should exist while the body is streaming.");
        Assert.False(File.Exists(partialPath), ".partial must be deleted after the internal download timeout.");
        Assert.False(File.Exists(finalPath));

        lock (observedStates)
        {
            Assert.Contains(UpdateServiceState.Downloading, observedStates);
        }

        // Late Progress<T> callbacks must not resurrect Downloading after the deferral settled.
        await Task.Delay(100);
        UpdateStateSnapshot snapshot = service.CurrentSnapshot;
        Assert.NotEqual(UpdateServiceState.Downloading, snapshot.State);
        Assert.NotEqual(UpdateServiceState.Checking, snapshot.State);
        Assert.Equal(UpdateServiceState.DeferredOffline, snapshot.State);
        Assert.Null(snapshot.DownloadProgressPercent);
        Assert.Null(snapshot.ReadyUpdate);
        Assert.Contains(snapshot.History, h => h.Result == UpdateHistoryResult.DeferredOffline);
    }

    [Fact]
    public async Task F01_CallerCancellation_RestoresIdleOrReadyToInstallAndRethrows()
    {
        await using var sandbox = new TestSandbox();
        using var key = CreateTestSigningKey();

        byte[] installer012 = BuildSyntheticInstallerBytes("0.1.2", 2048);
        (byte[] manifest012, byte[] sig012) = BuildSignedManifest(key, version: "0.1.2", installer012);
        byte[] installer013 = BuildSyntheticInstallerBytes("0.1.3", 2048);
        (byte[] manifest013, byte[] sig013) = BuildSignedManifest(key, version: "0.1.3", installer013);
        string installer013Url = "https://github.com/chialecode/spawn/releases/download/v0.1.3/spawn-setup-0.1.3-x64.exe";
        string partial013 = Path.Combine(sandbox.Paths.UpdatesCacheDirectory, "0.1.3", "spawn-setup-0.1.3-x64.exe.partial");

        int phase = 1;
        StubHttpMessageHandler serving012 = BuildServingHandler(manifest012, sig012, "0.1.2", installer012);
        var handler = new HangingHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            switch (Volatile.Read(ref phase))
            {
                case 1:
                    return null; // Manifest request hangs.
                case 2:
                    return serving012.Respond(req);
                default:
                    if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifest013);
                    if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sig013);
                    if (url == installer013Url)
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StallingStreamContent(installer013.AsSpan(0, 512).ToArray(), onStall: null),
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));
        var idle = new FakeUpdateIdleEvaluator { Snapshot = new UpdateIdleSnapshot { IsMainWindowOpen = true } };
        await using var service = CreateService(sandbox, key.PublicKeyPem, "release", "0.1.1", handler, clock, idle);
        await service.InitializeAsync();

        // Phase 1: caller cancels while Checking -> OperationCanceledException + state restored to Idle.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.CheckForUpdatesAsync(UpdateTriggerKind.Manual, cts.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }

        UpdateStateSnapshot afterPhase1 = service.CurrentSnapshot;
        Assert.Equal(UpdateServiceState.Idle, afterPhase1.State);
        Assert.Null(afterPhase1.DownloadProgressPercent);
        Assert.Null(afterPhase1.ReadyUpdate);
        Assert.Empty(afterPhase1.History);

        // Phase 2: a normal check stages v0.1.2 (ReadyToInstall).
        Volatile.Write(ref phase, 2);
        UpdateCheckResult staged = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual);
        Assert.Equal(UpdateCheckOutcome.UpdateReady, staged.Outcome);
        Assert.Equal(UpdateServiceState.ReadyToInstall, service.CurrentSnapshot.State);

        // Phase 3: v0.1.3 installer stalls while Downloading; caller cancels -> back to ReadyToInstall(v0.1.2).
        Volatile.Write(ref phase, 3);
        var sawDownloading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, snap) =>
        {
            if (snap.State == UpdateServiceState.Downloading)
            {
                sawDownloading.TrySetResult(true);
            }
        };

        using (var cts = new CancellationTokenSource())
        {
            Task<UpdateCheckResult> checkTask = service.CheckForUpdatesAsync(UpdateTriggerKind.Manual, cts.Token);
            await sawDownloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkTask.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await Task.Delay(100);
        UpdateStateSnapshot afterPhase3 = service.CurrentSnapshot;
        Assert.Equal(UpdateServiceState.ReadyToInstall, afterPhase3.State);
        Assert.Null(afterPhase3.DownloadProgressPercent);
        Assert.NotNull(afterPhase3.ReadyUpdate);
        Assert.Equal("0.1.2", afterPhase3.ReadyUpdate!.Version);
        Assert.False(File.Exists(partial013));

        // The operation lock was released: a subsequent check proceeds normally.
        Volatile.Write(ref phase, 2);
        UpdateCheckResult again = await service.CheckForUpdatesAsync(UpdateTriggerKind.Manual)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(UpdateCheckOutcome.UpdateReady, again.Outcome);
    }

    // -------------------------------------------------------------------------
    // Helpers & Fakes (100% in-memory / ephemeral test keys)
    // -------------------------------------------------------------------------

    private static UpdateService CreateService(
        TestSandbox sandbox,
        string publicKeyPem,
        string channel,
        string currentVersion,
        HttpMessageHandler handler,
        TimeProvider timeProvider,
        IUpdateIdleEvaluator? idle = null,
        IUpdateQuitCoordinator? quit = null,
        IUpdateInstallerLauncher? launcher = null,
        TimeSpan? httpRequestTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        var options = new UpdateServiceOptions
        {
            ChannelOverride = channel,
            CurrentVersionOverride = currentVersion,
            CommitShaOverride = "abcdef1234567890",
            PublicKeyPem = publicKeyPem,
        };

        if (httpRequestTimeout is TimeSpan requestTimeout)
        {
            options = options with { HttpRequestTimeout = requestTimeout };
        }

        if (downloadTimeout is TimeSpan installerTimeout)
        {
            options = options with { DownloadTimeout = installerTimeout };
        }

        return new UpdateService(
            sandbox.Paths,
            sandbox.Database,
            idle ?? new FakeUpdateIdleEvaluator(),
            quit ?? new FakeUpdateQuitCoordinator(),
            launcher ?? new FakeUpdateInstallerLauncher(),
            timeProvider,
            options,
            handler);
    }

    private sealed class EphemeralSigningKey : IDisposable
    {
        public ECDsa Key { get; }
        public string PublicKeyPem { get; }

        public EphemeralSigningKey()
        {
            Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            PublicKeyPem = Key.ExportSubjectPublicKeyInfoPem() + "\n";
        }

        public byte[] SignDer(byte[] payload) =>
            Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        public void Dispose() => Key.Dispose();
    }

    private static EphemeralSigningKey CreateTestSigningKey() => new();

    private static byte[] BuildSyntheticInstallerBytes(string version, int length)
    {
        byte[] data = new byte[length];
        byte[] tag = Encoding.UTF8.GetBytes($"SPAWN-SYNTHETIC-SETUP-{version}");
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = tag[i % tag.Length];
        }

        return data;
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static (byte[] ManifestBytes, byte[] SignatureBytes) BuildSignedManifest(
        EphemeralSigningKey signingKey,
        string version,
        byte[] installerBytes,
        int schemaVersion = 1,
        string channel = "release",
        string? overrideName = null,
        string? overrideUrl = null,
        long? overrideSize = null,
        string? overrideSha256 = null)
    {
        string name = overrideName ?? $"spawn-setup-{version}-x64.exe";
        string url = overrideUrl ?? $"https://github.com/chialecode/spawn/releases/download/v{version}/{name}";
        var manifest = new
        {
            schemaVersion,
            channel,
            version,
            installer = new
            {
                name,
                url,
                size = overrideSize ?? installerBytes.LongLength,
                sha256 = overrideSha256 ?? Sha256Hex(installerBytes),
            },
            publishedUtc = "2026-11-22T08:00:00Z",
            notesUrl = $"https://github.com/chialecode/spawn/releases/tag/v{version}",
        };

        string json = JsonSerializer.Serialize(manifest, IndentedJsonOptions) + "\n";
        byte[] manifestBytes = Encoding.UTF8.GetBytes(json);
        byte[] sigBytes = signingKey.SignDer(manifestBytes);
        return (manifestBytes, sigBytes);
    }

    private static StubHttpMessageHandler BuildServingHandler(
        byte[] manifestBytes,
        byte[] sigBytes,
        string version,
        byte[] installerBytes)
    {
        string installerUrl = $"https://github.com/chialecode/spawn/releases/download/v{version}/spawn-setup-{version}-x64.exe";
        return new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url == UpdateServiceOptions.DefaultManifestUrl) return OkBytes(manifestBytes);
            if (url == UpdateServiceOptions.DefaultSignatureUrl) return OkBytes(sigBytes);
            if (url == installerUrl) return OkBytes(installerBytes);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    private static HttpResponseMessage OkBytes(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var resp = new HttpResponseMessage(status);
        resp.Headers.Location = new Uri(location, UriKind.Absolute);
        return resp;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsWriteLocked(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(15);
        }

        Assert.True(predicate(), "Timed out waiting for condition.");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            HttpResponseMessage response = responder(request);
            response.RequestMessage ??= request;
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));
    }

    /// <summary>
    /// Offline handler whose responder may return <c>null</c> to simulate a request that never receives
    /// response headers: it then waits on the request token, exactly like a stalled socket.
    /// </summary>
    private sealed class HangingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage?> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage? response = responder(request);
            if (response is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable: infinite delay completed.");
            }

            response.RequestMessage ??= request;
            return response;
        }
    }

    /// <summary>
    /// Body content of unknown length that yields the prefix bytes, then stalls on the read token.
    /// </summary>
    private sealed class StallingStreamContent(byte[] prefix, Action? onStall) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new StallingReadStream(prefix, onStall));
    }

    private sealed class StallingReadStream(byte[] prefix, Action? onStall) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            onStall?.Invoke();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnboundedStreamContent(byte[] data) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(data, 0, data.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(data, writable: false));
    }

    private sealed class FakeUpdateIdleEvaluator : IUpdateIdleEvaluator
    {
        public UpdateIdleSnapshot Snapshot { get; set; } = new() { IsMainWindowOpen = false };
        public UpdateIdleSnapshot Evaluate() => Snapshot;
    }

    private sealed class FakeUpdateQuitCoordinator : IUpdateQuitCoordinator
    {
        public List<string> QuitReasons { get; } = [];
        public int CompleteShutdownCount { get; private set; }
        public int AbortShutdownCount { get; private set; }
        public Action<string>? OnQuitRequested { get; set; }
        public Func<string, Task>? OnQuitRequestedAsync { get; set; }
        public Action? OnCompleteShutdown { get; set; }
        public Action? OnAbortShutdown { get; set; }

        public async Task RequestQuitForUpdateAsync(string reason, CancellationToken cancellationToken = default)
        {
            QuitReasons.Add(reason);
            OnQuitRequested?.Invoke(reason);
            if (OnQuitRequestedAsync is not null)
            {
                await OnQuitRequestedAsync(reason).ConfigureAwait(false);
            }
        }

        public void CompleteDeferredShutdown()
        {
            CompleteShutdownCount++;
            OnCompleteShutdown?.Invoke();
        }

        public Task AbortDeferredShutdownAsync(CancellationToken cancellationToken = default)
        {
            AbortShutdownCount++;
            OnAbortShutdown?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUpdateInstallerLauncher : IUpdateInstallerLauncher
    {
        public List<(string Path, string Arguments)> Launches { get; } = [];
        public Func<string, string, bool>? OnLaunch { get; set; }
        public string? ErrorMessage { get; set; }

        public bool LaunchSilentInstaller(string installerPath, string arguments, out string? errorMessage)
        {
            Launches.Add((installerPath, arguments));
            bool ok = OnLaunch is null || OnLaunch(installerPath, arguments);
            errorMessage = ok ? null : (ErrorMessage ?? "Launch failed");
            return ok;
        }
    }

    private sealed class ProbeSnapshotService(Action onCreate, Action onRestore) : IPreTrainerSaveSnapshotService
    {
        public Task<PreTrainerSnapshotResult> CreateAsync(string gameId, string? trainerId, CancellationToken ct = default)
        {
            onCreate();
            return Task.FromResult(new PreTrainerSnapshotResult { Succeeded = true, Status = PreTrainerSnapshotStatus.Completed });
        }

        public Task<IReadOnlyList<PreTrainerSnapshotRecord>> ListAsync(string gameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreTrainerSnapshotRecord>>([]);

        public Task<PreTrainerRestoreResult> RestoreAsync(string snapshotId, CancellationToken ct = default)
        {
            onRestore();
            return Task.FromResult(new PreTrainerRestoreResult { Succeeded = true });
        }
    }

    private sealed class ProbeGameDataService(Action onClean, Action onUndo) : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnlineRules = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default)
        {
            onClean();
            return Task.FromResult(new GameDataCleanResult { Succeeded = true, Message = "ok" });
        }

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default)
        {
            onUndo();
            return Task.FromResult(new GameDataCleanResult { Succeeded = true, Message = "ok" });
        }
    }
}
