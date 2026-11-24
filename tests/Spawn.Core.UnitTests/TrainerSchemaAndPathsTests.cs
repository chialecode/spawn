using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Spawn.Contracts;
using Spawn.Core;
using Xunit;

namespace Spawn.Core.UnitTests;

/// <summary>
/// M3-A-02 automated verification: SQLite Schema v3 → v4 idempotent migration, four trainer tables CRUD,
/// <c>verified_offline_bypass</c> non-mutation of <c>anti_cheat_tier</c>, <see cref="TrainerSettingsDto"/>
/// and cache persistence, plus <see cref="SpawnPaths"/> trainer/snapshot/pipe isolation.
/// </summary>
public sealed class TrainerSchemaAndPathsTests
{
    private sealed record CachedSteamCategoriesSample(int AppId, string EnglishName, int[] CategoryIds);

    [Fact]
    public async Task Database_MigrationV3ToV4_IsIdempotentAndPreservesExistingData()
    {
        await using var sandbox = new TestSandbox();
        string dbPath = sandbox.Paths.DatabaseFilePath;
        Directory.CreateDirectory(sandbox.Paths.DbDirectory);

        string existingGameId = SpawnId.NewUlid();
        string existingSessionId = SpawnId.NewUlid();
        string fakeInstallDir = sandbox.CreateSubDirectory("games", "LegacyV3Game");

        // 1. Seed a genuine v3 database (no v4 columns on games, no trainer tables).
        await using (var rawConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await rawConn.OpenAsync();
            await using var cmd = rawConn.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE schema_migrations (
                    version         INTEGER PRIMARY KEY,
                    applied_at_utc  TEXT NOT NULL
                );

                CREATE TABLE games (
                    game_id                     TEXT PRIMARY KEY,
                    title                       TEXT NOT NULL,
                    sort_title                  TEXT NOT NULL,
                    install_dir                 TEXT NOT NULL,
                    executable_rel_path         TEXT NOT NULL,
                    platform_source             TEXT NOT NULL DEFAULT 'Standalone',
                    anti_cheat_tier             INTEGER NOT NULL DEFAULT 0,
                    anti_cheat_signature        TEXT,
                    steam_app_id                INTEGER,
                    gog_game_id                 TEXT,
                    gog_client_id               TEXT,
                    achievement_capture_mode    TEXT NOT NULL DEFAULT 'None',
                    achievement_channel_label   TEXT,
                    cover_image_path            TEXT,
                    icon_path                   TEXT,
                    hero_image_path             TEXT,
                    launch_arguments            TEXT,
                    total_active_sec            INTEGER NOT NULL DEFAULT 0,
                    total_background_sec        INTEGER NOT NULL DEFAULT 0,
                    total_afk_sec               INTEGER NOT NULL DEFAULT 0,
                    last_played_utc             TEXT,
                    created_utc                 TEXT NOT NULL
                );

                CREATE TABLE play_sessions (
                    session_id      TEXT PRIMARY KEY,
                    game_id         TEXT NOT NULL REFERENCES games(game_id) ON DELETE CASCADE,
                    started_utc     TEXT NOT NULL,
                    ended_utc       TEXT,
                    active_sec      INTEGER NOT NULL DEFAULT 0,
                    background_sec  INTEGER NOT NULL DEFAULT 0,
                    afk_sec         INTEGER NOT NULL DEFAULT 0,
                    exit_code       INTEGER
                );

                CREATE TABLE app_settings (
                    setting_key     TEXT PRIMARY KEY COLLATE NOCASE,
                    setting_value   TEXT NOT NULL,
                    updated_at_utc  TEXT NOT NULL
                );

                INSERT INTO schema_migrations (version, applied_at_utc) VALUES
                    (1, '2026-11-18T00:00:00.000Z'),
                    (2, '2026-11-19T00:00:00.000Z'),
                    (3, '2026-11-20T00:00:00.000Z');

                INSERT INTO games (
                    game_id, title, sort_title, install_dir, executable_rel_path,
                    platform_source, anti_cheat_tier, steam_app_id,
                    total_active_sec, created_utc
                ) VALUES (
                    $game_id, 'Legacy V3 Title', 'Legacy V3 Title', $install_dir, 'game.exe',
                    'SteamLocal', 0, 1245620,
                    4200, '2026-11-20T01:00:00.000Z'
                );

                INSERT INTO play_sessions (
                    session_id, game_id, started_utc, ended_utc, active_sec, exit_code
                ) VALUES (
                    $session_id, $game_id, '2026-11-20T01:00:00.000Z', '2026-11-20T02:10:00.000Z', 4200, 0
                );

                INSERT INTO app_settings (setting_key, setting_value, updated_at_utc)
                VALUES ('overlay.sound_volume', '0.85', '2026-11-20T01:00:00.000Z');
                """;
            cmd.Parameters.AddWithValue("$game_id", existingGameId);
            cmd.Parameters.AddWithValue("$session_id", existingSessionId);
            cmd.Parameters.AddWithValue("$install_dir", Path.TrimEndingDirectorySeparator(Path.GetFullPath(fakeInstallDir)));
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Run first v3 -> v4 upgrade via SpawnDatabase.
        using (var db = new SpawnDatabase(sandbox.Paths))
        {
            await db.InitializeAsync();
            Assert.Equal(4, await db.GetSchemaVersionAsync());

            var preservedGame = await db.GetGameByIdAsync(existingGameId);
            Assert.NotNull(preservedGame);
            Assert.Equal("Legacy V3 Title", preservedGame!.Title);
            Assert.Equal(4200, preservedGame.TotalActiveSec);
            Assert.False(preservedGame.VerifiedOfflineBypass);
            Assert.Null(preservedGame.SteamBuildId);

            var preservedSessions = await db.GetPlaySessionsByGameAsync(existingGameId);
            Assert.Single(preservedSessions);
            Assert.Equal(existingSessionId, preservedSessions[0].SessionId);

            Assert.Equal("0.85", await db.GetSettingAsync(AppSettingKeys.OverlaySoundVolume));
        }

        // 3. Verify all 4 new columns and 4 new tables exist in SQLite metadata.
        await using (var inspectConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            await inspectConn.OpenAsync();

            var gameColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var colCmd = inspectConn.CreateCommand())
            {
                colCmd.CommandText = "PRAGMA table_info(games);";
                await using var reader = await colCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    gameColumns.Add(reader.GetString(reader.GetOrdinal("name")));
                }
            }

            Assert.Contains("steam_build_id", gameColumns);
            Assert.Contains("steam_last_updated", gameColumns);
            Assert.Contains("verified_offline_bypass", gameColumns);
            Assert.Contains("verified_offline_bypass_at_utc", gameColumns);

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var tblCmd = inspectConn.CreateCommand())
            {
                tblCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                await using var reader = await tblCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            Assert.Contains("trainer_catalog", tables);
            Assert.Contains("trainer_title_map", tables);
            Assert.Contains("trainer_hotkey_bindings", tables);
            Assert.Contains("pre_trainer_snapshots", tables);

            // Force re-running v4 migration path even if marker row were removed -> PRAGMA table_info must prevent duplicate ALTER failure.
            await using var resetMarker = inspectConn.CreateCommand();
            resetMarker.CommandText = "DELETE FROM schema_migrations WHERE version = 4;";
            await resetMarker.ExecuteNonQueryAsync();
        }

        // 4. Re-initialize twice to prove full idempotency and data preservation.
        using (var reupgraded = new SpawnDatabase(sandbox.Paths))
        {
            await reupgraded.InitializeAsync();
            Assert.Equal(4, await reupgraded.GetSchemaVersionAsync());
            var gameAfterReupgrade = await reupgraded.GetGameByIdAsync(existingGameId);
            Assert.NotNull(gameAfterReupgrade);
            Assert.Equal(4200, gameAfterReupgrade!.TotalActiveSec);
        }

        using (var reopened = new SpawnDatabase(sandbox.Paths))
        {
            await reopened.InitializeAsync();
            Assert.Equal(4, await reopened.GetSchemaVersionAsync());
        }
    }

    [Fact]
    public async Task Database_TrainerFourTables_FullCrudRoundTripAndCascade()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameId = SpawnId.NewUlid();
        string trainerId = SpawnId.NewUlid();
        string snapshot1Id = SpawnId.NewUlid();
        string snapshot2Id = SpawnId.NewUlid();
        DateTimeOffset t0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

        await sandbox.Database.UpsertGameAsync(new GameRecord
        {
            GameId = gameId,
            Title = "艾尔登法环",
            InstallDir = sandbox.CreateSubDirectory("games", "EldenRing"),
            ExecutableRelPath = "eldenring.exe",
            PlatformSource = PlatformSource.SteamLocal,
            AntiCheatTier = AntiCheatTier.Tier0,
            SteamAppId = 1245620,
            SteamBuildId = "15500123",
            SteamLastUpdatedUtc = t0,
        });

        // 1. trainer_title_map CRUD
        var titleMap = new TrainerTitleMapRecord
        {
            GameId = gameId,
            EnglishTitle = "ELDEN RING",
            TitleSource = TrainerTitleSource.AcfName,
            UserSearchOverride = null,
            FlingPageUrl = "https://flingtrainer.com/trainer/elden-ring-trainer/",
            UpdatedAtUtc = t0,
        };
        await sandbox.Database.UpsertTrainerTitleMapAsync(titleMap);

        var loadedTitleMap = await sandbox.Database.GetTrainerTitleMapAsync(gameId);
        Assert.NotNull(loadedTitleMap);
        Assert.Equal("ELDEN RING", loadedTitleMap!.EnglishTitle);
        Assert.Equal(TrainerTitleSource.AcfName, loadedTitleMap.TitleSource);
        Assert.Equal("https://flingtrainer.com/trainer/elden-ring-trainer/", loadedTitleMap.FlingPageUrl);

        Assert.True(await sandbox.Database.UpdateTrainerUserSearchOverrideAsync(gameId, "Elden Ring Shadow"));
        Assert.Equal("Elden Ring Shadow", (await sandbox.Database.GetTrainerTitleMapAsync(gameId))!.UserSearchOverride);

        // 2. trainer_catalog CRUD
        var trainer = new TrainerCatalogRecord
        {
            TrainerId = trainerId,
            GameId = gameId,
            Provider = TrainerProviderKind.FlingMain,
            DisplayName = "Elden Ring v1.02-v1.16.1 Plus 43 Trainer",
            VersionLabel = "v1.02-v1.16.1",
            SourcePageUrl = "https://flingtrainer.com/trainer/elden-ring-trainer/",
            SourceUpdatedAt = "2025.04.16",
            SandboxRelativePath = $"{gameId}\\{trainerId}\\EldenRing_Trainer.exe",
            FileSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            FileSize = 1_048_576,
            PeMachine = "x64",
            RequiresAdmin = true,
            AuthenticodeStatus = TrainerAuthenticodeStatus.Unsigned,
            ParseMode = TrainerParseMode.Structured,
            BoundBuildId = "15500123",
            BoundExeSize = 72_000_000,
            BoundExeLastWriteUtc = t0,
            ImportedAtUtc = t0,
        };
        await sandbox.Database.UpsertTrainerAsync(trainer);

        var loadedTrainer = await sandbox.Database.GetTrainerByIdAsync(trainerId);
        Assert.NotNull(loadedTrainer);
        Assert.Equal(TrainerProviderKind.FlingMain, loadedTrainer!.Provider);
        Assert.Equal(trainer.FileSha256, loadedTrainer.FileSha256);
        Assert.True(loadedTrainer.RequiresAdmin);
        Assert.Equal("x64", loadedTrainer.PeMachine);
        Assert.Equal(TrainerParseMode.Structured, loadedTrainer.ParseMode);
        Assert.Equal("15500123", loadedTrainer.BoundBuildId);
        Assert.Null(loadedTrainer.LastLaunchedAtUtc);

        DateTimeOffset launchedAt = t0.AddMinutes(5);
        Assert.True(await sandbox.Database.UpdateTrainerLastLaunchedAsync(trainerId, launchedAt));
        Assert.Equal(launchedAt, (await sandbox.Database.GetTrainerByIdAsync(trainerId))!.LastLaunchedAtUtc);
        Assert.Single(await sandbox.Database.GetTrainersByGameIdAsync(gameId));
        Assert.Single(await sandbox.Database.GetAllTrainersAsync());

        // 3. trainer_hotkey_bindings CRUD
        await sandbox.Database.ReplaceTrainerHotkeyBindingsAsync(
            trainerId,
            [
                new TrainerHotkeyBindingRecord
                {
                    TrainerId = trainerId,
                    EntryId = "opt_2",
                    Label = "无限精力",
                    Tooltip = "保持精力条满值",
                    OriginalChord = "Num 2",
                    EntryKind = TrainerHotkeyEntryKind.Toggle,
                    ProxyState = false,
                    SortOrder = 2,
                },
                new TrainerHotkeyBindingRecord
                {
                    TrainerId = trainerId,
                    EntryId = "opt_1",
                    Label = "无敌模式 / 无视伤害",
                    OriginalChord = "Num 1",
                    EntryKind = TrainerHotkeyEntryKind.Toggle,
                    ProxyState = false,
                    SortOrder = 1,
                },
            ]);

        var bindings = await sandbox.Database.GetTrainerHotkeyBindingsAsync(trainerId);
        Assert.Equal(2, bindings.Count);
        Assert.Equal("opt_1", bindings[0].EntryId);
        Assert.Equal("opt_2", bindings[1].EntryId);
        Assert.Equal("保持精力条满值", bindings[1].Tooltip);

        Assert.True(await sandbox.Database.UpdateTrainerHotkeyMappedChordAsync(trainerId, "opt_1", "Ctrl+Shift+1"));
        Assert.True(await sandbox.Database.UpdateTrainerHotkeyProxyStateAsync(trainerId, "opt_1", true));
        var updatedBindings = await sandbox.Database.GetTrainerHotkeyBindingsAsync(trainerId);
        Assert.Equal("Ctrl+Shift+1", updatedBindings[0].EffectiveChord);
        Assert.True(updatedBindings[0].ProxyState);

        Assert.Equal(1, await sandbox.Database.ResetTrainerHotkeyProxyStatesAsync(trainerId));
        Assert.False((await sandbox.Database.GetTrainerHotkeyBindingsAsync(trainerId))[0].ProxyState);

        // 4. pre_trainer_snapshots CRUD
        await sandbox.Database.UpsertPreTrainerSnapshotAsync(new PreTrainerSnapshotRecord
        {
            SnapshotId = snapshot1Id,
            GameId = gameId,
            TrainerId = trainerId,
            TriggerKind = PreTrainerSnapshotTriggerKind.PreTrainer,
            Status = PreTrainerSnapshotStatus.Completed,
            FileCount = 4,
            TotalBytes = 65_536,
            ManifestRelativePath = $"{gameId}\\{snapshot1Id}\\manifest.json",
            CreatedAtUtc = t0,
        });
        await sandbox.Database.UpsertPreTrainerSnapshotAsync(new PreTrainerSnapshotRecord
        {
            SnapshotId = snapshot2Id,
            GameId = gameId,
            TrainerId = trainerId,
            TriggerKind = PreTrainerSnapshotTriggerKind.PreRestoreSafety,
            Status = PreTrainerSnapshotStatus.Skipped,
            SkipReason = "SizeThresholdExceeded",
            FileCount = 0,
            TotalBytes = 0,
            CreatedAtUtc = t0.AddMinutes(1),
        });

        var allSnaps = await sandbox.Database.GetPreTrainerSnapshotsByGameAsync(gameId);
        Assert.Equal(2, allSnaps.Count);
        Assert.Equal(snapshot2Id, allSnaps[0].SnapshotId);

        var preTrainerOnly = await sandbox.Database.GetPreTrainerSnapshotsByGameAsync(
            gameId,
            triggerKindFilter: PreTrainerSnapshotTriggerKind.PreTrainer);
        Assert.Single(preTrainerOnly);
        Assert.Equal(snapshot1Id, preTrainerOnly[0].SnapshotId);

        DateTimeOffset restoredAt = t0.AddMinutes(10);
        Assert.True(await sandbox.Database.MarkPreTrainerSnapshotRestoredAsync(snapshot1Id, restoredAt));
        Assert.Equal(restoredAt, (await sandbox.Database.GetPreTrainerSnapshotByIdAsync(snapshot1Id))!.RestoredAtUtc);

        // 5. Cascade checks: deleting trainer cascades hotkeys, keeps snapshots with trainer_id = NULL.
        Assert.True(await sandbox.Database.DeleteTrainerAsync(trainerId));
        Assert.Empty(await sandbox.Database.GetTrainerHotkeyBindingsAsync(trainerId));
        var snapAfterTrainerDelete = await sandbox.Database.GetPreTrainerSnapshotByIdAsync(snapshot1Id);
        Assert.NotNull(snapAfterTrainerDelete);
        Assert.Null(snapAfterTrainerDelete!.TrainerId);

        // Deleting game cascades title map and snapshots.
        Assert.True(await sandbox.Database.DeleteGameAsync(gameId));
        Assert.Null(await sandbox.Database.GetTrainerTitleMapAsync(gameId));
        Assert.Empty(await sandbox.Database.GetPreTrainerSnapshotsByGameAsync(gameId));
    }

    [Fact]
    public async Task Database_VerifiedOfflineBypass_NeverMutatesAntiCheatTierAndSurvivesUpsertRescan()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        string gameId = SpawnId.NewUlid();
        string installDir = sandbox.CreateSubDirectory("games", "HybridSinglePlayer");
        DateTimeOffset confirmedAt = new(2026, 11, 21, 12, 30, 0, TimeSpan.Zero);

        var initialGame = new GameRecord
        {
            GameId = gameId,
            Title = "Hybrid EAC Title",
            InstallDir = installDir,
            ExecutableRelPath = "game.exe",
            PlatformSource = PlatformSource.SteamLocal,
            AntiCheatTier = AntiCheatTier.Tier2,
            AntiCheatSignature = "EasyAntiCheat:EOS",
            SteamAppId = 1245620,
        };
        await sandbox.Database.UpsertGameAsync(initialGame);

        // Enable verified_offline_bypass -> anti_cheat_tier MUST stay Tier2.
        Assert.True(await sandbox.Database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: true, confirmedAtUtc: confirmedAt));

        var bypassInfo = await sandbox.Database.GetGameVerifiedOfflineBypassAsync(gameId);
        Assert.NotNull(bypassInfo);
        Assert.True(bypassInfo!.Value.Enabled);
        Assert.Equal(confirmedAt, bypassInfo.Value.ConfirmedAtUtc);
        Assert.Equal(AntiCheatTier.Tier2, bypassInfo.Value.StaticTier);

        var loaded = await sandbox.Database.GetGameByIdAsync(gameId);
        Assert.NotNull(loaded);
        Assert.Equal(AntiCheatTier.Tier2, loaded!.AntiCheatTier);
        Assert.True(loaded.VerifiedOfflineBypass);
        Assert.Equal(confirmedAt, loaded.VerifiedOfflineBypassAtUtc);

        // Simulate library rescan via UpsertGameAsync with default VerifiedOfflineBypass = false and new build fingerprint.
        DateTimeOffset steamUpdated = new(2026, 11, 21, 13, 0, 0, TimeSpan.Zero);
        await sandbox.Database.UpsertGameAsync(initialGame with
        {
            Title = "Hybrid EAC Title (Rescanned)",
            SteamBuildId = "17009999",
            SteamLastUpdatedUtc = steamUpdated,
            VerifiedOfflineBypass = false,
            VerifiedOfflineBypassAtUtc = null,
        });

        var afterRescan = await sandbox.Database.GetGameByIdAsync(gameId);
        Assert.NotNull(afterRescan);
        Assert.Equal(AntiCheatTier.Tier2, afterRescan!.AntiCheatTier);
        Assert.True(afterRescan.VerifiedOfflineBypass);
        Assert.Equal(confirmedAt, afterRescan.VerifiedOfflineBypassAtUtc);
        Assert.Equal("17009999", afterRescan.SteamBuildId);
        Assert.Equal(steamUpdated, afterRescan.SteamLastUpdatedUtc);

        // Explicit disable clears bypass while preserving static AntiCheatTier.
        Assert.True(await sandbox.Database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: false));
        var afterDisable = await sandbox.Database.GetGameByIdAsync(gameId);
        Assert.NotNull(afterDisable);
        Assert.Equal(AntiCheatTier.Tier2, afterDisable!.AntiCheatTier);
        Assert.False(afterDisable.VerifiedOfflineBypass);
        Assert.Null(afterDisable.VerifiedOfflineBypassAtUtc);
    }

    [Fact]
    public async Task AntiCheatGuardSandboxRoots_IncludeDefaultAndUserCustomRoot()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        IReadOnlyList<string> defaults = SpawnCoreServiceCollectionExtensions.ResolveTrainerSandboxRoots(sandbox.Paths, sandbox.Database);
        Assert.Equal([sandbox.Paths.TrainerSandboxDirectory], defaults);

        string customSandbox = sandbox.CreateSubDirectory("ascii-sandbox");
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto { CustomSandboxRoot = customSandbox });

        IReadOnlyList<string> roots = SpawnCoreServiceCollectionExtensions.ResolveTrainerSandboxRoots(sandbox.Paths, sandbox.Database);
        Assert.Equal(2, roots.Count);
        Assert.Contains(sandbox.Paths.TrainerSandboxDirectory, roots);
        Assert.Contains(Path.TrimEndingDirectorySeparator(customSandbox), roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Database_TrainerSettingsAndKeyValueCache_RoundTripAndTtl()
    {
        await using var sandbox = new TestSandbox();
        await sandbox.Database.InitializeAsync();

        // 1. Verify safe defaults (D-28: FullMute by default, 75ms hold, 5 pre-trainer snapshots, 256MB threshold).
        var defaults = await sandbox.Database.GetTrainerSettingsAsync();
        Assert.Null(defaults.CustomSandboxRoot);
        Assert.Equal(TrainerAudioMode.FullMute, defaults.AudioMode);
        Assert.Equal(0.35, defaults.CueVolume, precision: 3);
        Assert.True(defaults.EnableA0PreMute);
        Assert.Equal(75, defaults.HotkeyHoldMs);
        Assert.True(defaults.CreatePreTrainerSnapshot);
        Assert.Equal(5, defaults.PreTrainerSnapshotRetentionCount);
        Assert.Equal(256, defaults.PreTrainerSnapshotMaxSizeMb);
        Assert.True(defaults.StopWithGameExit);
        Assert.False(defaults.HideOriginalWindowOnLaunch);
        Assert.False(defaults.StrictVanguardMode);
        Assert.False(defaults.IncludeFlingArchiveInSearch);
        Assert.False(defaults.DefaultScanCodeMode);

        // 2. Save custom settings (including out-of-range values to verify clamping).
        string customSandbox = sandbox.CreateSubDirectory("ascii-sandbox");
        await sandbox.Database.SaveTrainerSettingsAsync(new TrainerSettingsDto
        {
            CustomSandboxRoot = customSandbox,
            AudioMode = TrainerAudioMode.KeepCuesExperimental,
            CueVolume = 1.5,
            EnableA0PreMute = false,
            HotkeyHoldMs = 200,
            CreatePreTrainerSnapshot = false,
            PreTrainerSnapshotRetentionCount = 10,
            PreTrainerSnapshotMaxSizeMb = 512,
            StopWithGameExit = false,
            HideOriginalWindowOnLaunch = true,
            StrictVanguardMode = true,
            IncludeFlingArchiveInSearch = true,
            DefaultScanCodeMode = true,
        });

        var loaded = await sandbox.Database.GetTrainerSettingsAsync();
        Assert.Equal(customSandbox, loaded.CustomSandboxRoot);
        Assert.Equal(TrainerAudioMode.KeepCuesExperimental, loaded.AudioMode);
        Assert.Equal(1.0, loaded.CueVolume, precision: 3);
        Assert.False(loaded.EnableA0PreMute);
        Assert.Equal(TrainerSettingsDto.MaxHotkeyHoldMs, loaded.HotkeyHoldMs);
        Assert.False(loaded.CreatePreTrainerSnapshot);
        Assert.Equal(10, loaded.PreTrainerSnapshotRetentionCount);
        Assert.Equal(512, loaded.PreTrainerSnapshotMaxSizeMb);
        Assert.False(loaded.StopWithGameExit);
        Assert.True(loaded.HideOriginalWindowOnLaunch);
        Assert.True(loaded.StrictVanguardMode);
        Assert.True(loaded.IncludeFlingArchiveInSearch);
        Assert.True(loaded.DefaultScanCodeMode);

        // 3. Key-value cache JSON round-trip & TTL expiration.
        var sample = new CachedSteamCategoriesSample(1245620, "ELDEN RING", [2, 1]);
        await sandbox.Database.SetCacheJsonAsync("steam.appdetails.1245620", sample);

        var hit = await sandbox.Database.GetCacheJsonAsync<CachedSteamCategoriesSample>(
            "steam.appdetails.1245620",
            maxAge: TimeSpan.FromMinutes(5));
        Assert.NotNull(hit);
        Assert.Equal("ELDEN RING", hit!.EnglishName);
        Assert.Equal([2, 1], hit.CategoryIds);

        var expired = await sandbox.Database.GetCacheJsonAsync<CachedSteamCategoriesSample>(
            "steam.appdetails.1245620",
            maxAge: TimeSpan.Zero);
        Assert.Null(expired);

        await sandbox.Database.DeleteCacheEntryAsync("steam.appdetails.1245620");
        Assert.Null(await sandbox.Database.GetCacheEntryAsync("steam.appdetails.1245620"));
    }

    [Fact]
    public void SpawnPaths_TrainerDirectoriesAndPipeNames_StrictlyIsolatedInTempSandbox()
    {
        using var sandbox = new TestSandbox();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string prodRoot = Path.GetFullPath(Path.Combine(localAppData, "spawn"));
        string devRoot = Path.GetFullPath(Path.Combine(localAppData, "spawn-dev"));

        // 1. All new M3 directories stay strictly inside isolated temp DataRoot and match spec topology.
        Assert.Equal(Path.Combine(sandbox.Paths.VaultDirectory, "Sandbox", "Trainers"), sandbox.Paths.TrainerSandboxDirectory);
        Assert.Equal(Path.Combine(sandbox.Paths.TrainerSandboxDirectory, ".staging"), sandbox.Paths.TrainerStagingDirectory);
        Assert.Equal(Path.Combine(sandbox.Paths.VaultDirectory, "Snapshots", "PreTrainer"), sandbox.Paths.PreTrainerSnapshotsDirectory);
        Assert.Equal(Path.Combine(sandbox.Paths.CacheDirectory, "trainers"), sandbox.Paths.TrainerCacheDirectory);

        foreach (string dir in new[]
        {
            sandbox.Paths.TrainerSandboxDirectory,
            sandbox.Paths.TrainerStagingDirectory,
            sandbox.Paths.PreTrainerSnapshotsDirectory,
            sandbox.Paths.TrainerCacheDirectory,
        })
        {
            Assert.True(Directory.Exists(dir), $"Expected isolated directory to exist: {dir}");
            Assert.StartsWith(sandbox.Paths.DataRoot, dir, StringComparison.OrdinalIgnoreCase);
            Assert.False(dir.StartsWith(prodRoot, StringComparison.OrdinalIgnoreCase));
            Assert.False(dir.StartsWith(devRoot, StringComparison.OrdinalIgnoreCase));
        }

        // 2. Custom pure-ASCII sandbox root resolution & non-ASCII rejection.
        Assert.Equal(sandbox.Paths.TrainerSandboxDirectory, sandbox.Paths.ResolveTrainerSandboxDirectory(null));
        string asciiCustom = sandbox.CreateSubDirectory("CustomTrainersAscii");
        Assert.Equal(Path.GetFullPath(asciiCustom), sandbox.Paths.ResolveTrainerSandboxDirectory(asciiCustom));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(asciiCustom), ".staging"),
            sandbox.Paths.GetTrainerStagingDirectory(asciiCustom));

        string nonAsciiPath = Path.Combine(sandbox.RootDirectory, "中文沙箱目录");
        Assert.False(SpawnPaths.IsPureAsciiPath(nonAsciiPath));
        Assert.Throws<ArgumentException>(() => sandbox.Paths.ResolveTrainerSandboxDirectory(nonAsciiPath, requirePureAscii: true));
        Assert.Throws<ArgumentException>(() => sandbox.Paths.ResolveTrainerSandboxDirectory("relative\\trainers"));

        // 3. TrainerPipeNamePrefix matches OverlayPipeName isolation prefix and generates unique 128-bit random suffixes.
        string expectedPrefix = sandbox.Paths.OverlayPipeName.Replace("-overlay", "-trainer-", StringComparison.Ordinal);
        Assert.StartsWith("spawn-test-ipc-", sandbox.Paths.TrainerPipeNamePrefix, StringComparison.Ordinal);
        Assert.Equal(expectedPrefix, sandbox.Paths.TrainerPipeNamePrefix);

        string pipe1 = sandbox.Paths.CreateTrainerSessionPipeName();
        string pipe2 = sandbox.Paths.CreateTrainerSessionPipeName();
        Assert.NotEqual(pipe1, pipe2);
        Assert.StartsWith(sandbox.Paths.TrainerPipeNamePrefix, pipe1, StringComparison.Ordinal);
        Assert.StartsWith(sandbox.Paths.TrainerPipeNamePrefix, pipe2, StringComparison.Ordinal);

        string suffix1 = pipe1[sandbox.Paths.TrainerPipeNamePrefix.Length..];
        string suffix2 = pipe2[sandbox.Paths.TrainerPipeNamePrefix.Length..];
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), suffix1);
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), suffix2);
    }

    [Fact]
    public async Task GameDataManagementContract_DefaultValidateSaveRestoreTarget_BlocksTraversalAndHklmAndAllowsDeletedSafeSaveDir()
    {
        using var sandbox = new TestSandbox();
        string existingSaveDir = sandbox.CreateSubDirectory("saves", "ExistingGame");
        string deletedSaveDir = Path.Combine(sandbox.RootDirectory, "saves", "DeletedAfterSnapshot");
        string unsafeDir = sandbox.CreateSubDirectory("install", "GameRoot");
        const string safeRegKey = @"HKCU\Software\SPAWN-Test\StubGame";

        IGameDataManagementService service = new StubGameDataManagementService(
        [
            new GameDataEntryDto
            {
                EntryId = "save-existing",
                GameId = "g1",
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = existingSaveDir,
                Exists = true,
                IsSafeToClean = true,
            },
            new GameDataEntryDto
            {
                EntryId = "save-deleted",
                GameId = "g1",
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = deletedSaveDir,
                Exists = false,
                IsSafeToClean = true,
            },
            new GameDataEntryDto
            {
                EntryId = "save-unsafe",
                GameId = "g1",
                Category = GameDataCategory.SaveData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = unsafeDir,
                Exists = true,
                IsSafeToClean = false,
            },
            new GameDataEntryDto
            {
                EntryId = "reg-deleted",
                GameId = "g1",
                Category = GameDataCategory.RegistryData,
                SourceRule = GameDataSourceRule.NativeRule,
                PathOrRegistryKey = safeRegKey,
                Exists = false,
                IsSafeToClean = true,
            },
        ]);

        // 1. Traversal attempts with '..' or mixed '/' separators MUST be rejected.
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", existingSaveDir + @"\..\..\Windows\System32\evil.dll")).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", existingSaveDir.Replace('\\', '/') + "/../other/save.dat")).IsSafe);

        // 2. Non-HKCU registry hives (HKLM / HKEY_LOCAL_MACHINE) and '..' registry paths MUST be rejected.
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", @"HKLM\SOFTWARE\Microsoft\Windows")).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet")).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", safeRegKey + @"\..\OtherVendor")).IsSafe);

        // 3. Unsafe / sibling prefix / relative paths MUST be rejected.
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", Path.Combine(unsafeDir, "save.bin"))).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", existingSaveDir + "EvilSibling\\save.bin")).IsSafe);
        Assert.False((await service.ValidateSaveRestoreTargetAsync("g1", @"relative\save.bin")).IsSafe);

        // 4. Valid sub-paths (both '\' and '/') under existing AND deleted (Exists == false) safe save dirs MUST be allowed.
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", Path.Combine(existingSaveDir, "slot0", "save.dat"))).IsSafe);
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", existingSaveDir.Replace('\\', '/') + "/slot0/save.dat")).IsSafe);
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", deletedSaveDir)).IsSafe);
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", Path.Combine(deletedSaveDir, "slot1.sav"))).IsSafe);

        // 5. Valid HKCU registry key and subkey (even when deleted: Exists == false) MUST be allowed.
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", safeRegKey)).IsSafe);
        Assert.True((await service.ValidateSaveRestoreTargetAsync("g1", @"HKEY_CURRENT_USER\Software\SPAWN-Test\StubGame\Profile1")).IsSafe);
    }

    private sealed class StubGameDataManagementService(IReadOnlyList<GameDataEntryDto> entries) : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
            string gameId,
            bool refreshOnlineRules = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(entries);

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("stub"));

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("stub"));
    }
}
