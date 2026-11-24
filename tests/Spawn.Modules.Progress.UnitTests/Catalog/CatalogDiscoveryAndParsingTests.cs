using System.Net;
using System.Text;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Catalog;
using Xunit;

namespace Spawn.Modules.Progress.UnitTests.Catalog;

/// <summary>
/// M3-A-06 automated verification: clean-room FLiNG discovery (<c>robots.txt</c> compliance, concurrency = 1,
/// 24h <c>/all-trainers/</c> cache, optional 7-day archive cache, zero <c>/?s=</c> or <c>/attachments/</c>,
/// honest User-Agent), AngleSharp structured hotkey &amp; <c>toolTips</c> extraction (&gt;=95% accuracy),
/// promo-link filtering, <see cref="TrainerParseMode.FreeHosted"/> graceful degradation,
/// and 5-category FLiNG version string → 3-tier confidence mapping.
/// </summary>
public sealed class CatalogDiscoveryAndParsingTests
{
    [Fact]
    public async Task Discovery_RobotsComplianceConcurrencyOne24hCacheAndOptIn7DayArchive_EnforcedStrictly()
    {
        await using var sandbox = new ProgressTestSandbox();
        await sandbox.Database.InitializeAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));

        string allTrainersHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-all-trainers.html"));
        string wpJson = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-wp-json-search.json"));
        string archiveHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-archive-index.html"));

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
            SteamLastUpdatedUtc = new DateTimeOffset(2025, 4, 10, 0, 0, 0, TimeSpan.Zero),
        });

        using var handler = new RecordingMockHttpHandler(async (req, ct) =>
        {
            // Introduce short async latency so concurrent calls would overlap if SemaphoreSlim(1,1) were missing.
            await Task.Delay(25, ct);
            string url = req.RequestUri!.AbsoluteUri;

            if (url.Contains("store.steampowered.com/api/appdetails", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"1245620":{"success":true,"data":{"name":"ELDEN RING"}}}""", Encoding.UTF8, "application/json"),
                };
            }

            if (url.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(allTrainersHtml, Encoding.UTF8, "text/html"),
                };
            }

            if (url.StartsWith(FlingUrlPolicy.WpJsonPostsEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(wpJson, Encoding.UTF8, "application/json"),
                };
            }

            if (url.Equals(FlingUrlPolicy.ArchiveIndexUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(archiveHtml, Encoding.UTF8, "text/html"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        var titleResolver = new CanonicalTitleResolver(sandbox.Database, httpClient, clock);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient, clock);
        var downloader = new TrainerSafeDownloader(httpClient);
        var extractor = new TrainerArchiveExtractor();
        var catalog = new TrainerCatalogService(sandbox.Database, sandbox.Paths, titleResolver, discovery, downloader, extractor, timeProvider: clock);

        // 1. Fire 4 concurrent discovery searches to prove SemaphoreSlim(1,1) enforces strict concurrency = 1.
        Task<TrainerMatchResult>[] parallelTasks =
        [
            catalog.MatchAsync(gameId),
            catalog.MatchAsync(gameId),
            catalog.MatchAsync(gameId),
            catalog.MatchAsync(gameId),
        ];
        TrainerMatchResult[] results = await Task.WhenAll(parallelTasks);

        Assert.All(results, r =>
        {
            Assert.True(r.Succeeded);
            Assert.Equal("ELDEN RING", r.EffectiveQuery);
            Assert.NotEmpty(r.Candidates);
            Assert.Contains(r.Candidates, c => c.PageUrl.Equals("https://flingtrainer.com/trainer/elden-ring-trainer/", StringComparison.OrdinalIgnoreCase));
            // Promo, HTTP, and untrusted-domain links must be strictly filtered out.
            Assert.DoesNotContain(r.Candidates, c => FlingUrlPolicy.ContainsPromotionalToken(c.PageUrl) || FlingUrlPolicy.ContainsPromotionalToken(c.Title));
            Assert.DoesNotContain(r.Candidates, c => c.PageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(r.Candidates, c => c.PageUrl.Contains("example.", StringComparison.OrdinalIgnoreCase));
        });

        // Verify FLiNG concurrency never exceeded 1 across all concurrent FLiNG calls.
        var flingCalls = handler.Calls.Where(c => c.Uri.Host.EndsWith("flingtrainer.com", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(flingCalls);
        Assert.Equal(1, flingCalls.Max(c => c.FlingConcurrencyAtEntry));

        // Zero robots.txt violations, zero batch /downloads/ crawl, honest non-browser User-Agent.
        foreach (RecordedHttpCall call in flingCalls)
        {
            Assert.DoesNotContain("/?s=", call.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("?s=", call.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/attachments/", call.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/downloads/", call.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(call.UserAgent);
            Assert.Contains("SPAWN-TrainerCatalog", call.UserAgent!, StringComparison.Ordinal);
            Assert.DoesNotContain("Mozilla/", call.UserAgent!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Chrome/", call.UserAgent!, StringComparison.OrdinalIgnoreCase);
        }

        // Archive site MUST NOT be queried when IncludeFlingArchiveInSearch is false (default).
        Assert.DoesNotContain(flingCalls, c => c.Uri.Host.Equals(FlingUrlPolicy.ArchiveHost, StringComparison.OrdinalIgnoreCase));

        // 2. Verify 24h /all-trainers/ cache reuse: run another search 10 hours later -> no new /all-trainers/ fetch.
        int allTrainersCountBefore = handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, allTrainersCountBefore);

        clock.Advance(TimeSpan.FromHours(10));
        await catalog.MatchAsync(gameId);
        Assert.Equal(1, handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase)));

        // Advance past 24h TTL -> /all-trainers/ refreshes once.
        clock.Advance(TimeSpan.FromHours(15));
        await catalog.MatchAsync(gameId);
        Assert.Equal(2, handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase)));

        // 3. Enable IncludeFlingArchiveInSearch -> fetches archive.flingtrainer.com and caches for 7 days.
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto { IncludeFlingArchiveInSearch = true });
        TrainerMatchResult archiveMatch1 = await catalog.MatchAsync(gameId, userSearchOverride: "Dark Souls III");
        Assert.Contains(archiveMatch1.Candidates, c =>
            c.Provider == TrainerProviderKind.FlingArchive
            && c.PageUrl.Equals("https://archive.flingtrainer.com/dark-souls-iii-trainer/", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.ArchiveIndexUrl, StringComparison.OrdinalIgnoreCase)));

        // 5 days later: still served from 7-day archive cache (0 extra archive requests).
        clock.Advance(TimeSpan.FromDays(5));
        await catalog.MatchAsync(gameId, userSearchOverride: "Dark Souls III");
        Assert.Equal(1, handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.ArchiveIndexUrl, StringComparison.OrdinalIgnoreCase)));

        // 3 more days (total 8 days > 7d TTL): archive refreshes once.
        clock.Advance(TimeSpan.FromDays(3));
        await catalog.MatchAsync(gameId, userSearchOverride: "Dark Souls III");
        Assert.Equal(2, handler.Calls.Count(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.ArchiveIndexUrl, StringComparison.OrdinalIgnoreCase)));

        // Chinese display title in games table must remain untouched.
        Assert.Equal("艾尔登法环", (await sandbox.Database.GetGameByIdAsync(gameId))!.Title);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false, "Access Denied")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, "Service Unavailable")]
    [InlineData(HttpStatusCode.OK, true, "<html><head><title>Just a moment...</title></head><body>Just a moment...</body></html>")]
    public async Task Discovery_ChallengeOr403Or503_ImmediatelyAbortsWithoutRetryAndRecommendsBrowserFallback(
        HttpStatusCode status,
        bool addCfMitigatedHeader,
        string body)
    {
        await using var sandbox = new ProgressTestSandbox();
        using var handler = new RecordingMockHttpHandler(_ =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            };
            if (addCfMitigatedHeader)
            {
                resp.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
            }

            return resp;
        });

        using var httpClient = new HttpClient(handler);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient);

        TrainerMatchResult match = await discovery.SearchAsync("g1", "Elden Ring", TrainerTitleSource.AcfName, includeArchive: false);
        Assert.False(match.Succeeded);
        Assert.True(match.FallbackToBrowserRecommended);
        Assert.False(string.IsNullOrWhiteSpace(match.FallbackBrowserUrl));
        // Aborted immediately on the very first challenge without retry loop.
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task HtmlParser_StaticDetailFixture_ExtractsAllHotkeysTooltipsAndSortedWhitelistedAttachments()
    {
        string html = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-detail-elden-ring.html"));
        var pageUri = new Uri("https://flingtrainer.com/trainer/elden-ring-trainer/", UriKind.Absolute);

        var localFp = new GameVersionFingerprint(
            SteamBuildId: "17991234",
            SteamLastUpdatedUtc: new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero),
            ExeSize: 70_000_000,
            ExeLastWriteUtc: new DateTimeOffset(2025, 4, 15, 0, 0, 0, TimeSpan.Zero),
            FilteredPeVersion: "1.16.1");

        FlingDetailPageParseResult parsed = FlingHtmlParser.ParseDetailPage(html, pageUri, "trn_001", localFp);

        Assert.Equal(TrainerParseMode.Structured, parsed.ParseMode);

        // 1. Hotkey extraction accuracy: fixture contains 34 structured options -> assert >= 95% (expect 34/34 = 100%).
        const int expectedHotkeyCount = 34;
        double accuracy = (double)parsed.Hotkeys.Count / expectedHotkeyCount;
        Assert.True(accuracy >= 0.95, $"Expected >=95% hotkey extraction accuracy, got {parsed.Hotkeys.Count}/{expectedHotkeyCount} ({accuracy:P1}).");
        Assert.Equal(expectedHotkeyCount, parsed.Hotkeys.Count);

        // Verify <script> was stripped and never leaked into hotkey labels.
        Assert.DoesNotContain(parsed.Hotkeys, h => h.Label.Contains("promoBanner", StringComparison.OrdinalIgnoreCase));

        // Verify specific critical chords & tooltips (en-dash &#8211;, Num /, Num -, Num +, Num *, Num ., Num Enter, </p> final item, toolTips).
        var godMode = Assert.Single(parsed.Hotkeys, h => h.OriginalChord == "Num 1");
        Assert.Equal("God Mode/Ignore Hits", godMode.Label);
        Assert.Contains("Prevents all damage from enemies & traps.", godMode.Tooltip ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Disable during scripted story events.", godMode.Tooltip ?? string.Empty, StringComparison.Ordinal);

        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num 0" && h.Label.Contains("Super Damage", StringComparison.Ordinal));
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num ." && h.Label == "Damage Multiplier");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num +" && h.Label == "Defense Multiplier");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num -" && h.Label == "Super Speed");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num *" && h.Label == "Slow Motion");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Num /" && h.Label == "Save Location" && h.EntryKind == TrainerHotkeyEntryKind.Trigger);

        var numEnter = Assert.Single(parsed.Hotkeys, h => h.OriginalChord == "Num Enter");
        Assert.Equal("Teleport to Saved Location", numEnter.Label);
        Assert.Contains("Save a position with Num /", numEnter.Tooltip ?? string.Empty, StringComparison.Ordinal);

        var ctrlNum1 = Assert.Single(parsed.Hotkeys, h => h.OriginalChord == "Ctrl+Num 1");
        Assert.Equal("Edit Runes", ctrlNum1.Label);
        Assert.Equal(TrainerHotkeyEntryKind.Trigger, ctrlNum1.EntryKind);
        Assert.Contains("spending or picking up Runes", ctrlNum1.Tooltip ?? string.Empty, StringComparison.Ordinal);

        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Alt+Num 1");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Shift+F1");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Ctrl+Alt+Num /");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Ctrl+Shift+Delete");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "F1");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "F12");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Home");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "End");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Insert");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Delete");
        Assert.Contains(parsed.Hotkeys, h => h.OriginalChord == "Page Up");

        // Final item terminated directly by </p> (no trailing <br>) MUST be extracted with its tooltip!
        var pageDown = Assert.Single(parsed.Hotkeys, h => h.OriginalChord == "Page Down");
        Assert.Equal("Decrease Game Speed", pageDown.Label);
        Assert.Contains("offline single-player mode", pageDown.Tooltip ?? string.Empty, StringComparison.Ordinal);

        // 2. Attachment table verification: all 5 legitimate attachments kept, latest pinned FIRST, all 4 promos/untrusted filtered!
        Assert.Equal(5, parsed.Attachments.Count);
        Assert.Equal("att-latest-5", parsed.Attachments[0].AttachmentId);
        Assert.Equal("2025.04.16", parsed.Attachments[0].UpdatedDateText);
        Assert.Equal("att-old-1", parsed.Attachments[^1].AttachmentId);

        Assert.All(parsed.Attachments, att =>
        {
            Assert.StartsWith("https://flingtrainer.com/", att.DownloadUrl, StringComparison.OrdinalIgnoreCase);
            Assert.False(FlingUrlPolicy.ContainsPromotionalToken(att.DownloadUrl));
            Assert.False(FlingUrlPolicy.ContainsPromotionalToken(att.FileName));
        });
    }

    [Fact]
    public async Task HtmlParser_NonStructuredDetailPage_GracefullyDegradesToFreeHosted()
    {
        string html = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-detail-non-structured.html"));
        var pageUri = new Uri("https://flingtrainer.com/trainer/custom-indie-game-trainer/", UriKind.Absolute);

        FlingDetailPageParseResult parsed = FlingHtmlParser.ParseDetailPage(html, pageUri);

        Assert.Equal(TrainerParseMode.FreeHosted, parsed.ParseMode);
        Assert.Empty(parsed.Hotkeys);
        Assert.Single(parsed.Attachments);
        Assert.Equal("att-indie-1", parsed.Attachments[0].AttachmentId);
    }

    [Fact]
    public void VersionFingerprint_FiveFlingVersionPatternsAndEngineNoiseFilter_MapsToThreeConfidenceTiersAndDetectsDrift()
    {
        // 1. Engine PE version noise filtering
        Assert.True(GameVersionFingerprintService.IsEngineVersionNoise("2021.3.35f1"));
        Assert.True(GameVersionFingerprintService.IsEngineVersionNoise("2022.3.18.1234"));
        Assert.True(GameVersionFingerprintService.IsEngineVersionNoise("4.27.2.0"));
        Assert.True(GameVersionFingerprintService.IsEngineVersionNoise("5.3.2.0"));
        Assert.False(GameVersionFingerprintService.IsEngineVersionNoise("1.16.1"));

        // 2. Classify all 5 FLiNG version string patterns
        Assert.Equal(FlingVersionStringKind.VersionRange, GameVersionFingerprintService.ParseFlingVersionString("v1.02-v1.16.1").Kind);
        Assert.Equal(FlingVersionStringKind.OpenEndedPlus, GameVersionFingerprintService.ParseFlingVersionString("v1.0+").Kind);
        Assert.Equal(FlingVersionStringKind.OpenEndedPlus, GameVersionFingerprintService.ParseFlingVersionString("Early Access+").Kind);
        Assert.Equal(FlingVersionStringKind.DateStampedRange, GameVersionFingerprintService.ParseFlingVersionString("v1.0-v20251216").Kind);
        Assert.Equal(FlingVersionStringKind.VersionWithUpdateDate, GameVersionFingerprintService.ParseFlingVersionString("v4.1.1 Updated 2025.04.16").Kind);
        Assert.Equal(FlingVersionStringKind.RangeWithTrailingUpdated, GameVersionFingerprintService.ParseFlingVersionString("v2.0-v2.5 Updated").Kind);

        var fpV116 = new GameVersionFingerprint(
            SteamBuildId: "17991234",
            SteamLastUpdatedUtc: new DateTimeOffset(2025, 4, 10, 0, 0, 0, TimeSpan.Zero),
            ExeSize: 64_000_000,
            ExeLastWriteUtc: new DateTimeOffset(2025, 4, 10, 0, 0, 0, TimeSpan.Zero),
            FilteredPeVersion: "1.16.1");

        // Pattern 1: bounded range matched vs outdated
        Assert.Equal(TrainerVersionConfidence.RangeMatched,
            GameVersionFingerprintService.EvaluateConfidence("v1.02-v1.16.1", "2025.04.16", fpV116).Confidence);
        Assert.Equal(TrainerVersionConfidence.PossiblyOutdated,
            GameVersionFingerprintService.EvaluateConfidence("v1.02-v1.10", "2023.08.01", fpV116).Confidence);

        // Pattern 2: open-ended '+' / Early Access+ -> CheckByDate (or PossiblyOutdated when game is much newer)
        Assert.Equal(TrainerVersionConfidence.CheckByDate,
            GameVersionFingerprintService.EvaluateConfidence("v1.0+", "2025.04.12", fpV116).Confidence);
        Assert.Equal(TrainerVersionConfidence.CheckByDate,
            GameVersionFingerprintService.EvaluateConfidence("Early Access+", null, fpV116).Confidence);
        Assert.Equal(TrainerVersionConfidence.PossiblyOutdated,
            GameVersionFingerprintService.EvaluateConfidence("v1.0+", "2024.01.01", fpV116).Confidence);

        // Pattern 3: date-stamped v1.0-v20251216 -> CheckByDate when current, PossiblyOutdated when older
        Assert.Equal(TrainerVersionConfidence.CheckByDate,
            GameVersionFingerprintService.EvaluateConfidence("v1.0-v20251216", null, fpV116).Confidence);
        Assert.Equal(TrainerVersionConfidence.PossiblyOutdated,
            GameVersionFingerprintService.EvaluateConfidence("v1.0-v20240101", null, fpV116).Confidence);

        // Pattern 4: v4.1.1 Updated 2025.04.16
        var fpV411 = fpV116 with { FilteredPeVersion = "4.1.1" };
        Assert.Equal(TrainerVersionConfidence.RangeMatched,
            GameVersionFingerprintService.EvaluateConfidence("v4.1.1 Updated 2025.04.16", null, fpV411).Confidence);
        Assert.Equal(TrainerVersionConfidence.PossiblyOutdated,
            GameVersionFingerprintService.EvaluateConfidence("v4.1.1 Updated 2024.04.16", null, fpV411).Confidence);

        // Pattern 5: v2.0-v2.5 Updated
        var fpV23 = fpV116 with { FilteredPeVersion = "2.3.0" };
        Assert.Equal(TrainerVersionConfidence.RangeMatched,
            GameVersionFingerprintService.EvaluateConfidence("v2.0-v2.5 Updated", "2025.04.15", fpV23).Confidence);
        Assert.Equal(TrainerVersionConfidence.CheckByDate,
            GameVersionFingerprintService.EvaluateConfidence("v2.0-v2.5 Updated", null, fpV116 with { FilteredPeVersion = "2022.3.20.0" }).Confidence);

        // 3. Binding drift detection (BuildId / ExeSize / ExeLastWriteUtc)
        var boundTrainer = new TrainerCatalogRecord
        {
            TrainerId = "t1",
            GameId = "g1",
            SandboxRelativePath = "g1\\t1\\trainer.exe",
            FileSha256 = new string('a', 64),
            BoundBuildId = "17991234",
            BoundExeSize = 64_000_000,
            BoundExeLastWriteUtc = new DateTimeOffset(2025, 4, 10, 0, 0, 0, TimeSpan.Zero),
        };

        Assert.False(GameVersionFingerprintService.DetectGameUpdateSinceBinding(boundTrainer, fpV116).Changed);

        var driftedBuild = GameVersionFingerprintService.DetectGameUpdateSinceBinding(boundTrainer, fpV116 with { SteamBuildId = "18000001" });
        Assert.True(driftedBuild.Changed);
        Assert.Equal(GameVersionFingerprintService.GameUpdatedGentleNotice, driftedBuild.Notice);

        var driftedSize = GameVersionFingerprintService.DetectGameUpdateSinceBinding(boundTrainer, fpV116 with { ExeSize = 65_123_456 });
        Assert.True(driftedSize.Changed);
    }

    [Fact]
    public void SourceCodeCompliance_CatalogSourceDirectory_ZeroDisallowedRobotsEndpointsOrBrowserSpoofing()
    {
        string catalogSourceDir = Path.Combine(
            ProgressTestSandbox.LocateRepositoryRoot(),
            "src",
            "Spawn.Modules.Progress",
            "Catalog");

        string[] csFiles = Directory.GetFiles(catalogSourceDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        foreach (string file in csFiles)
        {
            string source = File.ReadAllText(file, Encoding.UTF8);
            Assert.DoesNotContain("/?s=", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/attachments/", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mozilla/5.0", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Chrome/", source, StringComparison.OrdinalIgnoreCase);
        }

        // Runtime URL policy rejects disallowed robots.txt endpoints even if handed directly.
        Assert.False(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://flingtrainer.com/?s=elden+ring"), out _));
        Assert.False(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://flingtrainer.com/attachments/12345/"), out _));
        Assert.False(FlingUrlPolicy.IsAllowedFlingUri(new Uri("http://flingtrainer.com/all-trainers/"), out _));
        Assert.False(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://evil.example/trainer/foo/"), out _));
        Assert.False(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://flingtrainer.com/downloads/wemod-setup.exe"), out _));
        Assert.True(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://flingtrainer.com/all-trainers/"), out _));
        Assert.True(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://archive.flingtrainer.com/limbo-trainer/"), out _));
    }

    [Fact]
    public async Task Discovery_PassiveCloudflareJsDetectionBeaconOn200Html_DoesNotFalsePositiveWhileGenuineChallengesStillAbort()
    {
        await using var sandbox = new ProgressTestSandbox();
        string allTrainersHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-all-trainers.html"));
        string detailHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-detail-elden-ring.html"));
        string wpJson = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-wp-json-search.json"));

        // Inject Cloudflare's real passive JS Detection telemetry beacon before </body> on HTTP 200 HTML pages.
        const string passiveJsdBeacon = """
            <script>(function(){var u='/cdn-cgi/challenge-platform/scripts/jsd/main.js?ray=a5e18ef9a8c56dc3',a=document.createElement('script');a.src=u;document.getElementsByTagName('head')[0].appendChild(a);})();</script>
            """;
        string allTrainersWithBeacon = allTrainersHtml.Replace("</body>", passiveJsdBeacon + "</body>", StringComparison.OrdinalIgnoreCase);
        string detailWithBeacon = detailHtml.Replace("</body>", passiveJsdBeacon + "</body>", StringComparison.OrdinalIgnoreCase);

        Assert.False(FlingUrlPolicy.ContainsChallengeMarkerText(allTrainersWithBeacon));
        Assert.False(FlingUrlPolicy.ContainsChallengeMarkerText(detailWithBeacon));

        using var handler = new RecordingMockHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(allTrainersWithBeacon, Encoding.UTF8, "text/html"),
                };
            }

            if (url.StartsWith(FlingUrlPolicy.WpJsonPostsEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(wpJson, Encoding.UTF8, "application/json"),
                };
            }

            if (url.Equals("https://flingtrainer.com/trainer/elden-ring-trainer/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(detailWithBeacon, Encoding.UTF8, "text/html"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var httpClient = new HttpClient(handler);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient);

        TrainerMatchResult match = await discovery.SearchAsync("g1", "Elden Ring", TrainerTitleSource.AcfName, includeArchive: false);
        Assert.True(match.Succeeded, match.Message);
        Assert.False(match.FallbackToBrowserRecommended);
        TrainerCandidate erCand = Assert.Single(match.Candidates, c => c.PageUrl.Equals("https://flingtrainer.com/trainer/elden-ring-trainer/", StringComparison.OrdinalIgnoreCase));

        var (loaded, challengeDetected, errMsg) = await discovery.LoadCandidateDetailsAsync(erCand);
        Assert.False(challengeDetected, errMsg);
        Assert.Null(errMsg);
        Assert.Equal(5, loaded.Attachments.Count);
        Assert.Equal(34, loaded.PreviewHotkeys.Count);

        // Verify honest standard RFC headers (Accept + Accept-Language + honest non-browser User-Agent).
        var htmlCall = handler.Calls.First(c => c.Uri.AbsoluteUri.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("text/html", htmlCall.Accept ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("en-US", htmlCall.AcceptLanguage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var jsonCall = handler.Calls.First(c => c.Uri.AbsoluteUri.StartsWith(FlingUrlPolicy.WpJsonPostsEndpoint, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("application/json", jsonCall.Accept ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Genuine Cloudflare challenge/WAF interstitial markers MUST STILL be detected 100% of the time.
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<!DOCTYPE html><html><head><title>Just a moment...</title></head><body></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<!DOCTYPE html><html><head><title>Attention Required! | Cloudflare</title></head><body></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<html><body><script>window._cf_chl_opt={cvId:'3'};</script></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<html><body><script src='/cdn-cgi/challenge-platform/h/b/orchestrate/chl_page/v1'></script></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<html><body><form id=\"challenge-form\" action=\"/\"></form></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<html><body><div class=\"cf-browser-verification\">Enable JavaScript and cookies to continue</div></body></html>"));
        Assert.True(FlingUrlPolicy.ContainsChallengeMarkerText("<html><body><script src=\"https://challenges.cloudflare.com/turnstile/v0/api.js\"></script></body></html>"));
    }

    [Fact]
    public async Task Discovery_ArchiveDirectFileLinks_SynthesizeAttachmentWithoutFetchingBinaryAsHtml()
    {
        await using var sandbox = new ProgressTestSandbox();
        const string realArchiveIndexHtml = """
            <!DOCTYPE HTML>
            <html>
            <head><title>FLiNG Trainers Archive</title></head>
            <body>
              <table class="files">
                <tr><td>File Name</td><td>Size</td></tr>
                <tr>
                  <td><a href="files/Ace Combat 7 Skies Unknown v1.0-v1.13.1 Plus 11 Trainer.rar" target="_self">Ace Combat 7 Skies Unknown v1.0-v1.13.1 Plus 11 Trainer</a></td>
                  <td>636.73 KB</td>
                </tr>
              </table>
              <script>var u='/cdn-cgi/challenge-platform/scripts/jsd/main.js?ray=abc';</script>
            </body>
            </html>
            """;

        using var handler = new RecordingMockHttpHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.Equals(FlingUrlPolicy.AllTrainersIndexUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><head><title>All Trainers</title></head><body></body></html>", Encoding.UTF8, "text/html"),
                };
            }

            if (url.StartsWith(FlingUrlPolicy.WpJsonPostsEndpoint, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json"),
                };
            }

            if (url.Equals(FlingUrlPolicy.ArchiveIndexUrl, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(realArchiveIndexHtml, Encoding.UTF8, "text/html"),
                };
            }

            throw new InvalidOperationException($"Direct archive binary URL must NEVER be fetched as HTML detail text: {url}");
        });

        using var httpClient = new HttpClient(handler);
        using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient);

        TrainerMatchResult match = await discovery.SearchAsync("g-ac7", "Ace Combat 7 Skies Unknown", TrainerTitleSource.AcfName, includeArchive: true);
        Assert.True(match.Succeeded, match.Message);
        Assert.False(match.FallbackToBrowserRecommended);

        TrainerCandidate archiveCandidate = Assert.Single(match.Candidates, c => c.Provider == TrainerProviderKind.FlingArchive);
        Assert.Single(archiveCandidate.Attachments);
        Assert.NotNull(archiveCandidate.SelectedAttachment);
        Assert.EndsWith(".rar", archiveCandidate.SelectedAttachment!.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((long)Math.Round(636.73 * 1024), archiveCandidate.SelectedAttachment.ApproximateSizeBytes);

        int callsBeforeDetailLoad = handler.Calls.Count;
        var (loaded, challenge, err) = await discovery.LoadCandidateDetailsAsync(archiveCandidate);
        Assert.False(challenge);
        Assert.Null(err);
        Assert.Single(loaded.Attachments);
        Assert.NotNull(loaded.SelectedAttachment);
        // Zero extra HTTP requests: direct archive link never downloaded as HTML text.
        Assert.Equal(callsBeforeDetailLoad, handler.Calls.Count);
    }

    [Fact]
    public void UrlPolicy_PromotionalTokenFilter_AllowsLegitimateWordSubstringsWhileBlockingStandalonePromos()
    {
        // Legitimate game title containing "wand" as part of "wandering" must NOT be falsely rejected.
        Assert.False(FlingUrlPolicy.ContainsPromotionalToken("Wandering Sword Trainer"));
        Assert.False(FlingUrlPolicy.ContainsPromotionalToken("https://flingtrainer.com/trainer/wandering-sword-trainer/"));
        Assert.True(FlingUrlPolicy.IsAllowedFlingUri(new Uri("https://flingtrainer.com/trainer/wandering-sword-trainer/"), out _));

        // Standalone promotional brand tokens and installers MUST still be strictly blocked.
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("https://wand.com/download"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("https://flingtrainer.com/downloads/wand-app.exe"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("https://flingtrainer.com/downloads/get_wand.zip"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("Download with Wand"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("WeMod Auto-Updating Trainer"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("https://flingtrainer.com/downloads/game-setup.exe"));
        Assert.True(FlingUrlPolicy.ContainsPromotionalToken("https://flingtrainer.com/downloads/trainer-installer.exe"));
    }

    [Fact]
    public async Task Discovery_RedirectedDetailPage_UpdatesCanonicalPageUrlAndBlocksOffWhitelistRedirect()
    {
        await using var sandbox = new ProgressTestSandbox();
        string detailHtml = await File.ReadAllTextAsync(ProgressTestSandbox.GetCatalogFixturePath("fling-detail-elden-ring.html"));
        const string canonicalUrl = "https://flingtrainer.com/trainer/elden-ring-shadow-of-the-erdtree-trainer-1768067282/";

        // 1. Canonical same-origin redirect updates Candidate.PageUrl so downstream downloads send canonical Referer.
        using (var redirectHandler = new RecordingMockHttpHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(detailHtml, Encoding.UTF8, "text/html"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, canonicalUrl),
            };
            return resp;
        }))
        {
            using var httpClient = new HttpClient(redirectHandler);
            using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient);
            var candidate = new TrainerCandidate
            {
                CandidateId = "cand_er_old",
                Provider = TrainerProviderKind.FlingMain,
                Title = "Elden Ring Trainer",
                PageUrl = "https://flingtrainer.com/trainer/elden-ring-trainer/",
            };

            var (loaded, challenge, err) = await discovery.LoadCandidateDetailsAsync(candidate);
            Assert.False(challenge);
            Assert.Null(err);
            Assert.Equal(canonicalUrl, loaded.PageUrl);
            Assert.Equal(5, loaded.Attachments.Count);
        }

        // 2. Auto-redirect escaping the FLiNG HTTPS whitelist is strictly rejected.
        using (var evilRedirectHandler = new RecordingMockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailHtml, Encoding.UTF8, "text/html"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://untrusted-mirror.example/trainer/elden-ring/"),
        }))
        {
            using var httpClient = new HttpClient(evilRedirectHandler);
            using var discovery = new FlingCleanRoomDiscoveryClient(sandbox.Paths, httpClient);
            var candidate = new TrainerCandidate
            {
                CandidateId = "cand_er_evil",
                Provider = TrainerProviderKind.FlingMain,
                Title = "Elden Ring Trainer",
                PageUrl = "https://flingtrainer.com/trainer/elden-ring-trainer/",
            };

            var (loaded, challenge, err) = await discovery.LoadCandidateDetailsAsync(candidate);
            Assert.False(challenge);
            Assert.False(string.IsNullOrWhiteSpace(err));
            Assert.Empty(loaded.Attachments);
        }
    }
}
