using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.UnitTests.Snapshots;

internal sealed class SnapshotTestSandbox : IDisposable, IAsyncDisposable
{
    public string RootDirectory { get; }
    public string DataDirectory { get; }
    public SpawnPaths Paths { get; }
    public SpawnDatabase Database { get; }

    public SnapshotTestSandbox()
    {
        RootDirectory = Path.Combine(Path.GetTempPath(), $"spawn-snap-tests-{Guid.NewGuid():N}");
        DataDirectory = Path.Combine(RootDirectory, "data");
        Directory.CreateDirectory(DataDirectory);

        Paths = SpawnPaths.CreateIsolated(DataDirectory, workspaceRootHint: DataDirectory);
        Paths.EnsureDirectoriesExist();
        Database = new SpawnDatabase(Paths);
    }

    public string CreateSubDirectory(params string[] segments)
    {
        string combined = Path.Combine([RootDirectory, .. segments]);
        Directory.CreateDirectory(combined);
        return combined;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        for (int attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    DeleteTreeSafelyWithoutFollowingJunctions(RootDirectory);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(Math.Min(40 * (attempt + 1), 300));
            }
        }
    }

    public static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        string? parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return false;
        }

        try
        {
            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Process may have exited already.
        }

        proc.WaitForExit(5000);
        return proc.ExitCode == 0
               && Directory.Exists(linkPath)
               && (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0;
    }

    public static void CleanupTemporaryHkcuKey(string subKeyUnderCurrentUser)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKeyUnderCurrentUser, throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore cleanup failure.
        }

        try
        {
            // Removes Software\SPAWN-Test ONLY when completely empty (safe under parallel tests).
            Registry.CurrentUser.DeleteSubKey(@"Software\SPAWN-Test", throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore if sibling test still uses Software\SPAWN-Test.
        }
    }

    private static void DeleteTreeSafelyWithoutFollowingJunctions(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            return;
        }

        if ((File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(dirPath, recursive: false);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dirPath))
        {
            FileAttributes attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReparsePoint) == 0 && (attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }

            File.Delete(file);
        }

        foreach (string subDir in Directory.EnumerateDirectories(dirPath))
        {
            if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(subDir, recursive: false);
            }
            else
            {
                DeleteTreeSafelyWithoutFollowingJunctions(subDir);
            }
        }

        Directory.Delete(dirPath, recursive: false);
    }
}

internal sealed class ManualSnapshotTimeProvider(DateTimeOffset initialUtc) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtc;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}

internal sealed class StubSnapshotGameDataService : IGameDataManagementService
{
    private readonly Func<string, IReadOnlyList<GameDataEntryDto>> _entriesProvider;
    private readonly Func<string, string, GameDataSafetyCheckResult?>? _customValidator;

    public StubSnapshotGameDataService(
        Func<string, IReadOnlyList<GameDataEntryDto>> entriesProvider,
        Func<string, string, GameDataSafetyCheckResult?>? customValidator = null)
    {
        _entriesProvider = entriesProvider;
        _customValidator = customValidator;
    }

    public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_entriesProvider(gameId));

    public Task<IReadOnlyList<GameDataEntryDto>> EnumerateSaveLocationsAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken cancellationToken = default)
    {
        var all = _entriesProvider(gameId);
        IReadOnlyList<GameDataEntryDto> filtered = all
            .Where(e => e.Exists && e.IsSafeToClean && (e.Category == GameDataCategory.SaveData || e.Category == GameDataCategory.RegistryData))
            .ToArray();
        return Task.FromResult(filtered);
    }

    public Task<GameDataSafetyCheckResult> ValidateSaveRestoreTargetAsync(
        string gameId,
        string pathOrRegistryKey,
        CancellationToken cancellationToken = default)
    {
        if (_customValidator is not null)
        {
            var custom = _customValidator(gameId, pathOrRegistryKey);
            if (custom is not null)
            {
                return Task.FromResult(custom);
            }
        }

        // Delegate to frozen contract default validation logic.
        IGameDataManagementService iface = new DefaultContractDelegator(_entriesProvider);
        return iface.ValidateSaveRestoreTargetAsync(gameId, pathOrRegistryKey, cancellationToken);
    }

    public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataCleanResult.Ok("stub"));

    public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameDataUndoSnapshotDto?>(null);

    public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameDataCleanResult.Ok("stub"));

    private sealed class DefaultContractDelegator(Func<string, IReadOnlyList<GameDataEntryDto>> provider) : IGameDataManagementService
    {
        public Task<IReadOnlyList<GameDataEntryDto>> InspectGameDataAsync(string gameId, bool refreshOnlineRules = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(provider(gameId));

        public Task<GameDataCleanResult> CleanGameDataAsync(GameDataCleanRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("stub"));

        public Task<GameDataUndoSnapshotDto?> GetLatestUndoSnapshotAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GameDataUndoSnapshotDto?>(null);

        public Task<GameDataCleanResult> RestoreLastCleanupAsync(string gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(GameDataCleanResult.Ok("stub"));
    }
}

internal sealed class StubSnapshotGameLaunchService : IGameLaunchService
{
    private readonly Dictionary<string, ActiveGameSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ActiveGameSession> ActiveSessions => _sessions;

    public event EventHandler<ActiveGameSessionChangedEvent>? SessionChanged
    {
        add { }
        remove { }
    }

    public void SetRunning(ActiveGameSession session) => _sessions[session.GameId] = session;

    public void ClearRunning(string gameId) => _sessions.Remove(gameId);

    public ActiveGameSession? GetActiveSession(string gameId) =>
        _sessions.TryGetValue(gameId, out var session) ? session : null;

    public Task<GameLaunchResult> LaunchGameAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GameLaunchResult.Failure("STUB", "Not used in snapshot tests."));

    public Task<bool> StopGameAsync(string gameId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public void RegisterTrustedOverlayWindow(nint windowHandle, int processId)
    {
    }

    public void UnregisterTrustedOverlayWindow(nint windowHandle)
    {
    }
}
