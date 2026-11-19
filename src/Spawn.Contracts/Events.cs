namespace Spawn.Contracts;

/// <summary>
/// Lightweight in-process strongly-typed domain event bus decoupling SPAWN modules.
/// </summary>
public interface ISpawnEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : class;
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
    ValueTask PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken = default) where TEvent : class;
}

/// <summary>
/// Published by the Launcher module whenever a game is newly imported or rescanned.
/// </summary>
public sealed record GameImportedOrRescannedEvent(
    GameRecord Game,
    bool IsNewImport,
    DateTimeOffset OccurredUtc);

/// <summary>
/// Published when a game entry is removed from the SPAWN library.
/// </summary>
public sealed record GameRemovedEvent(
    string GameId,
    string Title,
    DateTimeOffset OccurredUtc);

/// <summary>
/// Published when a game session starts, transitions between Active/Background/AFK, or exits.
/// </summary>
public sealed record ActiveGameSessionChangedEvent(
    string GameId,
    ActiveGameSession? Session,
    PlaySessionActivityState CurrentState,
    PlaySessionRecord? CompletedSession = null,
    DateTimeOffset? OccurredUtc = null)
{
    public DateTimeOffset EffectiveOccurredUtc => OccurredUtc ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// Published when one or more achievements unlock (or are manually toggled/tested).
/// </summary>
public sealed record AchievementUnlockedNotification
{
    public required string GameId { get; init; }
    public required string GameTitle { get; init; }
    public required AchievementDefinitionDto Definition { get; init; }
    public required AchievementUnlockDto Unlock { get; init; }
    public int UnlockedCount { get; init; }
    public int TotalCount { get; init; }
    public bool IsAllAchievementsCompleted { get; init; }
    public bool SuppressOverlayToast { get; init; }
}

/// <summary>
/// Published when a game's achievement schema or local unlock state has been refreshed.
/// </summary>
public sealed record AchievementSchemaUpdatedEvent(
    string GameId,
    int UnlockedCount,
    int TotalCount,
    AchievementCaptureMode CaptureMode,
    string ChannelStatusText,
    DateTimeOffset OccurredUtc);
