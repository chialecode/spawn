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

    public Task<IReadOnlyList<GameRecord>> GetAllGamesAsync(CancellationToken cancellationToken = default) =>
        _database.GetAllGamesAsync(cancellationToken);

    public Task<GameRecord?> GetGameByIdAsync(string gameId, CancellationToken cancellationToken = default) =>
        _database.GetGameByIdAsync(gameId, cancellationToken);

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
        var existing = await _database.GetGameByInstallDirAsync(installDir, cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        string gameId = existing?.GameId ?? SpawnId.NewUlid(now);
        bool isNewImport = existing is null;

        var scanned = await _scanner.ScanGameDirectoryAsync(
            installDir,
            gameId,
            explicitExeRelPath ?? existing?.ExecutableRelPath,
            cancellationToken).ConfigureAwait(false);

        if (scanned is null)
        {
            return null;
        }

        var merged = MergeScannedIntoRecord(gameId, scanned, existing, explicitExeRelPath is not null, now);
        var (record, shouldInvalidateBypassInDb) = await ApplyFourLayerStaticClassificationAndBypassPolicyAsync(
            merged,
            existing,
            cancellationToken).ConfigureAwait(false);

        await _database.UpsertGameAsync(record, cancellationToken).ConfigureAwait(false);
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

        var candidateDirs = _scanner.DiscoverCandidateGameDirectories(rootDirectory);
        if (candidateDirs.Count == 0)
        {
            return [];
        }

        var imported = new List<GameRecord>(candidateDirs.Count);
        foreach (string dir in candidateDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = await ImportGameAsync(dir, cancellationToken).ConfigureAwait(false);
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

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var scanned = await _scanner.ScanGameDirectoryAsync(
            existing.InstallDir,
            existing.GameId,
            explicitExeRelPath: null,
            cancellationToken).ConfigureAwait(false);

        if (scanned is null)
        {
            return null;
        }

        var merged = MergeScannedIntoRecord(existing.GameId, scanned, existing, overrideExeRelPath: false, now);
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

        // Delete ONLY SPAWN's isolated artwork cache folder under %LOCALAPPDATA%\spawn[-dev]\artwork\<gameId>.
        // NEVER touch existing.InstallDir or any user game files!
        TryDeleteCachedArtworkDirectoryOnly(gameId);

        await _eventBus.PublishAsync(
            new GameRemovedEvent(gameId, existing.Title, _timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return true;
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
        bool overrideExeRelPath,
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

        // Preserve user-customized executable if still valid on disk unless an explicit .exe was passed.
        string exeRelPath = overrideExeRelPath
            ? scanned.ExecutableRelPath
            : (File.Exists(existing.ExecutableFullPath) ? existing.ExecutableRelPath : scanned.ExecutableRelPath);

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
