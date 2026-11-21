using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Implements <see cref="IGameLibraryService"/>: single-game folder/exe import, multi-folder library root scanning,
/// non-destructive rescanning, metadata editing, and safe catalog removal (never deleting user game files).
/// </summary>
public sealed class GameLibraryService : IGameLibraryService
{
    private readonly SpawnDatabase _database;
    private readonly SpawnPaths _paths;
    private readonly GameDirectoryScanner _scanner;
    private readonly ISpawnEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GameLibraryService> _logger;

    public GameLibraryService(
        SpawnDatabase database,
        SpawnPaths paths,
        GameDirectoryScanner scanner,
        ISpawnEventBus eventBus,
        TimeProvider? timeProvider = null,
        ILogger<GameLibraryService>? logger = null)
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

        var record = MergeScannedIntoRecord(gameId, scanned, existing, explicitExeRelPath is not null, now);
        await _database.UpsertGameAsync(record, cancellationToken).ConfigureAwait(false);

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

        var updated = MergeScannedIntoRecord(existing.GameId, scanned, existing, overrideExeRelPath: false, now);
        await _database.UpsertGameAsync(updated, cancellationToken).ConfigureAwait(false);

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

        var updated = existing with
        {
            Title = newTitle,
            SortTitle = newSortTitle,
            ExecutableRelPath = !string.IsNullOrWhiteSpace(update.ExecutableRelPath)
                ? update.ExecutableRelPath.Trim()
                : existing.ExecutableRelPath,
            LaunchArguments = update.LaunchArguments ?? existing.LaunchArguments,
            PlatformSource = update.PlatformSource ?? existing.PlatformSource,
            AntiCheatTier = update.AntiCheatTier ?? existing.AntiCheatTier,
            AntiCheatSignature = update.AntiCheatSignature ?? existing.AntiCheatSignature,
            SteamAppId = update.SteamAppId ?? existing.SteamAppId,
            GogGameId = update.GogGameId ?? existing.GogGameId,
            GogClientId = update.GogClientId ?? existing.GogClientId,
            AchievementCaptureMode = update.AchievementCaptureMode ?? existing.AchievementCaptureMode,
            AchievementChannelLabel = update.AchievementChannelLabel ?? existing.AchievementChannelLabel,
            CoverImagePath = update.CoverImagePath ?? existing.CoverImagePath,
            IconPath = update.IconPath ?? existing.IconPath,
            HeroImagePath = update.HeroImagePath ?? existing.HeroImagePath,
        };

        await _database.UpsertGameAsync(updated, cancellationToken).ConfigureAwait(false);

        bool identityChanged = update.SteamAppId.HasValue
            || update.GogGameId is not null
            || update.PlatformSource.HasValue;

        if (identityChanged)
        {
            await _eventBus.PublishAsync(
                new GameImportedOrRescannedEvent(updated, IsNewImport: false, OccurredUtc: _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }

        return await _database.GetGameByIdAsync(gameId, cancellationToken).ConfigureAwait(false) ?? updated;
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
        };
    }
}
