using System.Text.Json.Serialization;

namespace Spawn.Contracts;

/// <summary>
/// Logical classification of discovered per-game local files or registry keys.
/// </summary>
public enum GameDataCategory
{
    SaveData = 0,
    AchievementData = 1,
    ConfigAndLogs = 2,
    RegistryData = 3,
}

/// <summary>
/// Rule provider that identified a game data location.
/// </summary>
public enum GameDataSourceRule
{
    PCGamingWiki = 0,
    Ludusavi = 1,
    UnityEngine = 2,
    UnrealEngine = 3,
    SteamEmulator = 4,
    NativeRule = 5,
}

/// <summary>
/// Represents a single discovered save folder/file, offline achievement store, config/log path, or HKCU registry key for a game.
/// </summary>
public sealed record GameDataEntryDto
{
    public required string EntryId { get; init; }
    public string GameId { get; init; } = string.Empty;
    public required GameDataCategory Category { get; init; }
    public required GameDataSourceRule SourceRule { get; init; }
    public string? SourceLabel { get; init; }
    public required string PathOrRegistryKey { get; init; }
    public string? Description { get; init; }
    public bool Exists { get; init; }
    public int FileCount { get; init; }
    public long TotalBytes { get; init; }
    public bool IsSafeToClean { get; init; } = true;
    public string? SafetyNote { get; init; }
    public bool DefaultSelected { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }

    [JsonIgnore]
    public bool IsRegistryEntry =>
        Category == GameDataCategory.RegistryData
        || PathOrRegistryKey.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
        || PathOrRegistryKey.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string EffectiveSourceLabel =>
        !string.IsNullOrWhiteSpace(SourceLabel)
            ? SourceLabel!
            : SourceRule switch
            {
                GameDataSourceRule.PCGamingWiki => "PCGamingWiki",
                GameDataSourceRule.Ludusavi => "Ludusavi",
                GameDataSourceRule.UnityEngine => "Unity 引擎",
                GameDataSourceRule.UnrealEngine => "Unreal 引擎",
                GameDataSourceRule.SteamEmulator => "离线模拟器",
                _ => "原生存档",
            };
}

/// <summary>
/// Request payload for cleaning selected game data entries with optional reversible trash snapshot and SPAWN state reset.
/// </summary>
public sealed record GameDataCleanRequest
{
    public required string GameId { get; init; }
    public required IReadOnlyList<string> EntryIds { get; init; }
    public bool ResetSpawnAchievementsAndPlaytime { get; init; } = true;
    public bool CreateUndoBackup { get; init; } = true;
}

/// <summary>
/// Metadata describing a reversible cleanup backup stored under <c>SpawnPaths.TrashDir</c>.
/// </summary>
public sealed record GameDataUndoSnapshotDto
{
    public required string SnapshotId { get; init; }
    public required string GameId { get; init; }
    public required string BackupDirectory { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public int BackedUpEntryCount { get; init; }
    public int BackedUpFileCount { get; init; }
    public long TotalBytes { get; init; }
    public bool IncludedSpawnStateReset { get; init; }
    public string Status { get; init; } = "completed";
    public IReadOnlyList<string> BackedUpTargets { get; init; } = [];
}

/// <summary>
/// Result of manually purging one or all reversible cleanup trash snapshots for a game.
/// </summary>
public sealed record GameDataTrashPurgeResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public int DeletedSnapshotCount { get; init; }
    public long FreedBytes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static GameDataTrashPurgeResult Ok(
        string message,
        int deletedSnapshotCount = 0,
        long freedBytes = 0,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Succeeded = true,
            Message = message,
            DeletedSnapshotCount = deletedSnapshotCount,
            FreedBytes = freedBytes,
            Warnings = warnings ?? [],
        };

    public static GameDataTrashPurgeResult Fail(string message, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Succeeded = false,
            Message = message,
            Warnings = warnings ?? [],
        };
}

/// <summary>
/// Result of executing a game data cleanup or undo-restore operation.
/// </summary>
public sealed record GameDataCleanResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public int AffectedEntryCount { get; init; }
    public long TotalBytesProcessed { get; init; }
    public bool SpawnStateReset { get; init; }
    public GameDataUndoSnapshotDto? UndoSnapshot { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static GameDataCleanResult Ok(
        string message,
        int affectedEntryCount = 0,
        long totalBytesProcessed = 0,
        bool spawnStateReset = false,
        GameDataUndoSnapshotDto? undoSnapshot = null,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Succeeded = true,
            Message = message,
            AffectedEntryCount = affectedEntryCount,
            TotalBytesProcessed = totalBytesProcessed,
            SpawnStateReset = spawnStateReset,
            UndoSnapshot = undoSnapshot,
            Warnings = warnings ?? [],
        };

    public static GameDataCleanResult Fail(string message, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Succeeded = false,
            Message = message,
            Warnings = warnings ?? [],
        };
}

/// <summary>
/// Result of validating a save filesystem path or <c>HKCU</c> registry key against the game data safety fence.
/// </summary>
public sealed record GameDataSafetyCheckResult(
    bool IsSafe,
    string? Reason = null);

/// <summary>
/// Cached online game data location rule row persisted in SQLite (<c>game_data_rules</c>).
/// </summary>
public sealed record GameDataRuleCacheRecord
{
    public required string CacheKey { get; init; }
    public string? GameId { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }
    public string Provider { get; init; } = "PCGamingWiki";
    public required string RulesJson { get; init; }
    public DateTimeOffset FetchedUtc { get; init; } = DateTimeOffset.UtcNow;
}
