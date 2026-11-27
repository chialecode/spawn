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
        _options = options ?? new GameDataManagementOptions();
        _httpClient = _options.HttpMessageHandlerOverride is not null
            ? PcGamingWikiRuleParser.CreateHttpClient(_options.HttpMessageHandlerOverride, disposeHandler: false)
            : (httpClient ?? GameArtworkExtractor.CreateDefaultHttpClient());
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

        var identity = await BuildTargetIdentityAsync(game, cancellationToken).ConfigureAwait(false);
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
        AddUnrealEngineLocations(game, identity, builder);

        // 3. Publisher-customized Documents / LocalAppData & Social Club locations (e.g., Rockstar Games, My Games).
        AddPublisherAndSocialClubLocations(game, identity, builder);

        // 4. Steam local userdata\<uid>\<AppId>\remote offline save discovery.
        AddSteamUserdataLocations(game, builder);

        // 5. Offline Steam Emulators (CODEX, RUNE, Goldberg, gbe_fork, TENOKE, Online-Fix, ALI213, 3DM, SSE, EMPRESS).
        AddSteamEmulatorLocations(game, builder);

        // 6. Native / DRM-free game save & config rules (e.g., LIMBO GOG 1724299977).
        AddNativeAndPortableLocations(game, builder);

        return builder.BuildSorted();
    }

    public async Task<IReadOnlyList<GameDataEntryDto>> EnumerateSaveLocationsAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default)
    {
        var all = await InspectGameDataAsync(gameId, refreshOnlineRules, cancellationToken).ConfigureAwait(false);
        var safeExisting = all
            .Where(e => e.Exists
                        && e.IsSafeToClean
                        && (e.Category == GameDataCategory.SaveData || e.Category == GameDataCategory.RegistryData))
            .ToList();

        return DeduplicateNestedFilesystemTargets(safeExisting);
    }

    public async Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(
        string gameId,
        string pathOrRegistryKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(pathOrRegistryKey))
        {
            return new GameDataSafetyCheckResult(false, "游戏标识或恢复目标路径为空。");
        }

        var game = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (game is null)
        {
            return new GameDataSafetyCheckResult(false, "未在游戏库中找到该游戏记录。");
        }

        string trimmed = pathOrRegistryKey.Trim();
        if (trimmed.Contains('\0', StringComparison.Ordinal)
            || trimmed.Contains('*', StringComparison.Ordinal)
            || trimmed.Contains('?', StringComparison.Ordinal))
        {
            return new GameDataSafetyCheckResult(false, $"安全围栏拦截：恢复目标包含非法字符或通配符：{pathOrRegistryKey}");
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return new GameDataSafetyCheckResult(false, $"安全围栏拦截：禁止向设备路径或 UNC 共享路径恢复存档：{pathOrRegistryKey}");
        }

        string[] rawSegments = trimmed.Split(['\\', '/'], StringSplitOptions.None);
        if (rawSegments.Any(static s => s is ".." or "."))
        {
            return new GameDataSafetyCheckResult(false, $"安全围栏已拦截含相对跳转段的目标路径：{pathOrRegistryKey}");
        }

        var identity = await BuildTargetIdentityAsync(game, cancellationToken).ConfigureAwait(false);

        if (LooksLikeRegistryKeyPath(trimmed))
        {
            var (safe, subKey, reason) = GameDataSafetyFence.ValidateRegistryTarget(trimmed, _options);
            if (!safe || string.IsNullOrWhiteSpace(subKey))
            {
                return new GameDataSafetyCheckResult(false, reason ?? $"安全围栏拦截了不安全的注册表恢复目标：{pathOrRegistryKey}");
            }

            string canonicalReg = @"HKCU\" + subKey;
            string prefix = (string.IsNullOrWhiteSpace(_options.HkcuSoftwarePrefix)
                ? "Software"
                : _options.HkcuSoftwarePrefix.Trim('\\')) + @"\";
            string remainder = subKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? subKey[prefix.Length..]
                : subKey;

            string[] regSegments = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (regSegments.Any(seg => GameDataSafetyFence.IsGameSpecificSegment(seg, identity)))
            {
                return new GameDataSafetyCheckResult(true);
            }

            var discovered = await InspectGameDataAsync(gameId, refreshOnlineRules: false, cancellationToken).ConfigureAwait(false);
            foreach (var loc in discovered)
            {
                if (!loc.IsSafeToClean || !loc.IsRegistryEntry || string.IsNullOrWhiteSpace(loc.PathOrRegistryKey))
                {
                    continue;
                }

                var (locSafe, locSubKey, _) = GameDataSafetyFence.ValidateRegistryTarget(loc.PathOrRegistryKey, _options);
                if (!locSafe || string.IsNullOrWhiteSpace(locSubKey))
                {
                    continue;
                }

                string canonicalLoc = @"HKCU\" + locSubKey;
                if (string.Equals(canonicalReg, canonicalLoc, StringComparison.OrdinalIgnoreCase)
                    || canonicalReg.StartsWith(canonicalLoc + @"\", StringComparison.OrdinalIgnoreCase))
                {
                    return new GameDataSafetyCheckResult(true);
                }
            }

            return new GameDataSafetyCheckResult(false, $"安全围栏拦截：注册表恢复目标不在当前游戏存档范围内：{pathOrRegistryKey}");
        }

        if (!Path.IsPathFullyQualified(trimmed))
        {
            return new GameDataSafetyCheckResult(false, $"恢复目标必须为绝对路径：{pathOrRegistryKey}");
        }

        string normalizedTarget;
        try
        {
            normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new GameDataSafetyCheckResult(false, $"非法的恢复目标路径：{pathOrRegistryKey}");
        }

        var (fsSafe, fsReason) = GameDataSafetyFence.ValidateFilesystemTarget(
            normalizedTarget,
            game.InstallDir,
            identity,
            _paths,
            _options);
        if (!fsSafe)
        {
            return new GameDataSafetyCheckResult(false, fsReason ?? $"安全围栏拦截了不安全的文件恢复目标：{pathOrRegistryKey}");
        }

        if (ContainsDisallowedReparsePointOrSubtree(normalizedTarget, game.InstallDir, identity, _options))
        {
            return new GameDataSafetyCheckResult(false, $"安全围栏拦截：禁止向符号链接或目录联接点恢复存档：{pathOrRegistryKey}");
        }

        return new GameDataSafetyCheckResult(true);
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

        var identity = await BuildTargetIdentityAsync(game, cancellationToken).ConfigureAwait(false);
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

                if (!isReg && ContainsDisallowedReparsePointOrSubtree(id, game.InstallDir, identity, _options))
                {
                    return GameDataCleanResult.Fail($"安全围栏拦截：目标包含符号链接或 NTFS 目录联接点 (ReparsePoint)，禁止清理：{id}");
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

                if (ContainsDisallowedReparsePointOrSubtree(entry.PathOrRegistryKey, game.InstallDir, identity, _options))
                {
                    warnings.Add($"安全围栏拦截：目标路径或其子目录包含符号链接/NTFS 目录联接点 (ReparsePoint)，已拒绝递归删除：{entry.PathOrRegistryKey}");
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
                    if (ContainsDisallowedReparsePointOrSubtree(entry.PathOrRegistryKey, game.InstallDir, identity, _options))
                    {
                        warnings.Add($"安全围栏拦截：清理执行前检测到目标或其子目录含符号链接/NTFS 目录联接点 (ReparsePoint)，已跳过：{entry.PathOrRegistryKey}");
                        continue;
                    }

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

                    // Re-check immediately prior to deletion in case a junction was mounted concurrently.
                    if (ContainsDisallowedReparsePointOrSubtree(entry.PathOrRegistryKey, game.InstallDir, identity, _options))
                    {
                        warnings.Add($"安全围栏拦截：删除前检测到目录包含 NTFS 目录联接点 (ReparsePoint)，已拒绝递归删除：{entry.PathOrRegistryKey}");
                        continue;
                    }

                    Directory.Delete(entry.PathOrRegistryKey, recursive: true);
                    TrackPotentialEmptyCleanupParent(entry, game, identity, emptyEmulatorParentDirsToPrune);
                    affectedCount++;
                    totalBytesProcessed += dirBytes;
                    await MarkJournalItemRemovedAsync(manifestPath, manifest, journalItem, totalBytesProcessed).ConfigureAwait(false);
                }
                else if (File.Exists(entry.PathOrRegistryKey))
                {
                    if (ContainsDisallowedReparsePointOrSubtree(entry.PathOrRegistryKey, game.InstallDir, identity, _options))
                    {
                        warnings.Add($"安全围栏拦截：目标文件或祖先路径含符号链接/联接点 (ReparsePoint)，已跳过：{entry.PathOrRegistryKey}");
                        continue;
                    }

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
                    TrackPotentialEmptyCleanupParent(entry, game, identity, emptyEmulatorParentDirsToPrune);
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

        // Clean up empty per-game container directories (deepest first, e.g. ...\Profiles -> ...\GTA III Definitive Edition, or ...\Steam\CODEX\1703340).
        foreach (string parentDir in emptyEmulatorParentDirsToPrune.OrderByDescending(static p => p.Length))
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

    public async Task<IReadOnlyList<GameDataUndoSnapshotDto>> ListUndoSnapshotsAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        var all = await LoadAllUndoManifestsAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return [];
        }

        var dtos = new List<GameDataUndoSnapshotDto>(all.Count);
        foreach (var (manifest, snapshotDir) in all)
        {
            dtos.Add(ToUndoDto(manifest, snapshotDir));
        }

        return dtos;
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

        return await RestoreSnapshotCoreAsync(
            game,
            latest.Value.Manifest,
            latest.Value.SnapshotDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameDataCleanResult> RestoreUndoSnapshotAsync(
        string gameId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return GameDataCleanResult.Fail("游戏标识不能为空。");
        }

        if (string.IsNullOrWhiteSpace(snapshotId) || !IsSafeSnapshotIdentifier(snapshotId))
        {
            return GameDataCleanResult.Fail("回收站快照标识无效。");
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

        var all = await LoadAllUndoManifestsAsync(gameId, cancellationToken).ConfigureAwait(false);
        var match = all.FirstOrDefault(x => string.Equals(x.Manifest.SnapshotId, snapshotId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.Manifest is null || string.IsNullOrWhiteSpace(match.SnapshotDirectory))
        {
            return GameDataCleanResult.Fail($"未找到指定的清理回收站快照（{snapshotId}）。");
        }

        return await RestoreSnapshotCoreAsync(
            game,
            match.Manifest,
            match.SnapshotDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<GameDataTrashPurgeResult> PurgeTrashSnapshotsAsync(
        string gameId,
        string? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return GameDataTrashPurgeResult.Fail("游戏标识不能为空。");
        }

        bool purgeSingle = !string.IsNullOrWhiteSpace(snapshotId);
        if (purgeSingle && !IsSafeSnapshotIdentifier(snapshotId!))
        {
            return GameDataTrashPurgeResult.Fail("回收站快照标识无效。");
        }

        string gameTrashRoot = _paths.GetGameTrashDirectory(gameId);
        if (!Directory.Exists(gameTrashRoot))
        {
            return GameDataTrashPurgeResult.Ok("该游戏当前没有可清理的回收站快照。", deletedSnapshotCount: 0, freedBytes: 0);
        }

        string trashRootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.TrashDir)) + Path.DirectorySeparatorChar;
        string normalizedGameTrashRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameTrashRoot));
        if (!normalizedGameTrashRoot.StartsWith(trashRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return GameDataTrashPurgeResult.Fail("安全围栏拦截：回收站目录越界。");
        }

        if ((new DirectoryInfo(normalizedGameTrashRoot).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return GameDataTrashPurgeResult.Fail("安全围栏拦截：禁止操作重解析点回收站目录。");
        }

        string gameTrashRootPrefix = normalizedGameTrashRoot + Path.DirectorySeparatorChar;
        var targetsToDelete = new List<string>();

        if (purgeSingle)
        {
            var all = await LoadAllUndoManifestsAsync(gameId, cancellationToken).ConfigureAwait(false);
            var match = all.FirstOrDefault(x => string.Equals(x.Manifest.SnapshotId, snapshotId!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match.Manifest is not null && !string.IsNullOrWhiteSpace(match.SnapshotDirectory))
            {
                targetsToDelete.Add(match.SnapshotDirectory);
            }
            else
            {
                string directDir = Path.Combine(gameTrashRoot, snapshotId!.Trim());
                if (Directory.Exists(directDir))
                {
                    targetsToDelete.Add(directDir);
                }
            }

            if (targetsToDelete.Count == 0)
            {
                return GameDataTrashPurgeResult.Fail($"未找到指定的清理回收站快照（{snapshotId}）。");
            }
        }
        else
        {
            foreach (string subDir in Directory.EnumerateDirectories(gameTrashRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                targetsToDelete.Add(subDir);
            }

            if (targetsToDelete.Count == 0)
            {
                TryDeleteDirectoryIfCompletelyEmpty(gameTrashRoot);
                return GameDataTrashPurgeResult.Ok("该游戏当前没有可清理的回收站快照。", deletedSnapshotCount: 0, freedBytes: 0);
            }
        }

        int deletedCount = 0;
        long freedBytes = 0;
        var warnings = new List<string>();

        foreach (string candidateDir in targetsToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullCandidate;
            try
            {
                fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDir));
            }
            catch
            {
                continue;
            }

            if (!fullCandidate.StartsWith(gameTrashRootPrefix, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(fullCandidate))
            {
                continue;
            }

            var dirInfo = new DirectoryInfo(fullCandidate);
            if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"跳过重解析点目录：{fullCandidate}");
                continue;
            }

            var (_, bytes, _) = InspectFilesystemStats(fullCandidate);
            try
            {
                Directory.Delete(fullCandidate, recursive: true);
                deletedCount++;
                freedBytes += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"删除快照目录失败 ({Path.GetFileName(fullCandidate)})：{ex.Message}");
            }
        }

        TryDeleteDirectoryIfCompletelyEmpty(gameTrashRoot);

        if (deletedCount == 0 && warnings.Count > 0)
        {
            return GameDataTrashPurgeResult.Fail(warnings[0], warnings);
        }

        string msg = purgeSingle
            ? $"已从回收站永久删除 1 份快照，释放 {freedBytes} 字节。"
            : $"已清空该游戏 {deletedCount} 份回收站备份快照，释放 {freedBytes} 字节。";

        return GameDataTrashPurgeResult.Ok(msg, deletedCount, freedBytes, warnings);
    }

    private async Task<GameDataCleanResult> RestoreSnapshotCoreAsync(
        GameRecord game,
        UndoManifestModel manifest,
        string snapshotDir,
        CancellationToken cancellationToken)
    {
        var restoreIdentity = await BuildTargetIdentityAsync(game, cancellationToken).ConfigureAwait(false);
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

                            if (ContainsDisallowedReparsePointOrSubtree(item.OriginalPathOrKey, game.InstallDir, restoreIdentity, _options))
                            {
                                warnings.Add($"安全围栏拦截：恢复目标或其子目录含符号链接/NTFS 目录联接点 (ReparsePoint)，已跳过：{item.OriginalPathOrKey}");
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

                            if (ContainsDisallowedReparsePointOrSubtree(item.OriginalPathOrKey, game.InstallDir, restoreIdentity, _options))
                            {
                                warnings.Add($"安全围栏拦截：恢复目标文件或祖先目录含符号链接/联接点 (ReparsePoint)，已跳过：{item.OriginalPathOrKey}");
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
            var titleMap = await _database.GetTrainerTitleMapAsync(game.GameId, cancellationToken).ConfigureAwait(false);
            string primaryLookupTitle = !string.IsNullOrWhiteSpace(titleMap?.EnglishTitle)
                ? titleMap.EnglishTitle
                : game.Title;

            var fetched = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
                _httpClient,
                game.SteamAppId,
                game.GogGameId,
                primaryLookupTitle,
                _options.OnlineLookupTimeout,
                cancellationToken).ConfigureAwait(false);

            if (fetched is null
                && !string.IsNullOrWhiteSpace(game.Title)
                && !string.Equals(primaryLookupTitle, game.Title, StringComparison.OrdinalIgnoreCase))
            {
                fetched = await PcGamingWikiRuleParser.FetchOnlineRulesAsync(
                    _httpClient,
                    game.SteamAppId,
                    game.GogGameId,
                    game.Title,
                    _options.OnlineLookupTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

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

    private async Task<GameDataTargetIdentity> BuildTargetIdentityAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        var extras = new List<string?>();
        var titleMap = await _database.GetTrainerTitleMapAsync(game.GameId, cancellationToken).ConfigureAwait(false);
        string? mappedEnglishTitle = !string.IsNullOrWhiteSpace(titleMap?.EnglishTitle)
            ? titleMap.EnglishTitle.Trim()
            : null;

        var unity = GameEngineInspector.TryReadUnityAppInfo(game.InstallDir);
        if (unity is not null && GameEngineInspector.IsUsefulUnityProductName(unity.ProductName))
        {
            extras.Add(unity.ProductName);
        }

        string? ueProject = GameEngineInspector.TryDetectUnrealProjectName(game.InstallDir);
        if (GameEngineInspector.IsUsefulUnrealProjectName(ueProject))
        {
            extras.Add(ueProject);
        }

        if (!string.IsNullOrWhiteSpace(game.ExecutableRelPath) && !string.IsNullOrWhiteSpace(game.InstallDir))
        {
            string exeFullPath = Path.Combine(game.InstallDir, game.ExecutableRelPath);
            string? peTitle = GameTitleCleaner.TryGetCleanPeProductName(exeFullPath);
            if (!string.IsNullOrWhiteSpace(peTitle))
            {
                extras.Add(peTitle);
            }
        }

        if (!string.IsNullOrWhiteSpace(game.Title))
        {
            string cleanedPreserved = GameTitleCleaner.CleanTitle(game.Title, preserveRemasterEdition: true);
            if (!string.Equals(cleanedPreserved, game.Title, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(cleanedPreserved, "Unknown Game", StringComparison.OrdinalIgnoreCase))
            {
                extras.Add(cleanedPreserved);
            }
        }

        var identity = GameDataTargetIdentity.FromGame(game, extras, englishTitle: mappedEnglishTitle);
        if (identity.SteamAppId is not > 0)
        {
            var (_, inspectedAppId, _, _) = GameEngineInspector.InspectSteamFiles(game.InstallDir);
            if (inspectedAppId is > 0)
            {
                identity = identity with { SteamAppId = inspectedAppId };
            }
        }

        return identity;
    }

    private static bool LooksLikeRegistryKeyPath(string candidate) =>
        candidate.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKCU/", StringComparison.OrdinalIgnoreCase)
        || candidate.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKLM/", StringComparison.OrdinalIgnoreCase)
        || candidate.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(@"HKCR\", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKCR/", StringComparison.OrdinalIgnoreCase)
        || candidate.Equals("HKCR", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKU/", StringComparison.OrdinalIgnoreCase)
        || candidate.Equals("HKU", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(@"HKCC\", StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith("HKCC/", StringComparison.OrdinalIgnoreCase)
        || candidate.Equals("HKCC", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsDisallowedReparsePoint(
        string normalizedTarget,
        string gameInstallDir,
        GameDataTargetIdentity identity,
        GameDataManagementOptions options)
    {
        try
        {
            if ((File.Exists(normalizedTarget) || Directory.Exists(normalizedTarget))
                && (File.GetAttributes(normalizedTarget) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            string fullInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameInstallDir));
            if (normalizedTarget.StartsWith(fullInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string? current = Path.GetDirectoryName(normalizedTarget);
                while (!string.IsNullOrWhiteSpace(current)
                       && current.Length > fullInstallDir.Length
                       && current.StartsWith(fullInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(current)
                        && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            else
            {
                string? current = Path.GetDirectoryName(normalizedTarget);
                while (!string.IsNullOrWhiteSpace(current)
                       && GameDataSafetyFence.IsGameSpecificPathOutsideInstall(current, identity, options))
                {
                    if (Directory.Exists(current)
                        && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }

        return false;
    }

    private static bool ContainsDisallowedReparsePointOrSubtree(
        string candidateTarget,
        string gameInstallDir,
        GameDataTargetIdentity identity,
        GameDataManagementOptions options)
    {
        string normalizedTarget;
        try
        {
            normalizedTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateTarget));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        if (ContainsDisallowedReparsePoint(normalizedTarget, gameInstallDir, identity, options))
        {
            return true;
        }

        if (!Directory.Exists(normalizedTarget))
        {
            return false;
        }

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
        };

        bool WalkSubtreeForReparsePoint(DirectoryInfo dir, int depth)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (depth > 24)
            {
                return true;
            }

            foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos("*", enumOpts))
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (child is DirectoryInfo childDir && WalkSubtreeForReparsePoint(childDir, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        try
        {
            return WalkSubtreeForReparsePoint(new DirectoryInfo(normalizedTarget), depth: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
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

    private void AddUnrealEngineLocations(GameRecord game, GameDataTargetIdentity identity, CandidateListBuilder builder)
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

        // Generic internal UE codenames (e.g., Gameface, UE4Game, UE5Game) are shared across unrelated titles and often
        // redirected to publisher folders (such as %LOCALAPPDATA%\Rockstar Games\<Game>\Saved). Never emit phantom
        // %LOCALAPPDATA%\Gameface\Saved entries; instead probe existing game-specific %LOCALAPPDATA%\<Variant>\Saved roots.
        if (GameEngineInspector.IsGenericUnrealProjectName(ueProject))
        {
            foreach (string gameDir in FindMatchingChildDirectories(localAppData, identity, BuildDeterministicFolderVariants(game, identity)))
            {
                string customSavedRoot = Path.Combine(gameDir, "Saved");
                if (Directory.Exists(customSavedRoot) && !IsDirectoryReparsePoint(customSavedRoot))
                {
                    AddUnrealSavedSubtree(customSavedRoot, "Unreal 引擎", requireSaveGamesExists: true, builder);
                }
            }

            return;
        }

        string savedRoot = Path.Combine(localAppData, ueProject, "Saved");
        if (Directory.Exists(savedRoot) && IsDirectoryReparsePoint(savedRoot))
        {
            return;
        }

        AddUnrealSavedSubtree(savedRoot, "Unreal 引擎", requireSaveGamesExists: false, builder);
    }

    private static void AddUnrealSavedSubtree(
        string savedRoot,
        string sourceLabel,
        bool requireSaveGamesExists,
        CandidateListBuilder builder)
    {
        string saveGamesDir = Path.Combine(savedRoot, "SaveGames");
        bool saveGamesExists = Directory.Exists(saveGamesDir);
        bool saveGamesIsReparse = saveGamesExists && IsDirectoryReparsePoint(saveGamesDir);
        if (!saveGamesIsReparse && (!requireSaveGamesExists || saveGamesExists))
        {
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.UnrealEngine,
                sourceLabel,
                saveGamesDir,
                "Unreal Engine 本地存档目录 (SaveGames)",
                defaultSelected: true);
        }

        string configDir = Path.Combine(savedRoot, "Config");
        if (!(Directory.Exists(configDir) && IsDirectoryReparsePoint(configDir)))
        {
            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                GameDataSourceRule.UnrealEngine,
                sourceLabel,
                configDir,
                "Unreal Engine 画质与键位配置 (Saved\\Config)",
                defaultSelected: false);
        }

        string logsDir = Path.Combine(savedRoot, "Logs");
        if (Directory.Exists(logsDir) && !IsDirectoryReparsePoint(logsDir))
        {
            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                GameDataSourceRule.UnrealEngine,
                sourceLabel,
                logsDir,
                "Unreal Engine 运行日志",
                defaultSelected: false);
        }
    }

    private void AddPublisherAndSocialClubLocations(
        GameRecord game,
        GameDataTargetIdentity identity,
        CandidateListBuilder builder)
    {
        string userDocs = _options.ResolveUserDocuments();
        string localAppData = _options.ResolveLocalAppData();
        string roaming = _options.ResolveAppDataRoaming();
        string? ueProject = GameEngineInspector.TryDetectUnrealProjectName(game.InstallDir);
        bool hasUnreal = !string.IsNullOrWhiteSpace(ueProject);

        var deterministicVariants = BuildDeterministicFolderVariants(game, identity);
        bool hasRockstarFiles = GameEngineInspector.HasRockstarOrSocialClubFiles(game.InstallDir);
        bool isKnownRockstarFranchise = IsKnownRockstarFranchise(identity);
        bool isRockstarGame = hasRockstarFiles || isKnownRockstarFranchise;
        string? preferredRockstarVariant = isRockstarGame ? SelectPreferredRockstarVariant(deterministicVariants) : null;

        // 1. %USERPROFILE%\Documents\Rockstar Games\<Variant> (resolved via Environment.GetFolderPath / options override)
        if (!string.IsNullOrWhiteSpace(userDocs))
        {
            string rockstarDocsRoot = Path.Combine(userDocs, "Rockstar Games");
            var matchedDocDirs = FindMatchingChildDirectories(rockstarDocsRoot, identity, deterministicVariants);
            if (matchedDocDirs.Count == 0 && !string.IsNullOrWhiteSpace(preferredRockstarVariant))
            {
                matchedDocDirs.Add(Path.Combine(rockstarDocsRoot, preferredRockstarVariant));
            }

            foreach (string docGameDir in matchedDocDirs)
            {
                AddRockstarDocumentsVariantLocations(docGameDir, hasUnreal, builder);
            }

            // Also probe Documents\My Games\<Variant> when present on disk (common UE/PC publisher save container).
            string myGamesRoot = Path.Combine(userDocs, "My Games");
            foreach (string myGameDir in FindMatchingChildDirectories(myGamesRoot, identity, deterministicVariants))
            {
                AddMyGamesVariantLocations(myGameDir, builder);
            }
        }

        // 2. %LOCALAPPDATA%\Rockstar Games\<Variant>\Saved (and non-UE %LOCALAPPDATA%\Rockstar Games\<Variant>\savegames)
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            string rockstarLocalRoot = Path.Combine(localAppData, "Rockstar Games");
            var matchedLocalDirs = FindMatchingChildDirectories(rockstarLocalRoot, identity, deterministicVariants);
            if (matchedLocalDirs.Count == 0 && hasUnreal && !string.IsNullOrWhiteSpace(preferredRockstarVariant))
            {
                matchedLocalDirs.Add(Path.Combine(rockstarLocalRoot, preferredRockstarVariant));
            }

            foreach (string localGameDir in matchedLocalDirs)
            {
                if (Directory.Exists(localGameDir) && IsDirectoryReparsePoint(localGameDir))
                {
                    continue;
                }

                string savedDir = Path.Combine(localGameDir, "Saved");
                bool savedExists = Directory.Exists(savedDir);
                bool savedIsReparse = savedExists && IsDirectoryReparsePoint(savedDir);
                if (!savedIsReparse && (savedExists || hasUnreal))
                {
                    AddUnrealSavedSubtree(savedDir, "Rockstar / Unreal", requireSaveGamesExists: true, builder);
                }

                foreach (string directSaveSub in new[] { "savegames", "SaveGames", "Profiles" })
                {
                    string candidateSaveDir = Path.Combine(localGameDir, directSaveSub);
                    if (Directory.Exists(candidateSaveDir) && !IsDirectoryReparsePoint(candidateSaveDir))
                    {
                        builder.TryAdd(
                            GameDataCategory.SaveData,
                            GameDataSourceRule.NativeRule,
                            "Rockstar 本地存档",
                            candidateSaveDir,
                            $"Rockstar 本地存档目录 ({directSaveSub})",
                            defaultSelected: true);
                    }
                }
            }
        }

        // 3. %APPDATA%\Goldberg SocialClub Emu Saves\<AppFolder>
        //    Game-specific folders pass the safety fence; shared multi-game trilogy folder GTA_DEF stays IsSafeToClean = false!
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            string socialClubEmuRoot = Path.Combine(roaming, "Goldberg SocialClub Emu Saves");
            if (Directory.Exists(socialClubEmuRoot) && !IsDirectoryReparsePoint(socialClubEmuRoot))
            {
                try
                {
                    foreach (string childDir in Directory.EnumerateDirectories(socialClubEmuRoot))
                    {
                        if (IsDirectoryReparsePoint(childDir))
                        {
                            continue;
                        }

                        string childName = Path.GetFileName(childDir);
                        if (GameDataSafetyFence.IsGameSpecificSegment(childName, identity))
                        {
                            builder.TryAdd(
                                GameDataCategory.SaveData,
                                GameDataSourceRule.SteamEmulator,
                                "Social Club Emu",
                                childDir,
                                "Goldberg Social Club 离线存档与成就目录",
                                defaultSelected: true);
                        }
                        else if (childName.Equals("GTA_DEF", StringComparison.OrdinalIgnoreCase)
                                 && IsGtaDefinitiveEditionIdentity(identity))
                        {
                            builder.TryAdd(
                                GameDataCategory.SaveData,
                                GameDataSourceRule.SteamEmulator,
                                "Social Club Emu",
                                childDir,
                                "Goldberg Social Club 三部曲共享目录 (GTA_DEF，跨游戏共享受保护)",
                                defaultSelected: false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Ignore inaccessible emulator directory.
                }
            }
        }
    }

    private static void AddRockstarDocumentsVariantLocations(
        string docGameDir,
        bool hasUnreal,
        CandidateListBuilder builder)
    {
        bool docDirRawExists = Directory.Exists(docGameDir);
        if (docDirRawExists && IsDirectoryReparsePoint(docGameDir))
        {
            return;
        }

        bool docDirExists = docDirRawExists;
        string profilesDir = Path.Combine(docGameDir, "Profiles");
        bool profilesExists = docDirExists && Directory.Exists(profilesDir);
        bool profilesIsReparse = profilesExists && IsDirectoryReparsePoint(profilesDir);
        bool addedProfileChildren = false;

        if (profilesExists && !profilesIsReparse)
        {
            try
            {
                bool hasDirectFiles = Directory.EnumerateFiles(profilesDir).Any();
                if (!hasDirectFiles)
                {
                    foreach (string uidDir in Directory.EnumerateDirectories(profilesDir))
                    {
                        if (IsDirectoryReparsePoint(uidDir))
                        {
                            continue;
                        }

                        addedProfileChildren = true;
                        string uidName = Path.GetFileName(uidDir);
                        builder.TryAdd(
                            GameDataCategory.SaveData,
                            GameDataSourceRule.NativeRule,
                            "Rockstar 文档存档",
                            uidDir,
                            $"Rockstar 用户档案存档目录 (Profiles\\{uidName})",
                            defaultSelected: true);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall back to Profiles directory itself below.
            }
        }

        if (!addedProfileChildren && !profilesIsReparse)
        {
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.NativeRule,
                "Rockstar 文档存档",
                profilesDir,
                "Rockstar 用户档案存档目录 (Profiles)",
                defaultSelected: true);
        }

        if (docDirExists)
        {
            foreach (string altSaveSub in new[] { "SaveGames", "savegames", "User Files" })
            {
                string altSaveDir = Path.Combine(docGameDir, altSaveSub);
                if (Directory.Exists(altSaveDir) && !IsDirectoryReparsePoint(altSaveDir))
                {
                    builder.TryAdd(
                        GameDataCategory.SaveData,
                        GameDataSourceRule.NativeRule,
                        "Rockstar 文档存档",
                        altSaveDir,
                        $"Rockstar 文档存档目录 ({altSaveSub})",
                        defaultSelected: true);
                }
            }
        }

        string configDir = Path.Combine(docGameDir, "Config");
        bool configExists = docDirExists && Directory.Exists(configDir);
        bool configIsReparse = configExists && IsDirectoryReparsePoint(configDir);
        string winNoEditorConfigDir = Path.Combine(configDir, "WindowsNoEditor");
        bool winNoEditorExists = configExists && !configIsReparse && Directory.Exists(winNoEditorConfigDir);
        bool winNoEditorIsReparse = winNoEditorExists && IsDirectoryReparsePoint(winNoEditorConfigDir);

        if (winNoEditorExists && !winNoEditorIsReparse)
        {
            bool hasDirectConfigFiles = false;
            try
            {
                hasDirectConfigFiles = Directory.EnumerateFiles(configDir).Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore enumeration error.
            }

            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                hasUnreal ? GameDataSourceRule.UnrealEngine : GameDataSourceRule.NativeRule,
                hasUnreal ? "Rockstar / Unreal" : "Rockstar 配置",
                hasDirectConfigFiles ? configDir : winNoEditorConfigDir,
                "Rockstar 画质与键位配置目录 (Config\\WindowsNoEditor)",
                defaultSelected: false);
        }
        else if (configExists && !configIsReparse && !winNoEditorIsReparse)
        {
            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                hasUnreal ? GameDataSourceRule.UnrealEngine : GameDataSourceRule.NativeRule,
                hasUnreal ? "Rockstar / Unreal" : "Rockstar 配置",
                configDir,
                "Rockstar 画质与键位配置目录 (Config)",
                defaultSelected: false);
        }
        else if (!docDirExists && hasUnreal)
        {
            builder.TryAdd(
                GameDataCategory.ConfigAndLogs,
                GameDataSourceRule.UnrealEngine,
                "Rockstar / Unreal",
                winNoEditorConfigDir,
                "Rockstar 画质与键位配置目录 (Config\\WindowsNoEditor)",
                defaultSelected: false);
        }

        if (docDirExists)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(docGameDir))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".cfg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileName(file);
                        builder.TryAdd(
                            GameDataCategory.ConfigAndLogs,
                            GameDataSourceRule.NativeRule,
                            "Rockstar 配置",
                            file,
                            $"Rockstar 图形与显示配置文件 ({fileName})",
                            defaultSelected: false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore unreadable root files.
            }

            string logsDir = Path.Combine(docGameDir, "Logs");
            if (Directory.Exists(logsDir) && !IsDirectoryReparsePoint(logsDir))
            {
                builder.TryAdd(
                    GameDataCategory.ConfigAndLogs,
                    GameDataSourceRule.NativeRule,
                    "Rockstar 日志",
                    logsDir,
                    "Rockstar 游戏与 NGX 运行日志 (Logs)",
                    defaultSelected: false);
            }
        }
    }

    private static void AddMyGamesVariantLocations(string myGameDir, CandidateListBuilder builder)
    {
        string ueSavedDir = Path.Combine(myGameDir, "Saved");
        if (Directory.Exists(ueSavedDir) && !IsDirectoryReparsePoint(ueSavedDir))
        {
            AddUnrealSavedSubtree(ueSavedDir, "My Games / Unreal", requireSaveGamesExists: true, builder);
            return;
        }

        bool addedSubSave = false;
        foreach (string sub in new[] { "Saves", "SaveGames", "savegames", "Profiles" })
        {
            string candidate = Path.Combine(myGameDir, sub);
            if (Directory.Exists(candidate) && !IsDirectoryReparsePoint(candidate))
            {
                addedSubSave = true;
                builder.TryAdd(
                    GameDataCategory.SaveData,
                    GameDataSourceRule.NativeRule,
                    "My Games 存档",
                    candidate,
                    $"文档 My Games 存档目录 ({sub})",
                    defaultSelected: true);
            }
        }

        if (!addedSubSave)
        {
            builder.TryAdd(
                GameDataCategory.SaveData,
                GameDataSourceRule.NativeRule,
                "My Games 存档",
                myGameDir,
                "文档 My Games 游戏存档目录",
                defaultSelected: true);
        }
    }

    private static List<string> FindMatchingChildDirectories(
        string containerRoot,
        GameDataTargetIdentity identity,
        List<string> deterministicVariants)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(containerRoot) || !Directory.Exists(containerRoot) || IsDirectoryReparsePoint(containerRoot))
        {
            return results;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First probe deterministic variants in priority order.
        foreach (string variant in deterministicVariants)
        {
            string candidate = Path.Combine(containerRoot, variant);
            if (Directory.Exists(candidate)
                && !IsDirectoryReparsePoint(candidate)
                && GameDataSafetyFence.IsGameSpecificSegment(variant, identity)
                && seen.Add(candidate))
            {
                results.Add(candidate);
            }
        }

        // Next enumerate immediate subdirectories to catch any punctuation/casing variation verified by the safety fence.
        try
        {
            foreach (string childDir in Directory.EnumerateDirectories(containerRoot))
            {
                if (IsDirectoryReparsePoint(childDir))
                {
                    continue;
                }

                string leaf = Path.GetFileName(childDir);
                if (GameDataSafetyFence.IsGameSpecificSegment(leaf, identity) && seen.Add(childDir))
                {
                    results.Add(childDir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore inaccessible container directories.
        }

        return results;
    }

    private static List<string> BuildDeterministicFolderVariants(GameRecord game, GameDataTargetIdentity identity)
    {
        var variants = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfValid(string? rawCandidate)
        {
            if (string.IsNullOrWhiteSpace(rawCandidate))
            {
                return;
            }

            string sanitized = SanitizeFolderVariantSegment(rawCandidate);
            if (sanitized.Length >= 3
                && GameDataSafetyFence.IsGameSpecificSegment(sanitized, identity)
                && seen.Add(sanitized))
            {
                variants.Add(sanitized);
            }
        }

        var seeds = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(game.Title))
        {
            seeds.Add(GameTitleCleaner.CleanTitle(game.Title, preserveRemasterEdition: true));
        }

        string installLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(installLeaf))
        {
            string cleanedInstallLeaf = GameTitleCleaner.CleanTitle(installLeaf, preserveRemasterEdition: true);
            if (GameDataSafetyFence.CanAcceptInstallFolderForIdentity(installLeaf, cleanedInstallLeaf, game.Title))
            {
                seeds.Add(cleanedInstallLeaf);
            }
        }

        foreach (string prod in identity.ProductNames)
        {
            seeds.Add(GameTitleCleaner.CleanTitle(prod, preserveRemasterEdition: true));
        }

        foreach (string seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed) || string.Equals(seed, "Unknown Game", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string franchiseForm in ExpandAndAbbreviateFranchiseForms(seed))
            {
                string compactNoColon = CollapseWhitespace(
                    franchiseForm
                        .Replace(":", string.Empty, StringComparison.Ordinal)
                        .Replace("–", " ", StringComparison.Ordinal)
                        .Replace("—", " ", StringComparison.Ordinal)
                        .Replace(" - ", " ", StringComparison.Ordinal));

                string compactWithoutThe = CollapseWhitespace(
                    compactNoColon
                        .Replace(" The Definitive Edition", " Definitive Edition", StringComparison.OrdinalIgnoreCase)
                        .Replace(" The Anniversary Edition", " Anniversary Edition", StringComparison.OrdinalIgnoreCase));

                AddIfValid(compactWithoutThe);
                AddIfValid(compactNoColon);

                string dashed = CollapseWhitespace(
                    franchiseForm
                        .Replace(":", string.Empty, StringComparison.Ordinal)
                        .Replace("–", "-", StringComparison.Ordinal)
                        .Replace("—", "-", StringComparison.Ordinal));
                AddIfValid(dashed);
            }
        }

        return variants;
    }

    private static IEnumerable<string> ExpandAndAbbreviateFranchiseForms(string title)
    {
        string trimmed = title.Trim();
        string expanded = GameTitleCleaner.ExpandSeriesAbbreviations(trimmed);
        string abbreviated = AbbreviateKnownFranchisePrefix(expanded);

        // Emit abbreviated Rockstar-style form ("GTA ...") alongside full form ("Grand Theft Auto ...").
        if (!string.Equals(abbreviated, expanded, StringComparison.OrdinalIgnoreCase))
        {
            yield return abbreviated;
        }

        yield return expanded;

        if (!string.Equals(trimmed, expanded, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(trimmed, abbreviated, StringComparison.OrdinalIgnoreCase))
        {
            yield return trimmed;
        }
    }

    private static string AbbreviateKnownFranchisePrefix(string title)
    {
        const string GtaFullPrefix = "Grand Theft Auto";
        if (title.StartsWith(GtaFullPrefix, StringComparison.OrdinalIgnoreCase)
            && title.Length > GtaFullPrefix.Length
            && !char.IsLetterOrDigit(title[GtaFullPrefix.Length]))
        {
            return "GTA" + title[GtaFullPrefix.Length..];
        }

        const string RdrFullPrefix = "Red Dead Redemption";
        if (title.StartsWith(RdrFullPrefix, StringComparison.OrdinalIgnoreCase)
            && title.Length > RdrFullPrefix.Length
            && !char.IsLetterOrDigit(title[RdrFullPrefix.Length]))
        {
            return "RDR" + title[RdrFullPrefix.Length..];
        }

        return title;
    }

    private static string? SelectPreferredRockstarVariant(List<string> variants)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        // Rockstar's Documents\Rockstar Games folders prefer abbreviated "GTA <Installment/Subtitle> Definitive Edition" / "GTA V",
        // whereas Red Dead Redemption uses the full "Red Dead Redemption <Installment>" folder name.
        foreach (string v in variants)
        {
            if (v.StartsWith("GTA ", StringComparison.OrdinalIgnoreCase) && !v.Contains(" - ", StringComparison.Ordinal))
            {
                return v;
            }

            if (v.StartsWith("Red Dead Redemption", StringComparison.OrdinalIgnoreCase) && !v.Contains(" - ", StringComparison.Ordinal))
            {
                return v;
            }
        }

        return variants[0];
    }

    private static bool IsKnownRockstarFranchise(GameDataTargetIdentity identity)
    {
        foreach (string prod in identity.ProductNames)
        {
            string expanded = GameTitleCleaner.ExpandSeriesAbbreviations(prod);
            if (expanded.StartsWith("Grand Theft Auto", StringComparison.OrdinalIgnoreCase)
                || expanded.StartsWith("Red Dead Redemption", StringComparison.OrdinalIgnoreCase)
                || expanded.StartsWith("Max Payne 3", StringComparison.OrdinalIgnoreCase)
                || expanded.StartsWith("L.A. Noire", StringComparison.OrdinalIgnoreCase)
                || expanded.StartsWith("LA Noire", StringComparison.OrdinalIgnoreCase)
                || expanded.Equals("Bully: Scholarship Edition", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGtaDefinitiveEditionIdentity(GameDataTargetIdentity identity)
    {
        foreach (string prod in identity.ProductNames)
        {
            string expanded = GameTitleCleaner.ExpandSeriesAbbreviations(
                GameTitleCleaner.CleanTitle(prod, preserveRemasterEdition: true));
            if (expanded.StartsWith("Grand Theft Auto", StringComparison.OrdinalIgnoreCase)
                && expanded.Contains("Definitive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeFolderVariantSegment(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (Array.IndexOf(invalid, ch) < 0)
            {
                sb.Append(ch);
            }
        }

        return CollapseWhitespace(sb.ToString()).TrimEnd('.', ' ');
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool prevSpace = false;
        foreach (char ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }

        return sb.ToString();
    }

    private static bool IsDirectoryReparsePoint(string dirPath)
    {
        try
        {
            return (File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private void AddSteamUserdataLocations(GameRecord game, CandidateListBuilder builder)
    {
        var (_, inspectedAppId, _, _) = GameEngineInspector.InspectSteamFiles(game.InstallDir);
        int? appId = game.SteamAppId ?? inspectedAppId;
        if (appId is not > 0)
        {
            return;
        }

        string? steamRoot = _options.ResolveSteamInstallDir();
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return;
        }

        string userdataRoot = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdataRoot))
        {
            return;
        }

        try
        {
            if ((File.GetAttributes(userdataRoot) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            string appIdStr = appId.Value.ToString(CultureInfo.InvariantCulture);
            foreach (string uidDir in Directory.EnumerateDirectories(userdataRoot))
            {
                var uidInfo = new DirectoryInfo(uidDir);
                if ((uidInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string uidName = uidInfo.Name;
                if (string.IsNullOrWhiteSpace(uidName)
                    || uidName.StartsWith('.')
                    || uidName.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
                    || uidName.Equals("0", StringComparison.Ordinal))
                {
                    continue;
                }

                string appDir = Path.Combine(uidDir, appIdStr);
                if (!Directory.Exists(appDir))
                {
                    continue;
                }

                if ((File.GetAttributes(appDir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string remoteDir = Path.Combine(appDir, "remote");
                if (Directory.Exists(remoteDir)
                    && (File.GetAttributes(remoteDir) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                builder.TryAdd(
                    category: GameDataCategory.SaveData,
                    sourceRule: GameDataSourceRule.NativeRule,
                    sourceLabel: "Steam 本地存档",
                    pathOrRegistryKey: remoteDir,
                    description: $"Steam userdata ({uidName}) 本地存档目录 (remote)",
                    defaultSelected: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Skipped inaccessible Steam userdata directory under {SteamRoot}.", steamRoot);
            }
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
                if (safe && ContainsDisallowedReparsePointOrSubtree(normalizedKey, installDir, identity, options))
                {
                    safe = false;
                    reason = $"安全围栏拦截：目标路径或其子目录包含符号链接/NTFS 目录联接点 (ReparsePoint)，严禁递归清理：{normalizedKey}";
                }

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

    private void TrackPotentialEmptyCleanupParent(
        GameDataEntryDto entry,
        GameRecord game,
        GameDataTargetIdentity identity,
        HashSet<string> candidateParents)
    {
        if (entry.SourceRule == GameDataSourceRule.SteamEmulator && game.SteamAppId.HasValue)
        {
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

            return;
        }

        // Also prune completely empty game-specific leaf containers (e.g. ...\Rockstar Games\GTA III Definitive Edition\Profiles
        // and ...\Rockstar Games\GTA III Definitive Edition), stopping strictly BEFORE any shared publisher/root parent!
        string fullInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var current = Directory.GetParent(entry.PathOrRegistryKey);
        while (current is not null)
        {
            string fullCurrent = Path.TrimEndingDirectorySeparator(current.FullName);
            if (string.Equals(fullCurrent, fullInstallDir, StringComparison.OrdinalIgnoreCase)
                || fullCurrent.StartsWith(fullInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!GameDataSafetyFence.IsGameSpecificPathOutsideInstall(fullCurrent, identity, _options))
            {
                break;
            }

            candidateParents.Add(fullCurrent);
            current = current.Parent;
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

    private static bool IsSafeSnapshotIdentifier(string snapshotId)
    {
        string trimmed = snapshotId.Trim();
        if (trimmed.Length == 0 || trimmed is "." or "..")
        {
            return false;
        }

        return !trimmed.Contains('\\', StringComparison.Ordinal)
               && !trimmed.Contains('/', StringComparison.Ordinal)
               && !trimmed.Contains("..", StringComparison.Ordinal)
               && !trimmed.Contains(':', StringComparison.Ordinal);
    }

    private async Task<List<(UndoManifestModel Manifest, string SnapshotDirectory)>> LoadAllUndoManifestsAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        string gameTrashRoot = _paths.GetGameTrashDirectory(gameId);
        if (!Directory.Exists(gameTrashRoot))
        {
            return [];
        }

        var manifests = new List<(UndoManifestModel Manifest, string SnapshotDirectory)>();
        foreach (string subDir in Directory.EnumerateDirectories(gameTrashRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (parsed is not null)
                {
                    manifests.Add((parsed, subDir));
                }
            }
            catch
            {
                // Skip corrupted trash manifest.
            }
        }

        manifests.Sort(static (left, right) =>
        {
            int cmp = right.Manifest.CreatedUtc.CompareTo(left.Manifest.CreatedUtc);
            return cmp != 0
                ? cmp
                : string.Compare(right.Manifest.SnapshotId, left.Manifest.SnapshotId, StringComparison.Ordinal);
        });

        return manifests;
    }

    private async Task<(UndoManifestModel Manifest, string SnapshotDirectory)?> LoadLatestUndoManifestAsync(
        string gameId,
        CancellationToken cancellationToken)
    {
        var all = await LoadAllUndoManifestsAsync(gameId, cancellationToken).ConfigureAwait(false);
        return all.Count > 0 ? all[0] : null;
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

    private static GameDataUndoSnapshotDto ToUndoDto(UndoManifestModel manifest, string snapshotDir)
    {
        int payloadFileCount = 0;
        long payloadBytes = 0;
        try
        {
            string filesDir = Path.Combine(snapshotDir, "files");
            if (Directory.Exists(filesDir))
            {
                var (fc, fb, _) = InspectFilesystemStats(filesDir);
                payloadFileCount += fc;
                payloadBytes += fb;
            }

            string regDir = Path.Combine(snapshotDir, "registry");
            if (Directory.Exists(regDir))
            {
                var (rc, rb, _) = InspectFilesystemStats(regDir);
                payloadFileCount += rc;
                payloadBytes += rb;
            }
        }
        catch
        {
            // Fall back to manifest-recorded metadata.
        }

        return new GameDataUndoSnapshotDto
        {
            SnapshotId = manifest.SnapshotId,
            GameId = manifest.GameId,
            BackupDirectory = snapshotDir,
            CreatedUtc = manifest.CreatedUtc,
            BackedUpEntryCount = manifest.Items.Count,
            BackedUpFileCount = payloadFileCount > 0 ? payloadFileCount : manifest.Items.Count,
            TotalBytes = Math.Max(manifest.TotalBytes, payloadBytes),
            IncludedSpawnStateReset = manifest.IncludedSpawnStateReset,
            Status = string.IsNullOrWhiteSpace(manifest.Status) ? UndoManifestStatus.Completed : manifest.Status,
            BackedUpTargets = manifest.Items.Select(static i => i.OriginalPathOrKey).ToArray(),
        };
    }

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
