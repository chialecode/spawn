using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Discovers, safely backs up, cleans, and restores per-game saves, offline emulator achievement files,
/// configuration/logs, and <c>HKCU</c> registry keys (<see cref="IGameDataManagementService"/>).
/// </summary>
public sealed class GameDataManagementService : IGameDataManagementService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly IGameLaunchService _launchService;
    private readonly IServiceProvider? _serviceProvider;
    private readonly HttpClient _httpClient;
    private readonly GameDataManagementOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameDataManagementService> _logger;

    public GameDataManagementService(
        SpawnDatabase database,
        SpawnPaths paths,
        IGameLaunchService launchService,
        IServiceProvider? serviceProvider = null,
        HttpClient? httpClient = null,
        GameDataManagementOptions? options = null,
        TimeProvider? timeProvider = null,
        ILogger<GameDataManagementService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(launchService);

        _database = database;
        _paths = paths;
        _launchService = launchService;
        _serviceProvider = serviceProvider;
        _httpClient = httpClient ?? GameArtworkExtractor.CreateDefaultHttpClient();
        _options = options ?? new GameDataManagementOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GameDataManagementService>.Instance;
    }

    public async Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return [];
        }

        var identity = BuildTargetIdentity(game);
        var builder = new CandidateListBuilder(game.GameId, game.InstallDir, identity, _paths, _options);

        // 1. PCGamingWiki online + SQLite cached rules.
        var pcgwRules = await ResolvePcGamingWikiRulesAsync(game, refreshOnlineRules, cancellationToken)
            .ConfigureAwait(false);
        foreach (var rawRule in pcgwRules)
        {
            foreach (var (effCategory, resolvedPath) in PcGamingWikiRuleParser.ExpandTemplateRule(rawRule, game.InstallDir, _options, identity))
            {
                builder.TryAdd(
                    category: effCategory,
                    sourceRule: GameDataSourceRule.PCGamingWiki,
                    sourceLabel: "PCGamingWiki",
                    pathOrRegistryKey: resolvedPath,
                    description: effCategory == GameDataCategory.ConfigAndLogs ? "PCGamingWiki 配置与日志位置" : "PCGamingWiki 已知存档数据位置",
                    defaultSelected: effCategory != GameDataCategory.ConfigAndLogs);
            }
        }

        // 2. Zero-config Unity & Unreal Engine inference.
        AddUnityEngineLocations(game, builder);
        AddUnrealEngineLocations(game, builder);

        // 3. Offline Steam Emulators (CODEX, RUNE, Goldberg, gbe_fork, TENOKE, Online-Fix, ALI213, 3DM, SSE, EMPRESS).
        AddSteamEmulatorLocations(game, builder);

        // 4. Native / DRM-free game save & config rules (e.g., LIMBO GOG 1724299977).
        AddNativeAndPortableLocations(game, builder);

        return builder.BuildSorted();
    }

    public async Task<GameDataCleanResult> CleanGameDataAsync(
        GameDataCleanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.GameId))
        {
            return GameDataCleanResult.Fail("游戏标识不能为空。");
        }

        var game = await _database.GetGameByIdAsync(request.GameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameDataCleanResult.Fail("未在游戏库中找到该游戏记录。");
        }

        // Safety Check 1: Refuse cleaning while the game is actively running!
        if (IsGameCurrentlyRunning(game))
        {
            return GameDataCleanResult.Fail($"《{game.Title}》当前正在运行中，请先关闭游戏后再执行数据清理。");
        }

        var identity = BuildTargetIdentity(game);
        var discovered = await InspectGameDataAsync(game.GameId, refreshOnlineRules: false, cancellationToken)
            .ConfigureAwait(false);
        var entryById = discovered.ToDictionary(e => e.EntryId, StringComparer.OrdinalIgnoreCase);

        var requestedIds = request.EntryIds ?? [];
        var selectedEntries = new List<GameDataEntryDto>();
        var warnings = new List<string>();

        foreach (string id in requestedIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (entryById.TryGetValue(id, out var entry))
            {
                selectedEntries.Add(entry);
            }
            else if (id.Contains(Path.DirectorySeparatorChar) || id.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            {
                // Defense-in-depth if a caller passed a raw path directly as an EntryId: run full safety fence!
                bool isReg = id.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase);
                var (safe, reason) = isReg
                    ? MapRegValidation(GameDataSafetyFence.ValidateRegistryTarget(id, _options))
                    : GameDataSafetyFence.ValidateFilesystemTarget(id, game.InstallDir, identity, _paths, _options);

                if (!safe)
                {
                    return GameDataCleanResult.Fail(reason ?? $"安全围栏已拦截不安全路径：{id}");
                }
            }
        }

        // Re-validate every selected entry through the safety fence before touching disk or registry!
        var validatedEntries = new List<GameDataEntryDto>();
        foreach (var entry in selectedEntries)
        {
            if (!entry.IsSafeToClean)
            {
                warnings.Add(entry.SafetyNote ?? $"已跳过受保护路径：{entry.PathOrRegistryKey}");
                continue;
            }

            if (entry.IsRegistryEntry)
            {
                var (safe, _, reason) = GameDataSafetyFence.ValidateRegistryTarget(entry.PathOrRegistryKey, _options);
                if (!safe)
                {
                    warnings.Add(reason ?? $"注册表安全围栏拦截：{entry.PathOrRegistryKey}");
                    continue;
                }
            }
            else
            {
                var (safe, reason) = GameDataSafetyFence.ValidateFilesystemTarget(
                    entry.PathOrRegistryKey,
                    game.InstallDir,
                    identity,
                    _paths,
                    _options);
                if (!safe)
                {
                    warnings.Add(reason ?? $"文件系统安全围栏拦截：{entry.PathOrRegistryKey}");
                    continue;
                }
            }

            validatedEntries.Add(entry);
        }

        if (selectedEntries.Count > 0 && validatedEntries.Count == 0 && !request.ResetSpawnAchievementsAndPlaytime)
        {
            return GameDataCleanResult.Fail(
                warnings.Count > 0 ? warnings[0] : "所选条目均被安全围栏保护，未执行删除。",
                warnings);
        }

        // Deduplicate nested filesystem entries so parent directories aren't processed twice with children.
        var deduplicatedEntries = DeduplicateNestedFilesystemTargets(validatedEntries);
        var existingToClean = deduplicatedEntries.Where(e => DoesTargetCurrentlyExist(e)).ToList();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string snapshotId = $"{now:yyyyMMddHHmmssfff}-{SpawnId.NewUlid(now)[..8]}";
        string gameTrashRoot = _paths.GetGameTrashDirectory(game.GameId);
        string snapshotDir = Path.Combine(gameTrashRoot, snapshotId);

        UndoManifestModel? manifest = null;
        var backedUpItems = new List<UndoManifestItem>();
        long totalBytesProcessed = 0;

        bool shouldWriteBackup = request.CreateUndoBackup
                                 && (existingToClean.Count > 0 || request.ResetSpawnAchievementsAndPlaytime);

        SpawnLedgerBackupModel? ledgerBackup = null;
        if (request.ResetSpawnAchievementsAndPlaytime && shouldWriteBackup)
        {
            var prevSessions = await _database.GetPlaySessionsByGameAsync(game.GameId, limit: 1000, cancellationToken)
                .ConfigureAwait(false);
            var prevUnlocks = await _database.GetAchievementUnlocksAsync(game.GameId, cancellationToken)
                .ConfigureAwait(false);

            ledgerBackup = new SpawnLedgerBackupModel
            {
                TotalActiveSec = game.TotalActiveSec,
                TotalBackgroundSec = game.TotalBackgroundSec,
                TotalAfkSec = game.TotalAfkSec,
                LastPlayedUtc = game.LastPlayedUtc,
                PlaySessions = [.. prevSessions],
                AchievementUnlocks = [.. prevUnlocks],
            };
        }

        // Crash-safe journal: the undo manifest is written as "pending" BEFORE anything is touched and rewritten after
        // every backed-up / removed item, so a power loss or crash mid-cleanup always leaves a restorable snapshot.
        string manifestPath = Path.Combine(snapshotDir, "manifest.json");
        if (shouldWriteBackup)
        {
            Directory.CreateDirectory(snapshotDir);
            manifest = new UndoManifestModel
            {
                SnapshotId = snapshotId,
                GameId = game.GameId,
                CreatedUtc = now,
                Status = UndoManifestStatus.Pending,
                IncludedSpawnStateReset = request.ResetSpawnAchievementsAndPlaytime,
                Items = backedUpItems,
                SpawnLedgerBackup = ledgerBackup,
            };

            try
            {
                await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectoryIfCompletelyEmpty(snapshotDir);
                return GameDataCleanResult.Fail($"无法写入可撤销回收清单，已中止清理且未删除任何数据：{ex.Message}");
            }
        }

        int affectedCount = 0;
        var emptyEmulatorParentDirsToPrune = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < existingToClean.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = existingToClean[i];
            UndoManifestItem? journalItem = null;

            try
            {
                if (entry.IsRegistryEntry)
                {
                    var (safe, subKey, _) = GameDataSafetyFence.ValidateRegistryTarget(entry.PathOrRegistryKey, _options);
                    if (!safe || string.IsNullOrWhiteSpace(subKey))
                    {
                        continue;
                    }

                    using var opened = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
                    if (opened is null)
                    {
                        continue;
                    }

                    var exportedTree = ExportRegistryTree(opened);
                    string regJson = JsonSerializer.Serialize(exportedTree, ManifestJsonOptions);
                    long regBytes = Encoding.UTF8.GetByteCount(regJson);

                    if (shouldWriteBackup)
                    {
                        string regRel = Path.Combine("registry", $"reg_{i:D3}.json");
                        string regDest = Path.Combine(snapshotDir, regRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(regDest)!);
                        await File.WriteAllTextAsync(regDest, regJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

                        journalItem = new UndoManifestItem
                        {
                            EntryId = entry.EntryId,
                            Category = entry.Category,
                            TargetKind = "RegistryKey",
                            OriginalPathOrKey = @"HKCU\" + subKey,
                            RegistrySubKey = subKey,
                            BackupRelPath = regRel,
                            SizeBytes = regBytes,
                        };
                        await AppendJournalItemAsync(manifestPath, manifest!, journalItem, totalBytesProcessed, cancellationToken).ConfigureAwait(false);
                    }

                    opened.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
                    affectedCount++;
                    totalBytesProcessed += regBytes;
                    await MarkJournalItemRemovedAsync(manifestPath, manifest, journalItem, totalBytesProcessed).ConfigureAwait(false);
                }
                else if (Directory.Exists(entry.PathOrRegistryKey))
                {
                    var (_, dirBytes, _) = InspectFilesystemStats(entry.PathOrRegistryKey);
                    if (shouldWriteBackup)
                    {
                        string dirRel = Path.Combine("files", $"dir_{i:D3}");
                        string dirDest = Path.Combine(snapshotDir, dirRel);
                        CopyDirectoryRecursive(entry.PathOrRegistryKey, dirDest);

                        journalItem = new UndoManifestItem
                        {
                            EntryId = entry.EntryId,
                            Category = entry.Category,
                            TargetKind = "Directory",
                            OriginalPathOrKey = entry.PathOrRegistryKey,
                            BackupRelPath = dirRel,
                            SizeBytes = dirBytes,
                        };
                        await AppendJournalItemAsync(manifestPath, manifest!, journalItem, totalBytesProcessed, cancellationToken).ConfigureAwait(false);
                    }

                    Directory.Delete(entry.PathOrRegistryKey, recursive: true);
                    TrackPotentialEmptyAppIdParent(entry, game, emptyEmulatorParentDirsToPrune);
                    affectedCount++;
                    totalBytesProcessed += dirBytes;
                    await MarkJournalItemRemovedAsync(manifestPath, manifest, journalItem, totalBytesProcessed).ConfigureAwait(false);
                }
                else if (File.Exists(entry.PathOrRegistryKey))
                {
                    long fileBytes = new FileInfo(entry.PathOrRegistryKey).Length;
                    if (shouldWriteBackup)
                    {
                        string fileRel = Path.Combine("files", $"file_{i:D3}_{Path.GetFileName(entry.PathOrRegistryKey)}");
                        string fileDest = Path.Combine(snapshotDir, fileRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDest)!);
                        File.Copy(entry.PathOrRegistryKey, fileDest, overwrite: true);

                        journalItem = new UndoManifestItem
                        {
                            EntryId = entry.EntryId,
                            Category = entry.Category,
                            TargetKind = "File",
                            OriginalPathOrKey = entry.PathOrRegistryKey,
                            BackupRelPath = fileRel,
                            SizeBytes = fileBytes,
                        };
                        await AppendJournalItemAsync(manifestPath, manifest!, journalItem, totalBytesProcessed, cancellationToken).ConfigureAwait(false);
                    }

                    File.Delete(entry.PathOrRegistryKey);
                    TrackPotentialEmptyAppIdParent(entry, game, emptyEmulatorParentDirsToPrune);
                    affectedCount++;
                    totalBytesProcessed += fileBytes;
                    await MarkJournalItemRemovedAsync(manifestPath, manifest, journalItem, totalBytesProcessed).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed cleaning entry {Target}.", entry.PathOrRegistryKey);
                }

                warnings.Add($"清理失败 ({entry.PathOrRegistryKey})：{ex.Message}");
            }
        }

        // Clean up empty per-AppId emulator container directories (e.g. ...\Steam\CODEX\1703340 when empty).
        foreach (string parentDir in emptyEmulatorParentDirsToPrune)
        {
            TryDeleteDirectoryIfCompletelyEmpty(parentDir);
        }

        bool spawnStateReset = false;
        if (request.ResetSpawnAchievementsAndPlaytime)
        {
            await _database.ResetGameAchievementsAndPlaytimeAsync(game.GameId, cancellationToken)
                .ConfigureAwait(false);

            var achService = _serviceProvider?.GetService<IAchievementService>();
            if (achService is not null)
            {
                await achService.ResetGameAchievementStateAsync(game.GameId, cancellationToken)
                    .ConfigureAwait(false);
            }

            spawnStateReset = true;
        }

        GameDataUndoSnapshotDto? undoDto = null;
        if (manifest is not null && (backedUpItems.Count > 0 || ledgerBackup is not null))
        {
            manifest.TotalBytes = totalBytesProcessed;
            manifest.IncludedSpawnStateReset = spawnStateReset;
            manifest.Status = UndoManifestStatus.Completed;
            await WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            undoDto = ToUndoDto(manifest, snapshotDir);
        }
        else if (Directory.Exists(snapshotDir))
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the empty pending manifest; it restores nothing.
            }

            TryDeleteDirectoryIfCompletelyEmpty(snapshotDir);
        }

        string summaryMsg = affectedCount > 0 || spawnStateReset
            ? $"已清理 {affectedCount} 项本地数据" + (spawnStateReset ? "，并已重置 SPAWN 成就与游玩时长。" : "。")
            : "所选本地路径当前尚未生成数据，无需删除文件。";

        return GameDataCleanResult.Ok(
            message: summaryMsg,
            affectedEntryCount: affectedCount,
            totalBytesProcessed: totalBytesProcessed,
            spawnStateReset: spawnStateReset,
            undoSnapshot: undoDto,
            warnings: warnings);
    }

    public async Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        var latest = await LoadLatestUndoManifestAsync(gameId, cancellationToken).ConfigureAwait(false);
        return latest is null ? null : ToUndoDto(latest.Value.Manifest, latest.Value.SnapshotDirectory);
    }

    public async Task<GameDataCleanResult> RestoreLastCleanupAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return GameDataCleanResult.Fail("游戏标识不能为空。");
        }

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return GameDataCleanResult.Fail("未在游戏库中找到该游戏记录。");
        }

        if (IsGameCurrentlyRunning(game))
        {
            return GameDataCleanResult.Fail($"《{game.Title}》当前正在运行中，请先关闭游戏后再恢复备份。");
        }

        var latest = await LoadLatestUndoManifestAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return GameDataCleanResult.Fail("未找到可供恢复的清理回收站快照。");
        }

        var (manifest, snapshotDir) = latest.Value;
        var restoreIdentity = BuildTargetIdentity(game);
        int restoredCount = 0;
        long restoredBytes = 0;
        var warnings = new List<string>();

        foreach (var item in manifest.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payloadPath = Path.Combine(snapshotDir, item.BackupRelPath);

            try
            {
                switch (item.TargetKind)
                {
                    case "Directory":
                        if (Directory.Exists(payloadPath))
                        {
                            var (safe, reason) = GameDataSafetyFence.ValidateFilesystemTarget(
                                item.OriginalPathOrKey,
                                game.InstallDir,
                                restoreIdentity,
                                _paths,
                                _options);
                            if (!safe)
                            {
                                warnings.Add(reason ?? $"跳过恢复受保护目录：{item.OriginalPathOrKey}");
                                continue;
                            }

                            CopyDirectoryRecursive(payloadPath, item.OriginalPathOrKey);
                            restoredCount++;
                            restoredBytes += item.SizeBytes;
                        }

                        break;

                    case "File":
                        if (File.Exists(payloadPath))
                        {
                            var (safe, reason) = GameDataSafetyFence.ValidateFilesystemTarget(
                                item.OriginalPathOrKey,
                                game.InstallDir,
                                restoreIdentity,
                                _paths,
                                _options);
                            if (!safe)
                            {
                                warnings.Add(reason ?? $"跳过恢复受保护文件：{item.OriginalPathOrKey}");
                                continue;
                            }

                            string? parentDir = Path.GetDirectoryName(item.OriginalPathOrKey);
                            if (!string.IsNullOrWhiteSpace(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }

                            File.Copy(payloadPath, item.OriginalPathOrKey, overwrite: true);
                            restoredCount++;
                            restoredBytes += item.SizeBytes;
                        }

                        break;

                    case "RegistryKey":
                        if (File.Exists(payloadPath) && !string.IsNullOrWhiteSpace(item.RegistrySubKey))
                        {
                            var (safe, validatedSubKey, reason) = GameDataSafetyFence.ValidateRegistryTarget(
                                @"HKCU\" + item.RegistrySubKey,
                                _options);
                            if (!safe || string.IsNullOrWhiteSpace(validatedSubKey))
                            {
                                warnings.Add(reason ?? $"跳过恢复不安全注册表项：{item.OriginalPathOrKey}");
                                continue;
                            }

                            string rawJson = await File.ReadAllTextAsync(payloadPath, Encoding.UTF8, cancellationToken)
                                .ConfigureAwait(false);
                            var tree = JsonSerializer.Deserialize<RegistryNodeModel>(rawJson, ManifestJsonOptions);
                            if (tree is not null)
                            {
                                using var key = Registry.CurrentUser.CreateSubKey(validatedSubKey, writable: true);
                                if (key is not null)
                                {
                                    ImportRegistryTree(key, tree);
                                    restoredCount++;
                                    restoredBytes += item.SizeBytes;
                                }
                            }
                        }

                        break;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"恢复失败 ({item.OriginalPathOrKey})：{ex.Message}");
            }
        }

        bool restoredSpawnLedger = false;
        if (manifest.IncludedSpawnStateReset && manifest.SpawnLedgerBackup is not null)
        {
            var lb = manifest.SpawnLedgerBackup;
            await _database.RestoreGamePlaytimeAndUnlocksSnapshotAsync(
                game.GameId,
                lb.TotalActiveSec,
                lb.TotalBackgroundSec,
                lb.TotalAfkSec,
                lb.LastPlayedUtc,
                lb.PlaySessions,
                lb.AchievementUnlocks,
                cancellationToken).ConfigureAwait(false);

            var achService = _serviceProvider?.GetService<IAchievementService>();
            if (achService is not null)
            {
                _ = await achService.ScanLocalUnlocksAsync(game.GameId, silentSeed: true, cancellationToken)
                    .ConfigureAwait(false);
            }

            restoredSpawnLedger = true;
        }

        // Remove the consumed trash snapshot directory.
        try
        {
            string trashRootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.TrashDir)) + Path.DirectorySeparatorChar;
            string fullSnapDir = Path.GetFullPath(snapshotDir);
            if (fullSnapDir.StartsWith(trashRootPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullSnapDir))
            {
                Directory.Delete(fullSnapDir, recursive: true);
            }
        }
        catch
        {
            // Non-fatal if OS still holds a transient handle in trash folder.
        }

        return GameDataCleanResult.Ok(
            message: $"已从回收快照完整恢复 {restoredCount} 项数据" + (restoredSpawnLedger ? "及 SPAWN 成就/时长记录。" : "。"),
            affectedEntryCount: restoredCount,
            totalBytesProcessed: restoredBytes,
            spawnStateReset: restoredSpawnLedger,
            warnings: warnings);
    }

    // -------------------------------------------------------------------------
    // Rule Discovery Providers (PCGamingWiki + Unity/UE + Emulators + Native)
    // -------------------------------------------------------------------------

    private async Task<IReadOnlyList<PcGamingWikiRawRule>> ResolvePcGamingWikiRulesAsync(
        GameRecord game,
        bool refreshOnlineRules,
        CancellationToken cancellationToken)
    {
        var cachedRecord = await _database.GetGameDataRuleCacheByGameAsync(
            game.GameId,
            game.SteamAppId,
            game.GogGameId,
            cancellationToken).ConfigureAwait(false);

        if (!refreshOnlineRules && cachedRecord is not null)
        {
            return DeserializeCachedRules(cachedRecord.RulesJson);
        }

        if (_options.AllowOnlinePcGamingWikiLookup)
        {
            var fetched = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
                _httpClient,
                game.SteamAppId,
                game.GogGameId,
                game.Title,
                _options.OnlineLookupTimeout,
                cancellationToken).ConfigureAwait(false);

            if (fetched is not null)
            {
                string cacheKey = game.SteamAppId is > 0
                    ? $"pcgw:steam:{game.SteamAppId.Value}"
                    : (!string.IsNullOrWhiteSpace(game.GogGameId)
                        ? $"pcgw:gog:{game.GogGameId}"
                        : $"pcgw:game:{game.GameId}");

                string rulesJson = JsonSerializer.Serialize(fetched, ManifestJsonOptions);
                await _database.UpsertGameDataRuleCacheAsync(
                    new GameDataRuleCacheRecord
                    {
                        CacheKey = cacheKey,
                        GameId = game.GameId,
                        SteamAppId = game.SteamAppId,
                        GogGameId = game.GogGameId,
                        Provider = "PCGamingWiki",
                        RulesJson = rulesJson,
                        FetchedUtc = _timeProvider.GetUtcNow(),
                    },
                    cancellationToken).ConfigureAwait(false);

                return fetched;
            }
        }

        return cachedRecord is not null ? DeserializeCachedRules(cachedRecord.RulesJson) : [];
    }

    private static GameDataTargetIdentity BuildTargetIdentity(GameRecord game)
    {
        var extras = new List<string?>();
        var unity = GameEngineInspector.TryReadUnityAppInfo(game.InstallDir);
        if (unity is not null)
        {
            extras.Add(unity.ProductName);
        }

        extras.Add(GameEngineInspector.TryDetectUnrealProjectName(game.InstallDir));
        return GameDataTargetIdentity.FromGame(game, extras);
    }

    private void AddUnityEngineLocations(GameRecord game, CandidateListBuilder builder)
    {
        var unityInfo = GameEngineInspector.TryReadUnityAppInfo(game.InstallDir);
        bool hasUnityDll = File.Exists(Path.Combine(game.InstallDir, "UnityPlayer.dll"));
        if (unityInfo is null && !hasUnityDll)
        {
            return;
        }

        string company = unityInfo?.CompanyName ?? string.Empty;
        string product = unityInfo?.ProductName ?? game.Title;

        string localLow = _options.ResolveLocalLow();
        if (!string.IsNullOrWhiteSpace(localLow) && !string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product))
        {
            string safeCompany = GameEngineInspector.SanitizeUnityPathSegment(company);
            string safeProduct = GameEngineInspector.SanitizeUnityPathSegment(product);

            // Primary Unity persistentDataPath (converts ':' -> '_', e.g. 'The Stanley Parable_ Ultra Deluxe').
            string primaryLocalLow = Path.Combine(localLow, safeCompany, safeProduct);
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.UnityEngine,
                "Unity 引擎",
                primaryLocalLow,
                "Unity LocalLow 持久存档与运行数据目录",
                defaultSelected: true);

            // Secondary colon-stripped variant if present on disk (e.g. 'The Stanley Parable Ultra Deluxe').
            string strippedProduct = GameEngineInspector.SanitizeUnityPathSegment(product.Replace(":", string.Empty, StringComparison.Ordinal));
            if (!string.Equals(strippedProduct, safeProduct, StringComparison.OrdinalIgnoreCase))
            {
                string altLocalLow = Path.Combine(localLow, safeCompany, strippedProduct);
                if (Directory.Exists(altLocalLow))
                {
                    builder.TryAdd(
                        GameDataCategory.SaveData,
                        GameDataSourceRule.UnityEngine,
                        "Unity 引擎",
                        altLocalLow,
                        "Unity LocalLow 兼容存档目录",
                        defaultSelected: true);
                }
            }

            // Unity HKCU\Software\<Company>\<Product> PlayerPrefs registry key.
            string rawRegKey = $@"HKCU\Software\{company}\{product}";
            string effectiveRegKey = PcGamingWikiRuleParser.RewriteHkcuSoftwarePrefix(rawRegKey, _options.HkcuSoftwarePrefix);
            builder.TryAdd(
                GameDataCategory.RegistryData,
                GameDataSourceRule.UnityEngine,
                "Unity 引擎",
                effectiveRegKey,
                "Unity PlayerPrefs 注册表配置与进度键值",
                defaultSelected: true);

            if (!string.Equals(safeProduct, product, StringComparison.OrdinalIgnoreCase))
            {
                string altRegKey = PcGamingWikiRuleParser.RewriteHkcuSoftwarePrefix($@"HKCU\Software\{company}\{safeProduct}", _options.HkcuSoftwarePrefix);
                if (DoesRegistrySubKeyExist(altRegKey))
                {
                    builder.TryAdd(
                        GameDataCategory.RegistryData,
                        GameDataSourceRule.UnityEngine,
                        "Unity 引擎",
                        altRegKey,
                        "Unity PlayerPrefs 注册表兼容键值",
                        defaultSelected: true);
                }
            }
        }

        // Roaming %APPDATA%\<Product_Underscore> pattern (used by titles like Vampire_Survivors).
        string roaming = _options.ResolveAppDataRoaming();
        if (!string.IsNullOrWhiteSpace(roaming) && !string.IsNullOrWhiteSpace(product))
        {
            string underscoreProduct = GameEngineInspector.SanitizeUnityPathSegment(product.Replace(' ', '_'));
            string roamingCandidate = Path.Combine(roaming, underscoreProduct);
            if (Directory.Exists(roamingCandidate) || game.SteamAppId == 1794680)
            {
                builder.TryAdd(
                    GameDataCategory.SaveData,
                    GameDataSourceRule.UnityEngine,
                    "Unity / AppData",
                    roamingCandidate,
                    "AppData Roaming 游戏存档目录",
                    defaultSelected: true);
            }
        }
    }

    private void AddUnrealEngineLocations(GameRecord game, CandidateListBuilder builder)
    {
        string? ueProject = GameEngineInspector.TryDetectUnrealProjectName(game.InstallDir);
        if (string.IsNullOrWhiteSpace(ueProject))
        {
            return;
        }

        string localAppData = _options.ResolveLocalAppData();
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return;
        }

        string savedRoot = Path.Combine(localAppData, ueProject, "Saved");
        builder.TryAdd(
            GameDataCategory.SaveData,
            GameDataSourceRule.UnrealEngine,
            "Unreal 引擎",
            Path.Combine(savedRoot, "SaveGames"),
            "Unreal Engine 本地存档目录 (SaveGames)",
            defaultSelected: true);

        builder.TryAdd(
            GameDataCategory.ConfigAndLogs,
            GameDataSourceRule.UnrealEngine,
            "Unreal 引擎",
            Path.Combine(savedRoot, "Config"),
            "Unreal Engine 画质与键位配置 (Saved\\Config)",
            defaultSelected: false);

        string logsDir = Path.Combine(savedRoot, "Logs");
        if (Directory.Exists(logsDir))
        {
            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                GameDataSourceRule.UnrealEngine,
                "Unreal 引擎",
                logsDir,
                "Unreal Engine 运行日志",
                defaultSelected: false);
        }
    }

    private void AddSteamEmulatorLocations(GameRecord game, CandidateListBuilder builder)
    {
        var (emuInfo, inspectedAppId, _, _) = GameEngineInspector.InspectSteamFiles(game.InstallDir);
        int? appId = game.SteamAppId ?? inspectedAppId;
        string? activeFlavor = emuInfo?.EmulatorFlavor;

        string pubDocs = _options.ResolvePublicDocuments();
        string roaming = _options.ResolveAppDataRoaming();
        string localAppData = _options.ResolveLocalAppData();
        string userDocs = _options.ResolveUserDocuments();

        if (appId is > 0)
        {
            string idStr = appId.Value.ToString(CultureInfo.InvariantCulture);

            // 1. CODEX (%PUBLIC%\Documents\Steam\CODEX\<AppId> & %APPDATA%\Steam\CODEX\<AppId>)
            AddSceneEmulatorPair(
                builder,
                flavor: "CODEX",
                isActiveFlavor: string.Equals(activeFlavor, "CODEX", StringComparison.OrdinalIgnoreCase),
                primaryRoot: Path.Combine(pubDocs, "Steam", "CODEX", idStr),
                secondaryRoot: Path.Combine(roaming, "Steam", "CODEX", idStr),
                saveSubPath: "remote",
                achSubPath: "achievements.ini");

            // 2. RUNE (%PUBLIC%\Documents\Steam\RUNE\<AppId> & %APPDATA%\Steam\RUNE\<AppId>)
            AddSceneEmulatorPair(
                builder,
                flavor: "RUNE",
                isActiveFlavor: string.Equals(activeFlavor, "RUNE", StringComparison.OrdinalIgnoreCase),
                primaryRoot: Path.Combine(pubDocs, "Steam", "RUNE", idStr),
                secondaryRoot: Path.Combine(roaming, "Steam", "RUNE", idStr),
                saveSubPath: "remote",
                achSubPath: "achievements.ini");

            // 3. gbe_fork (GSE Saves)
            string gseRoot = Path.Combine(roaming, "GSE Saves", idStr);
            if (string.Equals(activeFlavor, "gbe_fork", StringComparison.OrdinalIgnoreCase)
                || game.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
                || Directory.Exists(gseRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "Goldberg GSE", Path.Combine(gseRoot, "remote"), "Goldberg GSE 离线存档 (remote)", defaultSelected: true);
                builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "Goldberg GSE", Path.Combine(gseRoot, "achievements.json"), "Goldberg GSE 离线成就记录 (achievements.json)", defaultSelected: true);
            }

            // 4. Original Goldberg (%APPDATA%\Goldberg SteamEmu Saves\<AppId>)
            string goldbergRoot = Path.Combine(roaming, "Goldberg SteamEmu Saves", idStr);
            if (string.Equals(activeFlavor, "Goldberg", StringComparison.OrdinalIgnoreCase) || Directory.Exists(goldbergRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "Goldberg", Path.Combine(goldbergRoot, "remote"), "Goldberg 离线存档 (remote)", defaultSelected: true);
                builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "Goldberg", Path.Combine(goldbergRoot, "achievements.json"), "Goldberg 离线成就记录 (achievements.json)", defaultSelected: true);
            }

            // 5. Online-Fix (%PUBLIC%\Documents\OnlineFix\<AppId>)
            string onlineFixRoot = Path.Combine(pubDocs, "OnlineFix", idStr);
            if (string.Equals(activeFlavor, "OnlineFix", StringComparison.OrdinalIgnoreCase) || Directory.Exists(onlineFixRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "Online-Fix", Path.Combine(onlineFixRoot, "Saves"), "Online-Fix 存档目录", defaultSelected: true);
                builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "Online-Fix", Path.Combine(onlineFixRoot, "Stats", "Achievements.ini"), "Online-Fix 成就解锁文件", defaultSelected: true);
            }

            // 6. SmartSteamEmu (%APPDATA%\SmartSteamEmu\<AppId>)
            string sseRoot = Path.Combine(roaming, "SmartSteamEmu", idStr);
            if (string.Equals(activeFlavor, "SSE", StringComparison.OrdinalIgnoreCase) || Directory.Exists(sseRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "SmartSteamEmu", Path.Combine(sseRoot, "remote"), "SmartSteamEmu 存档目录", defaultSelected: true);
                builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "SmartSteamEmu", Path.Combine(sseRoot, "stats.bin"), "SmartSteamEmu 二进制成就统计 (stats.bin)", defaultSelected: true);
            }

            // 7. EMPRESS (%PUBLIC%\Documents\Steam\EMPRESS\<AppId>)
            string empressRoot = Path.Combine(pubDocs, "Steam", "EMPRESS", idStr);
            if (Directory.Exists(empressRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "EMPRESS", empressRoot, "EMPRESS 离线存档与成就目录", defaultSelected: true);
            }

            // 8. SKIDROW / Hoodlum
            string skidrowRoot = Path.Combine(localAppData, "SKIDROW", idStr);
            if (Directory.Exists(skidrowRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "SKIDROW", skidrowRoot, "SKIDROW 离线存档与成就目录", defaultSelected: true);
            }

            string hoodlumRoot = Path.Combine(roaming, "Hoodlum", idStr);
            if (Directory.Exists(hoodlumRoot))
            {
                builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "Hoodlum", hoodlumRoot, "Hoodlum 离线存档与成就目录", defaultSelected: true);
            }

            // 9. ALI213 Documents variant
            if (!string.IsNullOrWhiteSpace(userDocs))
            {
                string aliDocs = Path.Combine(userDocs, "VALVE", idStr, "ALI213");
                if (Directory.Exists(aliDocs))
                {
                    builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "ALI213", Path.Combine(aliDocs, "Saves"), "游侠 ALI213 文档存档目录", defaultSelected: true);
                    builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "ALI213", Path.Combine(aliDocs, "Stats", "Achievements.Bin"), "游侠 ALI213 成就记录 (Achievements.Bin)", defaultSelected: true);
                }
            }
        }

        // In-game portable emulator folders (TENOKE SteamData, ALI213 Profile, 3DMGAME)
        string tenokeDir = Path.Combine(game.InstallDir, "SteamData");
        if (string.Equals(activeFlavor, "TENOKE", StringComparison.OrdinalIgnoreCase) || Directory.Exists(tenokeDir))
        {
            builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "TENOKE", Path.Combine(tenokeDir, "remote"), "TENOKE 便携存档目录 (SteamData\\remote)", defaultSelected: true);
            builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "TENOKE", Path.Combine(tenokeDir, "user_stats.ini"), "TENOKE 离线成就记录 (user_stats.ini)", defaultSelected: true);
        }

        string aliInGame = Path.Combine(game.InstallDir, "Profile", "ALI213");
        if (string.Equals(activeFlavor, "ALI213", StringComparison.OrdinalIgnoreCase) || Directory.Exists(aliInGame))
        {
            builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "ALI213", Path.Combine(aliInGame, "Saves"), "游侠 ALI213 游戏内便携存档", defaultSelected: true);
            builder.TryAdd(GameDataCategory.AchievementData, GameDataSourceRule.SteamEmulator, "ALI213", Path.Combine(aliInGame, "Stats", "Achievements.Bin"), "游侠 ALI213 成就记录 (Achievements.Bin)", defaultSelected: true);
        }

        string dm3InGame = Path.Combine(game.InstallDir, "3DMGAME");
        if (string.Equals(activeFlavor, "3DM", StringComparison.OrdinalIgnoreCase) || Directory.Exists(dm3InGame))
        {
            builder.TryAdd(GameDataCategory.SaveData, GameDataSourceRule.SteamEmulator, "3DM", dm3InGame, "3DM 游戏内便携存档与成就目录", defaultSelected: true);
        }
    }

    private static void AddSceneEmulatorPair(
        CandidateListBuilder builder,
        string flavor,
        bool isActiveFlavor,
        string primaryRoot,
        string secondaryRoot,
        string saveSubPath,
        string achSubPath)
    {
        if (isActiveFlavor || Directory.Exists(primaryRoot))
        {
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.SteamEmulator,
                flavor,
                Path.Combine(primaryRoot, saveSubPath),
                $"{flavor} 离线存档目录 ({saveSubPath})",
                defaultSelected: true);

            builder.TryAdd(
                GameDataCategory.AchievementData,
                GameDataSourceRule.SteamEmulator,
                flavor,
                Path.Combine(primaryRoot, achSubPath),
                $"{flavor} 离线成就解锁记录 ({achSubPath})",
                defaultSelected: true);
        }

        if (Directory.Exists(secondaryRoot))
        {
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.SteamEmulator,
                $"{flavor} (AppData)",
                Path.Combine(secondaryRoot, saveSubPath),
                $"{flavor} AppData 离线存档目录",
                defaultSelected: true);

            builder.TryAdd(
                GameDataCategory.AchievementData,
                GameDataSourceRule.SteamEmulator,
                $"{flavor} (AppData)",
                Path.Combine(secondaryRoot, achSubPath),
                $"{flavor} AppData 离线成就记录",
                defaultSelected: true);
        }
    }

    private void AddNativeAndPortableLocations(GameRecord game, CandidateListBuilder builder)
    {
        bool isLimbo = string.Equals(game.GogGameId, "1724299977", StringComparison.OrdinalIgnoreCase)
                       || game.SteamAppId == 48000;

        if (isLimbo)
        {
            string saveGameDir = Path.Combine(game.InstallDir, "save_game");
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.NativeRule,
                "GOG 原生存档",
                saveGameDir,
                "LIMBO 原生加密存档与内置 13 项成就记录 (save_game\\savegame.txt)",
                defaultSelected: true);

            string rootSaveTxt = Path.Combine(game.InstallDir, "savegame.txt");
            if (File.Exists(rootSaveTxt))
            {
                builder.TryAdd(
                    GameDataCategory.SaveData,
                    GameDataSourceRule.NativeRule,
                    "GOG 原生存档",
                    rootSaveTxt,
                    "LIMBO 根目录原生存档 (savegame.txt)",
                    defaultSelected: true);
            }

            string roaming = _options.ResolveAppDataRoaming();
            if (!string.IsNullOrWhiteSpace(roaming))
            {
                string playdeadDir = Path.Combine(roaming, "Playdead", "Limbo");
                if (Directory.Exists(playdeadDir))
                {
                    builder.TryAdd(
                        GameDataCategory.SaveData,
                        GameDataSourceRule.NativeRule,
                        "原生存档",
                        playdeadDir,
                        "Playdead Limbo AppData 存档目录",
                        defaultSelected: true);
                }
            }

            string settingsTxt = Path.Combine(game.InstallDir, "settings.txt");
            if (File.Exists(settingsTxt))
            {
                builder.TryAdd(
                    GameDataCategory.ConfigAndLogs,
                    GameDataSourceRule.NativeRule,
                    "游戏配置",
                    settingsTxt,
                    "LIMBO 显示与语言配置文件 (settings.txt)",
                    defaultSelected: false);
            }
        }
        else
        {
            // Generic portable save subfolder probe inside <GameDir> if present.
            foreach (string subName in new[] { "save_game", "saves", "savedata" })
            {
                string candidate = Path.Combine(game.InstallDir, subName);
                if (Directory.Exists(candidate))
                {
                    builder.TryAdd(
                        GameDataCategory.SaveData,
                        GameDataSourceRule.NativeRule,
                        "便携存档",
                        candidate,
                        $"游戏内便携存档目录 ({subName})",
                        defaultSelected: true);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Candidate Aggregation, Inspection & Registry / Backup Helpers
    // -------------------------------------------------------------------------

    public static string ComputeDeterministicEntryId(string gameId, GameDataCategory category, string normalizedTarget)
    {
        string raw = $"{gameId}|{category}|{normalizedTarget.ToUpperInvariant()}";
        ulong hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(raw));
        return $"gd-{hash:x16}";
    }

    private sealed class CandidateListBuilder(
        string gameId,
        string installDir,
        GameDataTargetIdentity identity,
        SpawnPaths spawnPaths,
        GameDataManagementOptions options)
    {
        private readonly Dictionary<string, GameDataEntryDto> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void TryAdd(
            GameDataCategory category,
            GameDataSourceRule sourceRule,
            string sourceLabel,
            string pathOrRegistryKey,
            string description,
            bool defaultSelected)
        {
            if (string.IsNullOrWhiteSpace(pathOrRegistryKey))
            {
                return;
            }

            bool isRegistry = category == GameDataCategory.RegistryData
                              || pathOrRegistryKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
                              || pathOrRegistryKey.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
                              || pathOrRegistryKey.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase);

            string normalizedKey;
            bool isSafe;
            string? safetyNote;
            bool exists = false;
            int fileCount = 0;
            long totalBytes = 0;
            DateTimeOffset? lastModifiedUtc = null;

            if (isRegistry)
            {
                category = GameDataCategory.RegistryData;
                normalizedKey = pathOrRegistryKey.Trim().Replace('/', '\\').TrimEnd('\\');
                var (safe, subKey, reason) = GameDataSafetyFence.ValidateRegistryTarget(normalizedKey, options);
                isSafe = safe;
                safetyNote = reason;

                if (!string.IsNullOrWhiteSpace(subKey))
                {
                    var (regExists, valCount, approxBytes) = InspectRegistryStats(subKey);
                    exists = regExists;
                    fileCount = valCount;
                    totalBytes = approxBytes;
                }
            }
            else
            {
                try
                {
                    normalizedKey = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathOrRegistryKey));
                }
                catch
                {
                    return;
                }

                var (safe, reason) = GameDataSafetyFence.ValidateFilesystemTarget(normalizedKey, installDir, identity, spawnPaths, options);
                isSafe = safe;
                safetyNote = reason;

                if (Directory.Exists(normalizedKey) || File.Exists(normalizedKey))
                {
                    exists = true;
                    (fileCount, totalBytes, lastModifiedUtc) = InspectFilesystemStats(normalizedKey);
                }
            }

            if (_entries.TryGetValue(normalizedKey, out var existing))
            {
                // Prefer more specific emulator/engine label when PCGamingWiki also reported the same folder.
                if (existing.SourceRule == GameDataSourceRule.PCGamingWiki && sourceRule != GameDataSourceRule.PCGamingWiki)
                {
                    _entries[normalizedKey] = existing with
                    {
                        SourceRule = sourceRule,
                        SourceLabel = sourceLabel,
                        Description = description,
                    };
                }

                return;
            }

            string entryId = ComputeDeterministicEntryId(gameId, category, normalizedKey);
            _entries[normalizedKey] = new GameDataEntryDto
            {
                EntryId = entryId,
                GameId = gameId,
                Category = category,
                SourceRule = sourceRule,
                SourceLabel = sourceLabel,
                PathOrRegistryKey = normalizedKey,
                Description = description,
                Exists = exists,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                IsSafeToClean = isSafe,
                SafetyNote = safetyNote,
                DefaultSelected = isSafe && defaultSelected,
                LastModifiedUtc = lastModifiedUtc,
            };
        }

        public List<GameDataEntryDto> BuildSorted() =>
            _entries.Values
                .OrderBy(e => e.Category)
                .ThenByDescending(e => e.Exists)
                .ThenBy(e => e.PathOrRegistryKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    private bool IsGameCurrentlyRunning(GameRecord game) =>
        _launchService.GetActiveSession(game.GameId) is not null;

    private static bool DoesTargetCurrentlyExist(GameDataEntryDto entry)
    {
        if (entry.IsRegistryEntry)
        {
            return DoesRegistrySubKeyExist(entry.PathOrRegistryKey);
        }

        return Directory.Exists(entry.PathOrRegistryKey) || File.Exists(entry.PathOrRegistryKey);
    }

    private static bool DoesRegistrySubKeyExist(string regPath)
    {
        const string HkcuPrefix = @"HKCU\";
        if (!regPath.StartsWith(HkcuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(regPath[HkcuPrefix.Length..], writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static (bool Exists, int ValueCount, long ApproxBytes) InspectRegistryStats(string subKeyUnderCurrentUser)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(subKeyUnderCurrentUser, writable: false);
            if (root is null)
            {
                return (false, 0, 0);
            }

            int count = 0;
            long bytes = 0;

            void WalkKey(RegistryKey key, int depth)
            {
                if (depth > 8)
                {
                    return;
                }

                foreach (string valName in key.GetValueNames())
                {
                    count++;
                    object? val = key.GetValue(valName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    bytes += EstimateRegistryValueSize(val);
                }

                foreach (string childName in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(childName, writable: false);
                    if (child is not null)
                    {
                        WalkKey(child, depth + 1);
                    }
                }
            }

            WalkKey(root, 0);
            return (true, Math.Max(1, count), bytes);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    private static long EstimateRegistryValueSize(object? val) => val switch
    {
        byte[] b => b.Length,
        string s => Encoding.UTF8.GetByteCount(s),
        string[] arr => arr.Sum(x => Encoding.UTF8.GetByteCount(x ?? string.Empty)),
        int => 4,
        long => 8,
        _ => 8,
    };

    private static (int FileCount, long TotalBytes, DateTimeOffset? LastModifiedUtc) InspectFilesystemStats(string targetPath)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                var fi = new FileInfo(targetPath);
                return (1, fi.Length, new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero));
            }

            if (!Directory.Exists(targetPath))
            {
                return (0, 0, null);
            }

            int count = 0;
            long bytes = 0;
            DateTime latestUtc = DateTime.MinValue;

            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (string file in Directory.EnumerateFiles(targetPath, "*", opts))
            {
                var fi = new FileInfo(file);
                count++;
                bytes += fi.Length;
                if (fi.LastWriteTimeUtc > latestUtc)
                {
                    latestUtc = fi.LastWriteTimeUtc;
                }
            }

            DateTimeOffset? mod = latestUtc > DateTime.MinValue
                ? new DateTimeOffset(latestUtc, TimeSpan.Zero)
                : new DateTimeOffset(Directory.GetLastWriteTimeUtc(targetPath), TimeSpan.Zero);

            return (count, bytes, mod);
        }
        catch
        {
            return (0, 0, null);
        }
    }

    private static List<GameDataEntryDto> DeduplicateNestedFilesystemTargets(List<GameDataEntryDto> entries)
    {
        var dirs = entries
            .Where(e => !e.IsRegistryEntry && Directory.Exists(e.PathOrRegistryKey))
            .Select(e => Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.PathOrRegistryKey)))
            .OrderBy(p => p.Length)
            .ToList();

        var result = new List<GameDataEntryDto>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsRegistryEntry)
            {
                result.Add(entry);
                continue;
            }

            string norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.PathOrRegistryKey));
            bool coveredByParent = dirs.Any(d =>
                !string.Equals(d, norm, StringComparison.OrdinalIgnoreCase)
                && norm.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

            if (!coveredByParent)
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private static void TrackPotentialEmptyAppIdParent(
        GameDataEntryDto entry,
        GameRecord game,
        HashSet<string> candidateParents)
    {
        if (entry.SourceRule != GameDataSourceRule.SteamEmulator || !game.SteamAppId.HasValue)
        {
            return;
        }

        string idStr = game.SteamAppId.Value.ToString(CultureInfo.InvariantCulture);
        var parent = Directory.GetParent(entry.PathOrRegistryKey);
        while (parent is not null)
        {
            if (string.Equals(parent.Name, idStr, StringComparison.OrdinalIgnoreCase))
            {
                candidateParents.Add(parent.FullName);
                break;
            }

            parent = parent.Parent;
        }
    }

    private static void TryDeleteDirectoryIfCompletelyEmpty(string dirPath)
    {
        try
        {
            if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
            {
                Directory.Delete(dirPath, recursive: false);
            }
        }
        catch
        {
            // Ignore non-empty or locked folder.
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            var info = new DirectoryInfo(subDir);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            CopyDirectoryRecursive(subDir, Path.Combine(destinationDir, info.Name));
        }
    }

    private static RegistryNodeModel ExportRegistryTree(RegistryKey key)
    {
        var values = new List<RegistryValueEntryModel>();
        foreach (string name in key.GetValueNames())
        {
            RegistryValueKind kind = key.GetValueKind(name);
            object? rawVal = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (rawVal is null)
            {
                continue;
            }

            string encoded = kind switch
            {
                RegistryValueKind.Binary when rawVal is byte[] bytes => Convert.ToBase64String(bytes),
                RegistryValueKind.MultiString when rawVal is string[] arr => JsonSerializer.Serialize(arr),
                RegistryValueKind.DWord => Convert.ToInt32(rawVal, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                RegistryValueKind.QWord => Convert.ToInt64(rawVal, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                _ => rawVal.ToString() ?? string.Empty,
            };

            values.Add(new RegistryValueEntryModel
            {
                Name = name,
                Kind = kind.ToString(),
                EncodedValue = encoded,
            });
        }

        var subKeys = new Dictionary<string, RegistryNodeModel>(StringComparer.OrdinalIgnoreCase);
        foreach (string childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName, writable: false);
            if (child is not null)
            {
                subKeys[childName] = ExportRegistryTree(child);
            }
        }

        return new RegistryNodeModel
        {
            Values = values,
            SubKeys = subKeys,
        };
    }

    private static void ImportRegistryTree(RegistryKey targetKey, RegistryNodeModel node)
    {
        foreach (var val in node.Values)
        {
            if (!Enum.TryParse<RegistryValueKind>(val.Kind, ignoreCase: true, out var kind))
            {
                kind = RegistryValueKind.String;
            }

            object valueToWrite = kind switch
            {
                RegistryValueKind.Binary => Convert.FromBase64String(val.EncodedValue),
                RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(val.EncodedValue) ?? [],
                RegistryValueKind.DWord => int.TryParse(val.EncodedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dw) ? dw : 0,
                RegistryValueKind.QWord => long.TryParse(val.EncodedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long qw) ? qw : 0L,
                _ => val.EncodedValue,
            };

            targetKey.SetValue(val.Name, valueToWrite, kind);
        }

        foreach (var kvp in node.SubKeys)
        {
            using var child = targetKey.CreateSubKey(kvp.Key, writable: true);
            if (child is not null)
            {
                ImportRegistryTree(child, kvp.Value);
            }
        }
    }

    private static async Task WriteManifestAtomicAsync(string manifestPath, UndoManifestModel manifest, CancellationToken cancellationToken)
    {
        string tmp = manifestPath + ".tmp";
        string json = JsonSerializer.Serialize(manifest, ManifestJsonOptions);
        await File.WriteAllTextAsync(tmp, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, manifestPath, overwrite: true);
    }

    /// <summary>
    /// Records a freshly backed-up item in the pending journal. Must succeed before the original is removed, otherwise
    /// the exception aborts this item and the original stays untouched.
    /// </summary>
    private static async Task AppendJournalItemAsync(
        string manifestPath,
        UndoManifestModel manifest,
        UndoManifestItem item,
        long totalBytesSoFar,
        CancellationToken cancellationToken)
    {
        item.State = UndoManifestItemState.BackedUp;
        manifest.Items.Add(item);
        manifest.TotalBytes = totalBytesSoFar + item.SizeBytes;
        try
        {
            await WriteManifestAtomicAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            manifest.Items.Remove(item);
            throw;
        }
    }

    private async Task MarkJournalItemRemovedAsync(string manifestPath, UndoManifestModel? manifest, UndoManifestItem? item, long totalBytes)
    {
        if (manifest is null || item is null)
        {
            return;
        }

        item.State = UndoManifestItemState.Removed;
        manifest.TotalBytes = totalBytes;
        try
        {
            // Original already removed: never cancel this write half-way.
            await WriteManifestAtomicAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The item is already journaled as BackedUp, which is still fully restorable.
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to mark undo journal item as removed: {Target}.", item.OriginalPathOrKey);
            }
        }
    }

    private async Task<(UndoManifestModel Manifest, string SnapshotDirectory)?> LoadLatestUndoManifestAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        string gameTrashRoot = _paths.GetGameTrashDirectory(gameId);
        if (!Directory.Exists(gameTrashRoot))
        {
            return null;
        }

        (UndoManifestModel Manifest, string Dir)? newest = null;

        foreach (string subDir in Directory.EnumerateDirectories(gameTrashRoot))
        {
            string manifestPath = Path.Combine(subDir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                string json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<UndoManifestModel>(json, ManifestJsonOptions);
                if (parsed is not null
                    && (newest is null || parsed.CreatedUtc >= newest.Value.Manifest.CreatedUtc))
                {
                    newest = (parsed, subDir);
                }
            }
            catch
            {
                // Skip corrupted trash manifest.
            }
        }

        return newest;
    }

    private static List<PcGamingWikiRawRule> DeserializeCachedRules(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PcGamingWikiRawRule>>(rulesJson, ManifestJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static GameDataUndoSnapshotDto ToUndoDto(UndoManifestModel manifest, string snapshotDir) =>
        new()
        {
            SnapshotId = manifest.SnapshotId,
            GameId = manifest.GameId,
            BackupDirectory = snapshotDir,
            CreatedUtc = manifest.CreatedUtc,
            BackedUpEntryCount = manifest.Items.Count,
            TotalBytes = manifest.TotalBytes,
            IncludedSpawnStateReset = manifest.IncludedSpawnStateReset,
        };

    private static (bool IsSafe, string? Reason) MapRegValidation((bool IsSafe, string? SubKey, string? Reason) reg) =>
        (reg.IsSafe, reg.Reason);

    // -------------------------------------------------------------------------
    // Undo Snapshot Serialization Models
    // -------------------------------------------------------------------------

    private static class UndoManifestStatus
    {
        public const string Pending = "pending";
        public const string Completed = "completed";
    }

    private static class UndoManifestItemState
    {
        public const string BackedUp = "backed-up";
        public const string Removed = "removed";
    }

    private sealed class UndoManifestModel
    {
        public required string SnapshotId { get; init; }
        public required string GameId { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }

        /// <summary><c>pending</c> while cleanup is in progress (or was interrupted); <c>completed</c> afterwards.</summary>
        public string Status { get; set; } = UndoManifestStatus.Completed;
        public long TotalBytes { get; set; }
        public bool IncludedSpawnStateReset { get; set; }
        public List<UndoManifestItem> Items { get; init; } = [];
        public SpawnLedgerBackupModel? SpawnLedgerBackup { get; init; }
    }

    private sealed class UndoManifestItem
    {
        public required string EntryId { get; init; }
        public GameDataCategory Category { get; init; }
        public required string TargetKind { get; init; }
        public required string OriginalPathOrKey { get; init; }
        public string? RegistrySubKey { get; init; }
        public required string BackupRelPath { get; init; }
        public long SizeBytes { get; init; }

        /// <summary><c>backed-up</c> once the backup copy is durable; <c>removed</c> after the original was deleted.</summary>
        public string State { get; set; } = UndoManifestItemState.Removed;
    }

    private sealed class SpawnLedgerBackupModel
    {
        public long TotalActiveSec { get; init; }
        public long TotalBackgroundSec { get; init; }
        public long TotalAfkSec { get; init; }
        public DateTimeOffset? LastPlayedUtc { get; init; }
        public List<PlaySessionRecord> PlaySessions { get; init; } = [];
        public List<AchievementUnlockDto> AchievementUnlocks { get; init; } = [];
    }

    private sealed class RegistryNodeModel
    {
        public List<RegistryValueEntryModel> Values { get; init; } = [];
        public Dictionary<string, RegistryNodeModel> SubKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RegistryValueEntryModel
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = "String";
        public string EncodedValue { get; init; } = string.Empty;
    }
}
