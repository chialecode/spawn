using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Game identity used by <see cref="GameDataSafetyFence"/> to decide whether a path outside the install directory is
/// game-specific (title / AppID / GOG ID / engine product name / executable name).
/// </summary>
public sealed record GameDataTargetIdentity
{
    public string? Title { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }

    /// <summary>Fuzzy-matchable names (title, Unity <c>productName</c>, Unreal project name).</summary>
    public IReadOnlyList<string> ProductNames { get; init; } = [];

    /// <summary>Exact-match-only names (executable base name, install folder name) that may be short or generic.</summary>
    public IReadOnlyList<string> ExactNames { get; init; } = [];

    public static GameDataTargetIdentity FromGame(GameRecord game, IEnumerable<string?>? extraProductNames = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var products = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.Title))
        {
            products.Add(game.Title);
        }

        foreach (string? extra in extraProductNames ?? [])
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                products.Add(extra);
            }
        }

        var exact = new List<string>();
        string exeBase = Path.GetFileNameWithoutExtension(game.ExecutableRelPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(exeBase))
        {
            exact.Add(exeBase);
        }

        string dirLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(dirLeaf))
        {
            exact.Add(dirLeaf);
        }

        return new GameDataTargetIdentity
        {
            Title = game.Title,
            SteamAppId = game.SteamAppId,
            GogGameId = game.GogGameId,
            ProductNames = products,
            ExactNames = exact,
        };
    }
}

/// <summary>
/// Ironclad safety fence preventing accidental deletion of game installation roots, executables/DLLs,
/// engine asset directories, SPAWN's own database/vault, OS system roots, or <c>HKLM</c> registry hives.
/// </summary>
public static class GameDataSafetyFence
{
    private static readonly HashSet<string> ForbiddenFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".cdx",
        ".rne",
        ".sys",
        ".pak",
        ".utoc",
        ".ucas",
        ".bundle",
        ".assets",
        ".resS",
        ".resource",
        ".pkg",
        ".rpf",
        ".pck",
        ".wad",
        ".bsa",
        ".ba2",
        ".zip",
        ".info",
        ".hashdb",
    };

    private static readonly HashSet<string> AllowedInGameSubfolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "save_game",
        "saves",
        "save",
        "savedata",
        "userdata",
        "user_data",
        "SteamData",
        "Profile",
        "3DMGAME",
        "Logs",
    };

    private static readonly HashSet<string> AllowedInGameRootFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "savegame.txt",
        "settings.txt",
        "user_stats.ini",
        "config.ini",
        "options.ini",
    };

    private static readonly HashSet<string> ProtectedRegistryVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft",
        "Windows",
        "Classes",
        "Policies",
        "RegisteredApplications",
        "Wow6432Node",
    };

    /// <summary>
    /// Validates whether a filesystem path is safe for reversible game-data cleanup.
    /// </summary>
    public static (bool IsSafe, string? Reason) ValidateFilesystemTarget(
        string candidatePath,
        string gameInstallDir,
        GameDataTargetIdentity identity,
        SpawnPaths spawnPaths,
        GameDataManagementOptions options)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return (false, "目标路径为空。");
        }

        string fullTarget;
        string fullInstallDir;
        try
        {
            fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            fullInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameInstallDir));
        }
        catch
        {
            return (false, "目标路径格式非法。");
        }

        // 1. Never allow drive roots (e.g., C:\, D:\) or very shallow top-level OS paths.
        string? root = Path.GetPathRoot(fullTarget);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(fullTarget, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "严禁清理磁盘根目录。");
        }

        int depthSegments = fullTarget.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depthSegments < 3)
        {
            return (false, "路径层级过浅，已触发系统防误删围栏。");
        }

        // 2. Never allow deleting the game installation root itself!
        if (string.Equals(fullTarget, fullInstallDir, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "安全围栏拦截：严禁删除游戏安装根目录本体！");
        }

        // 3. Never allow deleting parent directories of gameInstallDir or SPAWN's own database/vault!
        if (IsSameOrParentDirectory(fullTarget, fullInstallDir))
        {
            return (false, "安全围栏拦截：目标路径包含游戏安装目录！");
        }

        string spawnDataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(spawnPaths.DataRoot));
        if (IsSameOrParentDirectory(fullTarget, spawnDataRoot))
        {
            return (false, "安全围栏拦截：严禁清理 SPAWN 数据根目录！");
        }

        string[] protectedSpawnSubdirs =
        [
            spawnPaths.DbDirectory,
            spawnPaths.VaultDirectory,
            spawnPaths.ConfigDirectory,
            spawnPaths.SecretsDirectory,
            spawnPaths.ArtworkDirectory,
            spawnPaths.LogsDirectory,
            spawnPaths.CacheDirectory,
        ];

        foreach (string sub in protectedSpawnSubdirs)
        {
            string normSub = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sub));
            if (string.Equals(fullTarget, normSub, StringComparison.OrdinalIgnoreCase)
                || fullTarget.StartsWith(normSub + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "安全围栏拦截：严禁清理 SPAWN 自身数据库或核心运行目录。");
            }
        }

        // 4. Never allow deleting base user special folders directly (%APPDATA%, %LOCALAPPDATA%, LocalLow, Documents, Public).
        string[] protectedSpecialRoots =
        [
            options.ResolveAppDataRoaming(),
            options.ResolveLocalAppData(),
            options.ResolveLocalLow(),
            options.ResolvePublicDocuments(),
            options.ResolvePublicRoot(),
            options.ResolveUserDocuments(),
            options.ResolveUserProfile(),
            options.ResolveSavedGames(),
            options.ResolveProgramData(),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];

        foreach (string special in protectedSpecialRoots)
        {
            if (string.IsNullOrWhiteSpace(special))
            {
                continue;
            }

            string normSpecial = Path.TrimEndingDirectorySeparator(Path.GetFullPath(special));
            if (string.Equals(fullTarget, normSpecial, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "安全围栏拦截：严禁清理系统级共享根文件夹。");
            }
        }

        // Also block shared Steam emulator parent roots (e.g. ...\Steam\CODEX without <AppId>).
        string leafName = Path.GetFileName(fullTarget);
        if (leafName.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("CODEX", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("RUNE", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("Goldberg SteamEmu Saves", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("GSE Saves", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "安全围栏拦截：严禁清理跨游戏共享模拟器总目录。");
        }

        // 5. Never allow deleting executable binaries, DLLs, or packed game archives anywhere.
        string ext = Path.GetExtension(fullTarget);
        if (!string.IsNullOrWhiteSpace(ext) && ForbiddenFileExtensions.Contains(ext))
        {
            return (false, $"安全围栏拦截：禁止删除可执行文件或核心资源包 ({ext})。");
        }

        // 6. Strict whitelist inside <GameInstallDir>: only allow explicit save/profile subfolders or safe config files!
        string installPrefix = fullInstallDir + Path.DirectorySeparatorChar;
        if (fullTarget.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relPath = Path.GetRelativePath(fullInstallDir, fullTarget);
            string[] relSegments = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relSegments.Length == 0)
            {
                return (false, "安全围栏拦截：非法游戏内相对路径。");
            }

            string topSegment = relSegments[0];

            // Explicitly block Unity *_Data, Unreal Engine/Content/Binaries, steam_settings, etc.
            if (topSegment.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Content", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("steam_settings", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("data", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("titledata", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("lang", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"安全围栏拦截：禁止清理游戏核心引擎/数据目录 ({topSegment})。");
            }

            if (AllowedInGameSubfolders.Contains(topSegment))
            {
                return (true, null);
            }

            if (relSegments.Length == 1 && !Directory.Exists(fullTarget) && AllowedInGameRootFileNames.Contains(topSegment))
            {
                return (true, null);
            }

            return relSegments.Length == 1 && !Directory.Exists(fullTarget)
                ? (false, $"安全围栏拦截：游戏根目录下仅允许清理白名单存档/配置文件，已保护 {topSegment}。")
                : (false, $"安全围栏拦截：游戏目录内仅允许清理明确白名单存档子目录（如 save_game / SteamData / Profile），已保护 {topSegment}。");
        }

        // 7. Outside <GameInstallDir>: strict allow-list. The target must live under a known per-user / per-machine data root
        //    AND contain at least one game-specific segment (title, AppID, GOG ID, engine product, Vendor\Game) below that root.
        //    Shared parents such as Documents\My Games, AppData\Roaming, AppData\LocalLow\<Vendor> are never cleanable.
        return ValidateOutsideInstallTarget(fullTarget, identity, options);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fullPath"/> sits under a known data root and contains at least
    /// one game-specific segment below it. Used both by the fence and by wildcard expansion so that globs can never widen
    /// into shared parent directories.
    /// </summary>
    public static bool IsGameSpecificPathOutsideInstall(string fullPath, GameDataTargetIdentity identity, GameDataManagementOptions options) =>
        ValidateOutsideInstallTarget(fullPath, identity, options).IsSafe;

    private static (bool IsSafe, string? Reason) ValidateOutsideInstallTarget(
        string fullTarget,
        GameDataTargetIdentity identity,
        GameDataManagementOptions options)
    {
        if (fullTarget.Contains('*', StringComparison.Ordinal) || fullTarget.Contains('?', StringComparison.Ordinal))
        {
            return (false, "安全围栏拦截：清理目标不得包含通配符。");
        }

        string? matchedRoot = null;
        foreach (string root in EnumerateKnownDataRoots(options))
        {
            if (fullTarget.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && (matchedRoot is null || root.Length > matchedRoot.Length))
            {
                matchedRoot = root;
            }
        }

        if (matchedRoot is null)
        {
            return (false, "安全围栏拦截：安装目录以外仅允许清理 AppData / LocalLow / 文档 / Saved Games / ProgramData 等已知数据根下的游戏专属路径。");
        }

        string relative = fullTarget[(matchedRoot.Length + 1)..];
        string[] segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (!segments.Any(seg => IsGameSpecificSegment(seg, identity)))
        {
            return (false, $"安全围栏拦截：目标路径不含游戏专属片段（标题 / AppID / 厂商\\游戏），已保护共享目录 {relative}。");
        }

        return (true, null);
    }

    private static IEnumerable<string> EnumerateKnownDataRoots(GameDataManagementOptions options)
    {
        string?[] raw =
        [
            options.ResolveAppDataRoaming(),
            options.ResolveLocalAppData(),
            options.ResolveLocalLow(),
            options.ResolvePublicDocuments(),
            options.ResolveUserDocuments(),
            options.ResolveSavedGames(),
            options.ResolveProgramData(),
            options.ResolveUserProfile(),
            options.ResolveSteamInstallDir(),
        ];

        foreach (string? r in raw)
        {
            if (string.IsNullOrWhiteSpace(r))
            {
                continue;
            }

            string normalized;
            try
            {
                normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            yield return normalized;
        }
    }

    internal static bool IsGameSpecificSegment(string segment, GameDataTargetIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string trimmed = segment.Trim();
        if (identity.SteamAppId is > 0
            && string.Equals(trimmed, identity.SteamAppId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(identity.GogGameId) && string.Equals(trimmed, identity.GogGameId.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        string norm = NormalizeName(trimmed);
        if (norm.Length < 3 || GenericSegmentNames.Contains(norm))
        {
            return false;
        }

        foreach (string exact in identity.ExactNames)
        {
            string ne = NormalizeName(exact);
            if (ne.Length >= 3 && !GenericSegmentNames.Contains(ne) && string.Equals(norm, ne, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (string product in identity.ProductNames)
        {
            string np = NormalizeName(product);
            if (np.Length < 3 || GenericSegmentNames.Contains(np))
            {
                continue;
            }

            if (string.Equals(norm, np, StringComparison.Ordinal))
            {
                return true;
            }

            // Segment embeds the full product name (e.g. "LimboSaves" for "Limbo" requires >= 6 chars to avoid noise).
            if (np.Length >= 6 && norm.Contains(np, StringComparison.Ordinal))
            {
                return true;
            }

            // Product name with decorations (e.g. "LIMBO (GOG)") still matches its core folder ("Limbo") when the
            // segment covers most of the name.
            if (norm.Length >= 5 && np.Contains(norm, StringComparison.Ordinal) && norm.Length >= np.Length * 0.6)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeName(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static readonly HashSet<string> GenericSegmentNames = new(StringComparer.Ordinal)
    {
        "game", "games", "mygames", "savedgames", "savegames", "savegame", "saves", "save", "savedata", "saved",
        "steam", "steamapps", "common", "userdata", "user", "users", "data", "config", "configs", "settings",
        "local", "locallow", "roaming", "appdata", "documents", "public", "programdata", "launcher", "bin",
        "win64", "win32", "x64", "x86", "binaries", "shipping", "client", "app", "application", "remote",
        "codex", "rune", "empress", "skidrow", "hoodlum", "gsesaves", "goldbergsteamemusaves", "smartsteamemu",
        "onlinefix", "valve", "ali213", "temp", "cache", "logs", "unity", "unrealengine", "engine",
    };

    /// <summary>
    /// Validates whether a Windows Registry key path is safe for reversible cleanup.
    /// Strictly requires <c>HKCU\Software\&lt;Vendor&gt;\&lt;Game&gt;</c> (depth &gt;= 3 under <c>HKCU</c>) and blocks <c>HKLM</c>.
    /// </summary>
    public static (bool IsSafe, string? SubKeyUnderCurrentUser, string? Reason) ValidateRegistryTarget(
        string registryKeyPath,
        GameDataManagementOptions options)
    {
        if (string.IsNullOrWhiteSpace(registryKeyPath))
        {
            return (false, null, "注册表路径为空。");
        }

        string normalized = registryKeyPath.Trim().Replace('/', '\\').TrimEnd('\\');
        if (normalized.StartsWith(@"HKEY_CURRENT_USER\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"HKCU\" + normalized[@"HKEY_CURRENT_USER\".Length..];
        }
        else if (normalized.StartsWith(@"HKEY_LOCAL_MACHINE\", StringComparison.OrdinalIgnoreCase))
        {
            normalized = @"HKLM\" + normalized[@"HKEY_LOCAL_MACHINE\".Length..];
        }

        if (!normalized.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "安全围栏拦截：仅允许管理当前用户 HKCU 注册表项，严禁修改 HKLM 系统注册表。");
        }

        string subKey = normalized[@"HKCU\".Length..];
        string requiredPrefix = (string.IsNullOrWhiteSpace(options.HkcuSoftwarePrefix) ? "Software" : options.HkcuSoftwarePrefix.Trim('\\')) + @"\";
        if (!subKey.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, $"安全围栏拦截：注册表路径必须位于 HKCU\\{requiredPrefix} 下。");
        }

        string remainder = subKey[requiredPrefix.Length..];
        string[] vendorAndGame = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (vendorAndGame.Length < 2)
        {
            return (false, null, "安全围栏拦截：HKCU\\Software 注册表深度不足（至少需包含 <开发商>\\<游戏名> 两级子键）。");
        }

        string vendor = vendorAndGame[0];
        if (ProtectedRegistryVendors.Contains(vendor))
        {
            return (false, null, $"安全围栏拦截：禁止清理系统保护命名空间 HKCU\\Software\\{vendor}。");
        }

        if (vendor.Equals("Valve", StringComparison.OrdinalIgnoreCase)
            && vendorAndGame[1].Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && vendorAndGame.Length < 4)
        {
            return (false, null, "安全围栏拦截：禁止清理 Steam 客户端全局注册表根键。");
        }

        return (true, subKey, null);
    }

    private static bool IsSameOrParentDirectory(string potentialParent, string childPath)
    {
        if (string.Equals(potentialParent, childPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return childPath.StartsWith(potentialParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
