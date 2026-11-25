using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Resolves physically isolated runtime channels (<c>spawn</c>, <c>spawn-dev</c>, <c>spawn-dev-test</c>, or <c>SPAWN_DATA_DIR</c>)
/// and discovers optional local development workspace resources (<c>.local/games</c>, <c>.local/secrets</c>, and <c>dev-workspace.json</c>).
/// </summary>
public sealed class SpawnPaths
{
    public const string DataDirEnvVar = "SPAWN_DATA_DIR";
    public const string VaultDirEnvVar = "SPAWN_VAULT_DIR";
    public const string ChannelEnvVar = "SPAWN_CHANNEL";
    public const string IdentityEnvVar = "SPAWN_IDENTITY";
    public const string DevWorkspaceFileName = "dev-workspace.json";

    public string DataRoot { get; }
    public string VaultDirectory { get; }
    public string ChannelName { get; }
    public string Identity { get; }
    public bool IsDevelopment { get; }
    public bool IsIsolatedOverride { get; }

    public string DbDirectory => Path.Combine(DataRoot, "db");
    public string DatabaseFilePath => Path.Combine(DbDirectory, "spawn.db");
    public string ConfigDirectory => Path.Combine(DataRoot, "config");
    public string AntiCheatGuardStateFilePath => Path.Combine(ConfigDirectory, "anticheat-guard-state.json");
    public string UpdateRestartStateFilePath => Path.Combine(ConfigDirectory, "update-restart-state.json");
    public string SecretsDirectory => Path.Combine(DataRoot, "secrets");
    public string LogsDirectory => Path.Combine(DataRoot, "logs");
    public string ArtworkDirectory => Path.Combine(DataRoot, "artwork");
    public string CacheDirectory => Path.Combine(DataRoot, "cache");
    public string GbeForkCacheDirectory => Path.Combine(CacheDirectory, "gbe_fork");
    public string TrainerCacheDirectory => Path.Combine(CacheDirectory, "trainers");
    public string UpdatesCacheDirectory => Path.Combine(CacheDirectory, "updates");
    public string TrashDir => Path.Combine(DataRoot, "trash");
    public string TrashDirectory => TrashDir;
    public string DevWorkspaceConfigPath => Path.Combine(DataRoot, DevWorkspaceFileName);
    public string AchievementIconsDirectory => Path.Combine(VaultDirectory, "Schemas", "Icons");
    public string VaultChunksDirectory => Path.Combine(VaultDirectory, "Chunks");
    public string VaultSavesDirectory => Path.Combine(VaultDirectory, "Saves");
    public string SaveStagingDirectory => Path.Combine(VaultSavesDirectory, ".staging");
    public string TrainerSandboxDirectory => Path.Combine(VaultDirectory, "Sandbox", "Trainers");
    public string TrainerStagingDirectory => Path.Combine(TrainerSandboxDirectory, ".staging");
    public string PreTrainerSnapshotsDirectory => Path.Combine(VaultDirectory, "Snapshots", "PreTrainer");

    public string? LocalWorkspaceRoot { get; }
    public string? LocalGamesDirectory { get; }
    public string? LocalSecretsDirectory { get; }
    public string OverlayPipeName { get; }
    public string TrainerPipeNamePrefix { get; }

    public SpawnPaths(
        string? explicitDataDir = null,
        bool isProduction = false,
        string? workspaceRootHint = null,
        string? explicitIdentity = null)
    {
        string? envDataDir = string.IsNullOrWhiteSpace(explicitDataDir)
            ? Environment.GetEnvironmentVariable(DataDirEnvVar)
            : null;

        string resolvedChannel = SpawnBuildInfo.Channel;
        if (!isProduction && string.Equals(resolvedChannel, "release", StringComparison.OrdinalIgnoreCase))
        {
            isProduction = true;
        }

        string resolvedIdentity = !string.IsNullOrWhiteSpace(explicitIdentity)
            ? explicitIdentity.Trim()
            : SpawnBuildInfo.Identity;

        if (!isProduction && string.Equals(resolvedIdentity, "spawn", StringComparison.OrdinalIgnoreCase))
        {
            isProduction = true;
        }

        if (!string.IsNullOrWhiteSpace(explicitDataDir))
        {
            DataRoot = Path.GetFullPath(explicitDataDir.Trim());
            Identity = !string.IsNullOrWhiteSpace(explicitIdentity) ? explicitIdentity.Trim() : "spawn-test";
            ChannelName = Identity;
            IsDevelopment = !isProduction;
            IsIsolatedOverride = true;
            VaultDirectory = Path.Combine(DataRoot, "vault");
        }
        else if (!string.IsNullOrWhiteSpace(envDataDir))
        {
            DataRoot = Path.GetFullPath(envDataDir.Trim());
            Identity = !string.IsNullOrWhiteSpace(explicitIdentity)
                ? explicitIdentity.Trim()
                : (Environment.GetEnvironmentVariable(IdentityEnvVar)?.Trim() is { Length: > 0 } envId ? envId : "spawn-test");
            ChannelName = Identity;
            IsDevelopment = !isProduction;
            IsIsolatedOverride = true;

            string? envVaultDir = Environment.GetEnvironmentVariable(VaultDirEnvVar);
            VaultDirectory = !string.IsNullOrWhiteSpace(envVaultDir)
                ? Path.GetFullPath(envVaultDir.Trim())
                : Path.Combine(DataRoot, "vault");
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = Path.GetTempPath();
            }

            Identity = isProduction
                ? "spawn"
                : (string.IsNullOrWhiteSpace(resolvedIdentity) ? "spawn-dev" : resolvedIdentity);
            ChannelName = Identity;
            IsDevelopment = !isProduction;
            IsIsolatedOverride = false;
            DataRoot = Path.GetFullPath(Path.Combine(localAppData, Identity));

            string? envVaultDir = Environment.GetEnvironmentVariable(VaultDirEnvVar);
            VaultDirectory = !string.IsNullOrWhiteSpace(envVaultDir)
                ? Path.GetFullPath(envVaultDir.Trim())
                : Path.Combine(DataRoot, "vault");
        }

        if (IsDevelopment)
        {
            LocalWorkspaceRoot = DiscoverWorkspaceRoot(workspaceRootHint, DataRoot, IsIsolatedOverride);
            if (!string.IsNullOrWhiteSpace(LocalWorkspaceRoot))
            {
                string candidateGames = Path.Combine(LocalWorkspaceRoot, ".local", "games");
                if (Directory.Exists(candidateGames))
                {
                    LocalGamesDirectory = Path.GetFullPath(candidateGames);
                }

                string candidateSecrets = Path.Combine(LocalWorkspaceRoot, ".local", "secrets");
                if (Directory.Exists(candidateSecrets))
                {
                    LocalSecretsDirectory = Path.GetFullPath(candidateSecrets);
                }
            }
        }

        string ipcPrefix = ComputeIpcSessionPrefix();
        OverlayPipeName = $"{ipcPrefix}-overlay";
        TrainerPipeNamePrefix = $"{ipcPrefix}-trainer-";
    }

    public static SpawnPaths Resolve(
        bool isProduction = false,
        string? workspaceRootHint = null,
        string? explicitIdentity = null) =>
        new(
            explicitDataDir: null,
            isProduction: isProduction,
            workspaceRootHint: workspaceRootHint,
            explicitIdentity: explicitIdentity);

    public static SpawnPaths CreateIsolated(string rootDirectory, string? workspaceRootHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return new SpawnPaths(explicitDataDir: rootDirectory, isProduction: false, workspaceRootHint: workspaceRootHint);
    }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DbDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ArtworkDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(GbeForkCacheDirectory);
        Directory.CreateDirectory(TrainerCacheDirectory);
        Directory.CreateDirectory(UpdatesCacheDirectory);
        Directory.CreateDirectory(TrashDir);
        Directory.CreateDirectory(VaultDirectory);
        Directory.CreateDirectory(AchievementIconsDirectory);
        Directory.CreateDirectory(VaultChunksDirectory);
        Directory.CreateDirectory(VaultSavesDirectory);
        Directory.CreateDirectory(SaveStagingDirectory);
        Directory.CreateDirectory(TrainerSandboxDirectory);
        Directory.CreateDirectory(TrainerStagingDirectory);
        Directory.CreateDirectory(PreTrainerSnapshotsDirectory);
    }

    /// <summary>
    /// Resolves the two-level content-addressed shard path (<c>vault\Chunks\aa\bb\&lt;blake3&gt;.zst</c>)
    /// for a validated 64-character hexadecimal BLAKE3 chunk hash.
    /// </summary>
    public string GetCasChunkPath(string chunkBlake3Hex, bool createParentDirectory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkBlake3Hex);
        string normalized = chunkBlake3Hex.Trim().ToLowerInvariant();
        if (normalized.Length != 64)
        {
            throw new ArgumentException("BLAKE3 chunk hash must be 64 hexadecimal characters.", nameof(chunkBlake3Hex));
        }

        for (int i = 0; i < normalized.Length; i++)
        {
            char c = normalized[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                throw new ArgumentException("BLAKE3 chunk hash must contain only lowercase hexadecimal characters.", nameof(chunkBlake3Hex));
            }
        }

        string shardDir = Path.Combine(VaultChunksDirectory, normalized[..2], normalized[2..4]);
        if (createParentDirectory)
        {
            Directory.CreateDirectory(shardDir);
        }

        return Path.Combine(shardDir, normalized + ".zst");
    }

    public string GetGameArtworkDirectory(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        string safeId = SanitizePathSegment(gameId);
        string dir = Path.Combine(ArtworkDirectory, safeId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetGameTrashDirectory(string gameId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        string safeId = SanitizePathSegment(gameId);
        string dir = Path.Combine(TrashDir, safeId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetAchievementIconDirectory(string schemaKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaKey);
        string safeKey = SanitizePathSegment(schemaKey);
        string dir = Path.Combine(AchievementIconsDirectory, safeKey);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> contains only printable 7-bit ASCII characters.
    /// </summary>
    public static bool IsPureAsciiPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (char ch in path)
        {
            if (ch < 0x20 || ch > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the active trainer sandbox root directory, defaulting to <see cref="TrainerSandboxDirectory"/>
    /// or validating an optional user-configured pure-ASCII custom root.
    /// </summary>
    public string ResolveTrainerSandboxDirectory(string? customSandboxRoot = null, bool requirePureAscii = true)
    {
        if (string.IsNullOrWhiteSpace(customSandboxRoot))
        {
            return TrainerSandboxDirectory;
        }

        string trimmed = customSandboxRoot.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Custom trainer sandbox root must be a fully qualified path.", nameof(customSandboxRoot));
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (requirePureAscii && !IsPureAsciiPath(normalized))
        {
            throw new ArgumentException("Custom trainer sandbox root must contain only pure ASCII characters.", nameof(customSandboxRoot));
        }

        return normalized;
    }

    public string GetTrainerStagingDirectory(string? customSandboxRoot = null, bool requirePureAscii = true) =>
        Path.Combine(ResolveTrainerSandboxDirectory(customSandboxRoot, requirePureAscii), ".staging");

    public string GetTrainerInstallDirectory(
        string gameId,
        string trainerId,
        string? customSandboxRoot = null,
        bool createDirectory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trainerId);

        string sandboxRoot = ResolveTrainerSandboxDirectory(customSandboxRoot);
        string dir = Path.Combine(sandboxRoot, SanitizePathSegment(gameId), SanitizePathSegment(trainerId));
        if (createDirectory)
        {
            Directory.CreateDirectory(dir);
        }

        return dir;
    }

    public string GetPreTrainerSnapshotDirectory(string gameId, string snapshotId, bool createDirectory = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        string dir = Path.Combine(PreTrainerSnapshotsDirectory, SanitizePathSegment(gameId), SanitizePathSegment(snapshotId));
        if (createDirectory)
        {
            Directory.CreateDirectory(dir);
        }

        return dir;
    }

    /// <summary>
    /// Generates a unique single-session Named Pipe name for <c>Spawn.Host</c> ↔ <c>Spawn.TrainerWorker</c>
    /// carrying a 128-bit CSPRNG hexadecimal suffix.
    /// </summary>
    public string CreateTrainerSessionPipeName()
    {
        Span<byte> randomBytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(randomBytes);
        return $"{TrainerPipeNamePrefix}{Convert.ToHexStringLower(randomBytes)}";
    }

    /// <summary>
    /// Returns candidate default game library scan directories (includes <c>.local/games</c> in development mode when present).
    /// </summary>
    public IReadOnlyList<string> GetDefaultScanDirectories()
    {
        if (IsDevelopment && !string.IsNullOrWhiteSpace(LocalGamesDirectory) && Directory.Exists(LocalGamesDirectory))
        {
            return [LocalGamesDirectory];
        }

        return [];
    }

    public static string? TryReadDevWorkspaceFile(string devWorkspaceJsonPath)
    {
        if (string.IsNullOrWhiteSpace(devWorkspaceJsonPath) || !File.Exists(devWorkspaceJsonPath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(devWorkspaceJsonPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                string? rawStr = doc.RootElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(rawStr) && Directory.Exists(rawStr))
                {
                    return Path.GetFullPath(rawStr);
                }

                return null;
            }

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string[] acceptedKeys =
            [
                "workspaceRoot",
                "workspace_root",
                "repoRoot",
                "repo_root",
                "repositoryRoot",
                "workspacePath",
                "workspaceDir",
                "root",
                "path",
            ];

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (acceptedKeys.Any(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    string? candidate = prop.Value.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed optional dev-workspace.json.
        }

        return null;
    }

    public static string? DiscoverLocalFileUpwards(string relativeSubPath, string? startDirHint = null)
    {
        foreach (string seed in GetDiscoverySeeds(startDirHint))
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, relativeSubPath);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static string? DiscoverWorkspaceRoot(string? workspaceRootHint, string dataRoot, bool isIsolatedOverride)
    {
        // 1. Explicit caller hint takes top precedence.
        if (!string.IsNullOrWhiteSpace(workspaceRootHint) && Directory.Exists(workspaceRootHint))
        {
            string? fromHint = WalkUpForWorkspaceMarker(workspaceRootHint) ?? Path.GetFullPath(workspaceRootHint);
            if (!string.IsNullOrWhiteSpace(fromHint))
            {
                return fromHint;
            }
        }

        // 2. Bridge file inside active DataRoot (e.g., %LOCALAPPDATA%\spawn-dev\dev-workspace.json written by install-dev).
        string dataRootBridge = Path.Combine(dataRoot, DevWorkspaceFileName);
        string? bridgedRoot = TryReadDevWorkspaceFile(dataRootBridge);
        if (!string.IsNullOrWhiteSpace(bridgedRoot))
        {
            return bridgedRoot;
        }

        // 3. Upward walk from CWD and AppContext.BaseDirectory (source runs and unit tests).
        foreach (string seed in GetDiscoverySeeds(hint: null))
        {
            string? walked = WalkUpForWorkspaceMarker(seed);
            if (!string.IsNullOrWhiteSpace(walked))
            {
                return walked;
            }
        }

        // 4. Non-isolated dev fallback: check standard %LOCALAPPDATA%\spawn-dev\dev-workspace.json if custom dev identity is used.
        if (!isIsolatedOverride)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                string sharedDevBridge = Path.Combine(localAppData, "spawn-dev", DevWorkspaceFileName);
                string? fallbackBridged = TryReadDevWorkspaceFile(sharedDevBridge);
                if (!string.IsNullOrWhiteSpace(fallbackBridged))
                {
                    return fallbackBridged;
                }
            }
        }

        return null;
    }

    private static string? WalkUpForWorkspaceMarker(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".local"))
                || File.Exists(Path.Combine(dir.FullName, "Spawn.sln"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static IEnumerable<string> GetDiscoverySeeds(string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && Directory.Exists(hint))
        {
            yield return Path.GetFullPath(hint);
        }

        string cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
        {
            yield return Path.GetFullPath(cwd);
        }

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
        {
            yield return Path.GetFullPath(baseDir);
        }
    }

    private string ComputeIpcSessionPrefix()
    {
        int sessionId;
        try
        {
            using var current = Process.GetCurrentProcess();
            sessionId = current.SessionId;
        }
        catch
        {
            sessionId = 1;
        }

        if (IsIsolatedOverride)
        {
            ulong hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(DataRoot.ToUpperInvariant()));
            return $"spawn-test-ipc-{sessionId}-{hash:x16}";
        }

        return $"{ChannelName}-ipc-{sessionId}";
    }

    private static string SanitizePathSegment(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (ch == ':' || Array.IndexOf(invalid, ch) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : "default";
    }
}
