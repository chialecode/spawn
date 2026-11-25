using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Core.AntiCheat;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Implements <see cref="IGameLibraryService"/>: single-game folder/exe import, multi-folder library root scanning,
/// non-destructive rescanning with four-layer static anti-cheat classification and <c>VerifiedOfflineBypass</c>
/// lifecycle enforcement, metadata editing, and safe catalog removal (never deleting user game files).
/// </summary>
public sealed class GameLibraryService : IGameLibraryService
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly GameDirectoryScanner _scanner;
    private readonly ISpawnEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameLibraryService> _logger;
    private readonly ITrainerSafetyGate? _trainerSafetyGate;
    private readonly GameScanOptions? _scanOptions;

    public GameLibraryService(
        SpawnDatabase database,
        SpawnPaths paths,
        GameDirectoryScanner scanner,
        ISpawnEventBus eventBus,
        TimeProvider? timeProvider = null,
        ILogger<GameLibraryService>? logger = null,
        ITrainerSafetyGate? trainerSafetyGate = null,
        GameScanOptions? scanOptions = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(eventBus);

        _database = database;
        _paths = paths;
        _scanner = scanner;
        _eventBus = eventBus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GameLibraryService>.Instance;
        _trainerSafetyGate = trainerSafetyGate;
        _scanOptions = scanOptions;
    }

    public async Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default)
    {
        var games = await _database.GetAllGamesAsync(cancellationToken).ConfigureAwait(false);
        if (!HasPotentialParentContainerOverlap(games))
        {
            return games;
        }

        int retired = await ReconcileAllStaleParentContainersCoreAsync(games, cancellationToken).ConfigureAwait(false);
        return retired > 0
            ? await _database.GetAllGamesAsync(cancellationToken).ConfigureAwait(false)
            : games;
    }

    public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
        _database.GetGameByIdAsync(gameId, cancellationToken);

    private static string GetUserPinnedExeSettingKey(string gameId) =>
        $"launcher.user_pinned_exe.{gameId}";

    public async Task<GameRecord?> ImportGameAsync(
        string directoryOrExePath,
        CancellationToken cancellationToken = default)
    {
        var resolved = GameDirectoryScanner.ResolveImportTarget(directoryOrExePath);
        if (resolved is null)
        {
            return null;
        }

        var (installDir, explicitExeRelPath) = resolved.Value;

        // When importing a directory (not an explicit .exe file), expand multi-game anthology/pack containers
        // into their standalone child game entries and retire any stale unpinned merged parent row.
        if (explicitExeRelPath is null)
        {
            var expandedDirs = GameDirectoryScanner.TryExpandMultiGameContainer(installDir);
            if (expandedDirs.Count >= 2)
            {
                var importedChildren = await ImportExpandedChildDirectoriesAsync(expandedDirs, cancellationToken)
                    .ConfigureAwait(false);
                if (importedChildren.Count >= 2)
                {
                    await ReconcileStaleParentContainerAsync(installDir, cancellationToken).ConfigureAwait(false);
                }

                return importedChildren.Count > 0 ? importedChildren[0] : null;
            }
        }

        return await ImportSingleResolvedTargetAsync(installDir, explicitExeRelPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GameRecord?> ImportSingleResolvedTargetAsync(
        string installDir,
        string? explicitExeRelPath,
        CancellationToken cancellationToken,
        bool allowContainerExpansion = true)
    {
        var existing = await _database.GetGameByInstallDirAsync(installDir, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        string gameId = existing?.GameId ?? SpawnId.NewUlid(now);
        bool isNewImport = existing is null;
        bool hasExplicitExe = explicitExeRelPath is not null;
        bool isUserPinnedExe = !hasExplicitExe
            && existing is not null
            && await IsExecutableUserPinnedAsync(existing.GameId, cancellationToken).ConfigureAwait(false);

        // Defense-in-depth: never persist an unpinned multi-game anthology container as a single game record.
        if (allowContainerExpansion && !hasExplicitExe && !isUserPinnedExe)
        {
            var expandedDirs = GameDirectoryScanner.TryExpandMultiGameContainer(installDir);
            if (expandedDirs.Count >= 2)
            {
                var importedChildren = await ImportExpandedChildDirectoriesAsync(expandedDirs, cancellationToken)
                    .ConfigureAwait(false);
                if (importedChildren.Count >= 2)
                {
                    await ReconcileStaleParentContainerAsync(installDir, cancellationToken).ConfigureAwait(false);
                }

                return importedChildren.Count > 0 ? importedChildren[0] : null;
            }
        }

        string? preferredExeRelPath = hasExplicitExe
            ? explicitExeRelPath
            : (isUserPinnedExe ? existing?.ExecutableRelPath : null);

        var scanned = await _scanner.ScanGameDirectoryAsync(
            installDir,
            gameId,
            preferredExeRelPath,
            cancellationToken).ConfigureAwait(false);

        if (scanned is null)
        {
            return null;
        }

        var merged = MergeScannedIntoRecord(
            gameId,
            scanned,
            existing,
            preserveExistingExe: isUserPinnedExe && !hasExplicitExe,
            now);
        var (record, shouldInvalidateBypassInDb) = await ApplyFourLayerStaticClassificationAndBypassPolicyAsync(
            merged,
            existing,
            cancellationToken).ConfigureAwait(false);

        await _database.UpsertGameAsync(record, cancellationToken).ConfigureAwait(false);
        if (hasExplicitExe)
        {
            await _database.SetSettingAsync(GetUserPinnedExeSettingKey(record.GameId), "1", cancellationToken)
                .ConfigureAwait(false);
        }

        if (shouldInvalidateBypassInDb)
        {
            await _database.SetGameVerifiedOfflineBypassAsync(
                record.GameId,
                enabled: false,
                confirmedAtUtc: null,
                cancellationToken).ConfigureAwait(false);
        }

        await _eventBus.PublishAsync(
            new GameImportedOrRescannedEvent(record, isNewImport, now),
            cancellationToken).ConfigureAwait(false);

        // Re-read from DB in case synchronous event subscribers (e.g. Achievement module) enriched capture mode/label.
        return await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false) ?? record;
    }

    public async Task<IReadOnlyList<GameRecord>> ScanLibraryDirectoryAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidateDirs = _scanner.DiscoverCandidateGameDirectories(normalizedRoot);
        if (candidateDirs.Count == 0)
        {
            return [];
        }

        var imported = new List<GameRecord>(candidateDirs.Count);
        var candidateParentContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            normalizedRoot,
        };

        foreach (string dir in candidateDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
                string? parentPath = Directory.GetParent(normalizedCandidate)?.FullName;
                if (!string.IsNullOrWhiteSpace(parentPath))
                {
                    candidateParentContainers.Add(Path.TrimEndingDirectorySeparator(parentPath));
                }

                var expandedNested = GameDirectoryScanner.TryExpandMultiGameContainer(normalizedCandidate);
                if (expandedNested.Count >= 2)
                {
                    candidateParentContainers.Add(normalizedCandidate);
                    var nestedImported = await ImportExpandedChildDirectoriesAsync(expandedNested, cancellationToken)
                        .ConfigureAwait(false);
                    imported.AddRange(nestedImported);
                    continue;
                }

                var game = await ImportSingleResolvedTargetAsync(
                    normalizedCandidate,
                    explicitExeRelPath: null,
                    cancellationToken,
                    allowContainerExpansion: false).ConfigureAwait(false);
                if (game is not null)
                {
                    imported.Add(game);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Skipped game candidate folder {Directory} due to scan error.", dir);
                }
            }
        }

        foreach (string parentContainer in candidateParentContainers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileStaleParentContainerAsync(parentContainer, cancellationToken).ConfigureAwait(false);
        }

        await ReconcileAllStaleParentContainersAsync(cancellationToken).ConfigureAwait(false);

        return imported;
    }

    public async Task<GameRecord?> RescanGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        var existing = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (existing is null || !Directory.Exists(existing.InstallDir))
        {
            return null;
        }

        bool isUserPinnedExe = await IsExecutableUserPinnedAsync(existing.GameId, cancellationToken)
            .ConfigureAwait(false);

        // If an unpinned existing record points to a multi-game anthology container, expand into standalone child games
        // and retire the stale merged parent row when safe.
        if (!isUserPinnedExe)
        {
            var expandedDirs = GameDirectoryScanner.TryExpandMultiGameContainer(existing.InstallDir);
            if (expandedDirs.Count >= 2)
            {
                var importedChildren = await ImportExpandedChildDirectoriesAsync(expandedDirs, cancellationToken)
                    .ConfigureAwait(false);
                if (importedChildren.Count >= 2)
                {
                    await ReconcileStaleParentContainerAsync(existing.InstallDir, cancellationToken).ConfigureAwait(false);
                    return importedChildren[0];
                }
            }
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var scanned = await _scanner.ScanGameDirectoryAsync(
            existing.InstallDir,
            existing.GameId,
            explicitExeRelPath: isUserPinnedExe ? existing.ExecutableRelPath : null,
            cancellationToken).ConfigureAwait(false);

        if (scanned is null)
        {
            return null;
        }

        var merged = MergeScannedIntoRecord(
            existing.GameId,
            scanned,
            existing,
            preserveExistingExe: isUserPinnedExe,
            now);
        var (updated, shouldInvalidateBypassInDb) = await ApplyFourLayerStaticClassificationAndBypassPolicyAsync(
            merged,
            existing,
            cancellationToken).ConfigureAwait(false);

        await _database.UpsertGameAsync(updated, cancellationToken).ConfigureAwait(false);
        if (shouldInvalidateBypassInDb)
        {
            await _database.SetGameVerifiedOfflineBypassAsync(
                updated.GameId,
                enabled: false,
                confirmedAtUtc: null,
                cancellationToken).ConfigureAwait(false);
        }

        await _eventBus.PublishAsync(
            new GameImportedOrRescannedEvent(updated, IsNewImport: false, OccurredUtc: now),
            cancellationToken).ConfigureAwait(false);

        return await _database.GetGameByIdAsync(existing.GameId, cancellationToken).ConfigureAwait(false) ?? updated;
    }

    public async Task<GameRecord?> UpdateGameMetadataAsync(
        string gameId,
        GameMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return null;
        }

        var existing = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        string newTitle = !string.IsNullOrWhiteSpace(update.Title) ? update.Title.Trim() : existing.Title;
        string newSortTitle = update.SortTitle is not null
            ? (string.IsNullOrWhiteSpace(update.SortTitle) ? GameTitleCleaner.BuildSortTitle(newTitle) : update.SortTitle.Trim())
            : (!string.Equals(newTitle, existing.Title, StringComparison.Ordinal)
                ? GameTitleCleaner.BuildSortTitle(newTitle)
                : existing.SortTitle);

        // Static anti_cheat_tier is NEVER altered by VerifiedOfflineBypass.
        var updatedStaticTier = update.AntiCheatTier ?? existing.AntiCheatTier;

        var updated = existing with
        {
            Title = newTitle,
            SortTitle = newSortTitle,
            ExecutableRelPath = !string.IsNullOrWhiteSpace(update.ExecutableRelPath)
                ? update.ExecutableRelPath.Trim()
                : existing.ExecutableRelPath,
            LaunchArguments = update.LaunchArguments ?? existing.LaunchArguments,
            PlatformSource = update.PlatformSource ?? existing.PlatformSource,
            AntiCheatTier = updatedStaticTier,
            AntiCheatSignature = update.AntiCheatSignature ?? existing.AntiCheatSignature,
            SteamAppId = update.SteamAppId ?? existing.SteamAppId,
            GogGameId = update.GogGameId ?? existing.GogGameId,
            GogClientId = update.GogClientId ?? existing.GogClientId,
            AchievementCaptureMode = update.AchievementCaptureMode ?? existing.AchievementCaptureMode,
            AchievementChannelLabel = update.AchievementChannelLabel ?? existing.AchievementChannelLabel,
            CoverImagePath = update.CoverImagePath ?? existing.CoverImagePath,
            IconPath = update.IconPath ?? existing.IconPath,
            HeroImagePath = update.HeroImagePath ?? existing.HeroImagePath,
            SteamBuildId = update.SteamBuildId ?? existing.SteamBuildId,
            SteamLastUpdatedUtc = update.SteamLastUpdatedUtc ?? existing.SteamLastUpdatedUtc,
        };

        await _database.UpsertGameAsync(updated, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(update.ExecutableRelPath))
        {
            await _database.SetSettingAsync(GetUserPinnedExeSettingKey(gameId), "1", cancellationToken)
                .ConfigureAwait(false);
        }

        if (update.VerifiedOfflineBypass.HasValue)
        {
            if (update.VerifiedOfflineBypass.Value)
            {
                bool canEnable = await IsEligibleForVerifiedOfflineBypassAsync(updated, cancellationToken)
                    .ConfigureAwait(false);
                if (canEnable)
                {
                    DateTimeOffset confirmedAt = update.VerifiedOfflineBypassAtUtc ?? _timeProvider.GetUtcNow();
                    await _database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: true, confirmedAt, cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (existing.VerifiedOfflineBypass)
                {
                    await _database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: false, confirmedAtUtc: null, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await _database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: false, confirmedAtUtc: null, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else if (existing.VerifiedOfflineBypass && updatedStaticTier != AntiCheatTier.Tier2)
        {
            await _database.SetGameVerifiedOfflineBypassAsync(gameId, enabled: false, confirmedAtUtc: null, cancellationToken)
                .ConfigureAwait(false);
        }

        bool identityChanged = update.SteamAppId.HasValue
            || update.GogGameId is not null
            || update.PlatformSource.HasValue;

        var persisted = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false) ?? updated;

        if (identityChanged)
        {
            await _eventBus.PublishAsync(
                new GameImportedOrRescannedEvent(persisted, IsNewImport: false, OccurredUtc: _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }

        return await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false) ?? persisted;
    }

    public async Task<bool> RemoveGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return false;
        }

        var existing = await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        bool deleted = await _database.DeleteGameAsync(gameId, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return false;
        }

        await _database.SetSettingAsync(GetUserPinnedExeSettingKey(gameId), null, cancellationToken)
            .ConfigureAwait(false);

        // Delete ONLY SPAWN's isolated artwork cache folder under %LOCALAPPDATA%\spawn[-dev]\artwork\<gameId>.
        // NEVER touch existing.InstallDir or any user game files!
        TryDeleteCachedArtworkDirectoryOnly(gameId);

        await _eventBus.PublishAsync(
            new GameRemovedEvent(gameId, existing.Title, _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private async Task<bool> IsExecutableUserPinnedAsync(string gameId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return false;
        }

        string? val = await _database.GetSettingAsync(GetUserPinnedExeSettingKey(gameId), cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(val, "1", StringComparison.Ordinal);
    }

    private async Task<List<GameRecord>> ImportExpandedChildDirectoriesAsync(
        IReadOnlyList<string> expandedDirs,
        CancellationToken cancellationToken)
    {
        var importedChildren = new List<GameRecord>(expandedDirs.Count);
        foreach (string childDir in expandedDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var childRecord = await ImportSingleResolvedTargetAsync(
                    childDir,
                    explicitExeRelPath: null,
                    cancellationToken,
                    allowContainerExpansion: false).ConfigureAwait(false);
                if (childRecord is not null)
                {
                    importedChildren.Add(childRecord);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Skipped expanded child game directory {Directory} due to scan error.", childDir);
                }
            }
        }

        return importedChildren;
    }

    /// <summary>
    /// Scans all persisted catalog entries for unpinned, zero-progress multi-game anthology parent containers
    /// whose <c>&gt;= 2</c> standalone child games are already registered, retiring stale duplicate parent rows
    /// without touching user game files.
    /// </summary>
    public async Task<int> ReconcileAllStaleParentContainersAsync(CancellationToken cancellationToken = default)
    {
        var games = await _database.GetAllGamesAsync(cancellationToken).ConfigureAwait(false);
        if (!HasPotentialParentContainerOverlap(games))
        {
            return 0;
        }

        return await ReconcileAllStaleParentContainersCoreAsync(games, cancellationToken).ConfigureAwait(false);
    }

    private static bool HasPotentialParentContainerOverlap(IReadOnlyList<GameRecord> games)
    {
        if (games.Count < 3)
        {
            return false;
        }

        var normalizedDirs = new string?[games.Count];
        for (int i = 0; i < games.Count; i++)
        {
            normalizedDirs[i] = TryNormalizeDirectoryPath(games[i].InstallDir);
        }

        for (int i = 0; i < games.Count; i++)
        {
            string? candidateParent = normalizedDirs[i];
            if (string.IsNullOrWhiteSpace(candidateParent))
            {
                continue;
            }

            string childPrefix = candidateParent + Path.DirectorySeparatorChar;
            int childCount = 0;
            for (int j = 0; j < games.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                string? other = normalizedDirs[j];
                if (other is not null && other.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    childCount++;
                    if (childCount >= 2)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private async Task<int> ReconcileAllStaleParentContainersCoreAsync(
        IReadOnlyList<GameRecord> snapshot,
        CancellationToken cancellationToken)
    {
        var normalizedDirs = new string?[snapshot.Count];
        for (int i = 0; i < snapshot.Count; i++)
        {
            normalizedDirs[i] = TryNormalizeDirectoryPath(snapshot[i].InstallDir);
        }

        var candidateParentDirs = new List<string>();
        var seenParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < snapshot.Count; i++)
        {
            string? candidateParent = normalizedDirs[i];
            if (string.IsNullOrWhiteSpace(candidateParent) || !seenParents.Add(candidateParent))
            {
                continue;
            }

            string childPrefix = candidateParent + Path.DirectorySeparatorChar;
            int childCount = 0;
            for (int j = 0; j < snapshot.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                string? other = normalizedDirs[j];
                if (other is not null && other.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    childCount++;
                    if (childCount >= 2)
                    {
                        candidateParentDirs.Add(candidateParent);
                        break;
                    }
                }
            }
        }

        int retiredCount = 0;
        foreach (string parentDir in candidateParentDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReconcileStaleParentContainerAsync(parentDir, cancellationToken).ConfigureAwait(false))
            {
                retiredCount++;
            }
        }

        return retiredCount;
    }

    private static string? TryNormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> ReconcileStaleParentContainerAsync(
        string parentDir,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
        {
            return false;
        }

        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDir));
        var staleParent = await _database.GetGameByInstallDirAsync(normalizedParent, cancellationToken)
            .ConfigureAwait(false);
        if (staleParent is null)
        {
            return false;
        }

        if (await IsExecutableUserPinnedAsync(staleParent.GameId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (staleParent.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
            || await _database.GetEnablerBackupAsync(staleParent.GameId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        var unlocks = await _database.GetAchievementUnlocksAsync(staleParent.GameId, cancellationToken)
            .ConfigureAwait(false);
        if (unlocks.Any(u => u.IsUnlocked))
        {
            return false;
        }

        var boundTrainers = await _database.GetTrainersByGameIdAsync(staleParent.GameId, cancellationToken)
            .ConfigureAwait(false);
        if (boundTrainers.Count > 0)
        {
            return false;
        }

        var saveSnapshots = await _database.GetSaveSnapshotsByGameAsync(staleParent.GameId, limit: 1, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (saveSnapshots.Count > 0)
        {
            return false;
        }

        var preTrainerSnapshots = await _database.GetPreTrainerSnapshotsByGameAsync(
                staleParent.GameId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (preTrainerSnapshots.Count > 0)
        {
            return false;
        }

        string childPrefix = normalizedParent + Path.DirectorySeparatorChar;
        var allGames = await _database.GetAllGamesAsync(cancellationToken).ConfigureAwait(false);
        int registeredStandaloneChildren = allGames.Count(g =>
        {
            if (string.Equals(g.GameId, staleParent.GameId, StringComparison.Ordinal))
            {
                return false;
            }

            string? normChild = TryNormalizeDirectoryPath(g.InstallDir);
            return normChild is not null && normChild.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase);
        });

        if (registeredStandaloneChildren < 2)
        {
            return false;
        }

        if (GameDirectoryScanner.TryExpandMultiGameContainer(normalizedParent).Count < 2)
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Retiring stale merged anthology container record {GameId} ({Title}) after registering {ChildCount} standalone child games.",
                staleParent.GameId,
                staleParent.Title,
                registeredStandaloneChildren);
        }

        return await RemoveGameAsync(staleParent.GameId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(GameRecord Record, bool ShouldInvalidateBypassInDb)> ApplyFourLayerStaticClassificationAndBypassPolicyAsync(
        GameRecord candidate,
        GameRecord? existing,
        CancellationToken cancellationToken)
    {
        AntiCheatTier staticTier = candidate.AntiCheatTier;
        string? primarySignature = candidate.AntiCheatSignature;
        bool? hasSinglePlayer = null;
        bool isMultiplayerOnly = false;

        if (_trainerSafetyGate is not null)
        {
            bool allowOnline = _scanOptions?.AllowOnlineMetadataLookup ?? true;
            var fourLayer = await _trainerSafetyGate.InspectGameFourLayerAsync(
                candidate,
                allowOnlineLookup: allowOnline,
                ct: cancellationToken).ConfigureAwait(false);

            staticTier = fourLayer.EffectiveTier;
            if (fourLayer.OfflineConservativeFallbackApplied
                && existing is not null
                && existing.AntiCheatTier > staticTier)
            {
                staticTier = existing.AntiCheatTier;
            }

            primarySignature = fourLayer.PrimarySignature ?? candidate.AntiCheatSignature;
            hasSinglePlayer = fourLayer.HasSinglePlayerCategory;
            isMultiplayerOnly = fourLayer.IsMultiplayerOnly;
        }
        else
        {
            var awacy = AreWeAntiCheatYetIndex.Default.Inspect(candidate);
            if (awacy is not null)
            {
                if (awacy.Tier > staticTier)
                {
                    staticTier = awacy.Tier;
                    primarySignature = awacy.PrimarySignature;
                }
                else
                {
                    primarySignature ??= awacy.PrimarySignature;
                }

                hasSinglePlayer = awacy.HasSinglePlayerHint;
                isMultiplayerOnly = awacy.HasSinglePlayerHint == false;
            }
        }

        bool bypassEnabled = existing?.VerifiedOfflineBypass ?? candidate.VerifiedOfflineBypass;
        DateTimeOffset? bypassConfirmedAt = existing?.VerifiedOfflineBypassAtUtc ?? candidate.VerifiedOfflineBypassAtUtc;
        bool shouldInvalidateBypassInDb = false;

        if (bypassEnabled)
        {
            // Rescan must NOT clear user's VerifiedOfflineBypass confirmation when conditions still hold;
            // auto-invalidate ONLY when static Tier becomes non-Tier2 (e.g. Tier 1/3) or single-player mode is lost.
            bool tierBecameIneligible = staticTier != AntiCheatTier.Tier2;
            bool lostSinglePlayerMode = hasSinglePlayer == false || isMultiplayerOnly;

            if (tierBecameIneligible || lostSinglePlayerMode)
            {
                bypassEnabled = false;
                bypassConfirmedAt = null;
                shouldInvalidateBypassInDb = existing is not null;

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Auto-invalidated VerifiedOfflineBypass for game {GameId} ({Title}) during rescan: staticTier={StaticTier}, hasSinglePlayer={HasSinglePlayer}.",
                        candidate.GameId,
                        candidate.Title,
                        staticTier,
                        hasSinglePlayer);
                }
            }
        }

        var updated = candidate with
        {
            AntiCheatTier = staticTier,
            AntiCheatSignature = primarySignature,
            VerifiedOfflineBypass = bypassEnabled,
            VerifiedOfflineBypassAtUtc = bypassConfirmedAt,
        };

        return (updated, shouldInvalidateBypassInDb);
    }

    private async Task<bool> IsEligibleForVerifiedOfflineBypassAsync(
        GameRecord game,
        CancellationToken cancellationToken)
    {
        if (game.AntiCheatTier != AntiCheatTier.Tier2)
        {
            return false;
        }

        if (_trainerSafetyGate is not null)
        {
            bool allowOnline = _scanOptions?.AllowOnlineMetadataLookup ?? true;
            var fourLayer = await _trainerSafetyGate.InspectGameFourLayerAsync(
                game,
                allowOnlineLookup: allowOnline,
                ct: cancellationToken).ConfigureAwait(false);
            var effectiveStaticTier = fourLayer.EffectiveTier > game.AntiCheatTier ? fourLayer.EffectiveTier : game.AntiCheatTier;
            return effectiveStaticTier == AntiCheatTier.Tier2 && fourLayer.HasSinglePlayerCategory == true;
        }

        var awacy = AreWeAntiCheatYetIndex.Default.Inspect(game);
        if (awacy is not null)
        {
            var effectiveStaticTier = awacy.Tier > game.AntiCheatTier ? awacy.Tier : game.AntiCheatTier;
            return effectiveStaticTier == AntiCheatTier.Tier2 && awacy.HasSinglePlayerHint == true;
        }

        return false;
    }

    private void TryDeleteCachedArtworkDirectoryOnly(string gameId)
    {
        try
        {
            string artworkRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.ArtworkDirectory))
                                 + Path.DirectorySeparatorChar;
            string gameArtworkDir = Path.GetFullPath(_paths.GetGameArtworkDirectory(gameId));

            if (gameArtworkDir.StartsWith(artworkRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(gameArtworkDir))
            {
                Directory.Delete(gameArtworkDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Could not clean cached artwork folder for removed game {GameId}.", gameId);
            }
        }
    }

    private static GameRecord MergeScannedIntoRecord(
        string gameId,
        ScannedGameDescriptor scanned,
        GameRecord? existing,
        bool preserveExistingExe,
        DateTimeOffset now)
    {
        if (existing is null)
        {
            return new GameRecord
            {
                GameId = gameId,
                Title = scanned.Title,
                SortTitle = scanned.SortTitle,
                InstallDir = scanned.InstallDir,
                ExecutableRelPath = scanned.ExecutableRelPath,
                PlatformSource = scanned.PlatformSource,
                AntiCheatTier = scanned.AntiCheatTier,
                AntiCheatSignature = scanned.AntiCheatSignature,
                SteamAppId = scanned.SteamAppId,
                GogGameId = scanned.GogGameId,
                GogClientId = scanned.GogClientId,
                AchievementCaptureMode = scanned.AchievementCaptureMode,
                AchievementChannelLabel = scanned.AchievementChannelLabel,
                CoverImagePath = scanned.CoverImagePath,
                IconPath = scanned.IconPath,
                HeroImagePath = scanned.HeroImagePath,
                SteamBuildId = scanned.SteamBuildId,
                SteamLastUpdatedUtc = scanned.SteamLastUpdatedUtc,
                CreatedUtc = now,
            };
        }

        // Preserve user-pinned executable when still present on disk; otherwise refresh scanner-selected executable.
        string exeRelPath = preserveExistingExe && File.Exists(existing.ExecutableFullPath)
            ? existing.ExecutableRelPath
            : scanned.ExecutableRelPath;

        // Preserve gbe_fork activated state if already enabled.
        var captureMode = existing.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
            ? AchievementCaptureMode.GbeForkActivated
            : scanned.AchievementCaptureMode;

        string? channelLabel = existing.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
            ? (existing.AchievementChannelLabel ?? "Goldberg GSE (已无损激活) · 就绪")
            : scanned.AchievementChannelLabel;

        return existing with
        {
            Title = scanned.Title,
            SortTitle = scanned.SortTitle,
            InstallDir = scanned.InstallDir,
            ExecutableRelPath = exeRelPath,
            PlatformSource = scanned.PlatformSource,
            AntiCheatTier = scanned.AntiCheatTier,
            AntiCheatSignature = scanned.AntiCheatSignature,
            SteamAppId = scanned.SteamAppId ?? existing.SteamAppId,
            GogGameId = scanned.GogGameId ?? existing.GogGameId,
            GogClientId = scanned.GogClientId ?? existing.GogClientId,
            AchievementCaptureMode = captureMode,
            AchievementChannelLabel = channelLabel,
            CoverImagePath = scanned.CoverImagePath ?? existing.CoverImagePath,
            IconPath = scanned.IconPath ?? existing.IconPath,
            HeroImagePath = scanned.HeroImagePath ?? existing.HeroImagePath,
            SteamBuildId = scanned.SteamBuildId ?? existing.SteamBuildId,
            SteamLastUpdatedUtc = scanned.SteamLastUpdatedUtc ?? existing.SteamLastUpdatedUtc,
            VerifiedOfflineBypass = existing.VerifiedOfflineBypass,
            VerifiedOfflineBypassAtUtc = existing.VerifiedOfflineBypassAtUtc,
        };
    }
}
