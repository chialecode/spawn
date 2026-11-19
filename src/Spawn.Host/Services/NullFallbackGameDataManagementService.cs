using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Host.Services;

/// <summary>
/// Defensive fallback registered via <c>TryAddSingleton</c> only if a custom DI host omits the launcher data service.
/// In standard <c>Spawn.Host</c> startup, <c>Spawn.Modules.Launcher.GameDataManagementService</c> takes precedence.
/// </summary>
internal sealed class NullFallbackGameDataManagementService(
    SpawnDatabase db,
    IAchievementService achievementService) : IGameDataManagementService
{
    public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GameDataEntryDto>>([]);

    public async Task<GameDataCleanResult> CleanGameDataAsync(
        GameDataCleanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResetSpawnAchievementsAndPlaytime)
        {
            await db.ResetGameAchievementsAndPlaytimeAsync(request.GameId, cancellationToken).ConfigureAwait(false);
            await achievementService.ResetGameAchievementStateAsync(request.GameId, cancellationToken).ConfigureAwait(false);
            return GameDataCleanResult.Ok("已重置该游戏在 SPAWN 中的成就记录与游玩时长", spawnStateReset: true);
        }

        return GameDataCleanResult.Fail("游戏数据管理服务尚未注册");
    }

    public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(
        string gameId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<GameDataUndoSnapshotDto?>(null);

    public Task<GameDataCleanResult> RestoreLastCleanupAsync(
        string gameId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataCleanResult.Fail("当前没有可恢复的清理备份"));
}
