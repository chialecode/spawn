using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Hard-redline safety fence for trusted Ludusavi rules and user-configured custom save locations.
/// Relaxes heuristic directory segment-length (&gt;= 3) and the fixed 10-folder in-game whitelist
/// (allowing paths such as <c>b1/Saved/SaveGames</c>, <c>preferences</c>, <c>runs</c>, <c>SNAppData</c>),
/// while strictly blocking drive roots, OS system directories, NTFS ReparsePoints/Junctions,
/// executables/scripts, SPAWN's own data root, game installation roots, core engine subdirectories,
/// and <c>HKLM</c> registry hives.
/// </summary>
public static class TrustedSavePathFence
{
    private static readonly HashSet<string> ForbiddenExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs",
        ".msi",
        ".com",
        ".scr",
        ".pif",
        ".reg",
        ".sh",
        ".sys",
    };

    private static readonly HashSet<string> ForbiddenTargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".dll",
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs",
        ".msi",
        ".com",
        ".scr",
        ".pif",
        ".reg",
        ".sh",
        ".sys",
        ".cdx",
        ".rne",
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
        ".info",
        ".hashdb",
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
    /// Returns <see langword="true"/> when <paramref name="filePath"/> has an executable or script extension
    /// that must never be captured or overwritten as a save file.
    /// </summary>
    public static bool IsForbiddenExecutableFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);
        return !string.IsNullOrWhiteSpace(ext) && ForbiddenExecutableExtensions.Contains(ext);
    }

    /// <summary>
    /// Returns <see langword="true"/> when a rejection message from <c>GameDataSafetyFence.ValidateFilesystemTarget</c>
    /// is solely due to the strict heuristic whitelist (fixed 10 in-game subfolders, game-specific 3-char segment check,
    /// or outside-known-data-root check), rather than a hard security redline.
    /// </summary>
    public static bool IsHeuristicStrictFenceRejection(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        return reason.Contains("仅允许清理明确白名单存档子目录", StringComparison.Ordinal)
               || reason.Contains("游戏根目录下仅允许清理白名单", StringComparison.Ordinal)
               || reason.Contains("目标路径不含游戏专属片段", StringComparison.Ordinal)
               || reason.Contains("安装目录以外仅允许清理", StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates a filesystem target originating from a matched Ludusavi rule or user-configured custom location.
    /// </summary>
    public static (bool IsSafe, string? Reason) ValidateTrustedFilesystemTarget(
        string candidatePath,
        string? gameInstallDir,
        IKnownFolderProvider? knownFolders = null,
        string? spawnDataRoot = null,
        bool isUserCustomPath = false)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return (false, "目标路径为空。");
        }

        if (candidatePath.Contains('*', StringComparison.Ordinal) || candidatePath.Contains('?', StringComparison.Ordinal))
        {
            return (false, "安全围栏拦截：目标路径不得包含通配符。");
        }

        string fullTarget;
        try
        {
            fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath.Trim()));
        }
        catch
        {
            return (false, "目标路径格式非法。");
        }

        string? root = Path.GetPathRoot(fullTarget);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(fullTarget, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "安全围栏拦截：严禁指向磁盘根目录。");
        }

        IKnownFolderProvider folders = knownFolders ?? WindowsKnownFolderResolver.Instance;

        // 1. OS Windows directory and all its descendants.
        string[] windowsCandidates =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            folders.WinDir,
        ];
        foreach (string winDir in windowsCandidates)
        {
            if (TryNormalizePath(winDir, out string normWin)
                && (IsSameOrParentDirectory(normWin, fullTarget) || IsSameOrParentDirectory(fullTarget, normWin)))
            {
                return (false, "安全围栏拦截：严禁访问 Windows 系统目录。");
            }
        }

        // 2. Protected volume / OS root children.
        string[] segments = fullTarget.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && IsProtectedRootChild(segments[1]))
        {
            return (false, $"安全围栏拦截：严禁访问系统保护目录 ({segments[1]})。");
        }

        // 3. SPAWN's own data root, parents, and descendants.
        string? resolvedSpawnRoot = spawnDataRoot;
        if (string.IsNullOrWhiteSpace(resolvedSpawnRoot))
        {
            try
            {
                resolvedSpawnRoot = SpawnPaths.Resolve().DataRoot;
            }
            catch
            {
                resolvedSpawnRoot = null;
            }
        }

        if (TryNormalizePath(resolvedSpawnRoot, out string normSpawnRoot)
            && (IsSameOrParentDirectory(fullTarget, normSpawnRoot) || IsSameOrParentDirectory(normSpawnRoot, fullTarget)))
        {
            return (false, "安全围栏拦截：严禁指向 SPAWN 自身数据或数据库目录。");
        }

        // 4. Game install root itself or any parent directory containing it.
        string? fullInstallDir = TryNormalizePath(gameInstallDir, out string normInstall) ? normInstall : null;
        if (!string.IsNullOrWhiteSpace(fullInstallDir))
        {
            if (string.Equals(fullTarget, fullInstallDir, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "安全围栏拦截：严禁指向游戏安装根目录本体！");
            }

            if (IsSameOrParentDirectory(fullTarget, fullInstallDir))
            {
                return (false, "安全围栏拦截：目标路径包含游戏安装目录！");
            }
        }

        // 5. Base system/user special folder roots (exact match).
        string? docs = folders.WinDocuments;
        string? sysRoot = Path.GetPathRoot(Environment.SystemDirectory);
        string?[] protectedSpecialRoots =
        [
            folders.WinAppData,
            folders.WinLocalAppData,
            folders.WinLocalAppDataLow,
            folders.WinPublic,
            string.IsNullOrWhiteSpace(folders.WinPublic) ? null : Path.Combine(folders.WinPublic, "Documents"),
            docs,
            string.IsNullOrWhiteSpace(docs) ? null : Path.Combine(docs, "My Games"),
            folders.Home,
            string.IsNullOrWhiteSpace(folders.Home) ? null : Path.Combine(folders.Home, "Saved Games"),
            folders.WinProgramData,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            string.IsNullOrWhiteSpace(sysRoot) ? null : Path.Combine(sysRoot, "Users"),
        ];

        foreach (string? special in protectedSpecialRoots)
        {
            if (TryNormalizePath(special, out string normSpecial)
                && string.Equals(fullTarget, normSpecial, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "安全围栏拦截：严禁指向系统或用户共享根文件夹。");
            }
        }

        // 6. Cross-game shared roots.
        string leafName = Path.GetFileName(fullTarget);
        if (leafName.Equals("Steam", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("userdata", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("CODEX", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("RUNE", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("Goldberg SteamEmu Saves", StringComparison.OrdinalIgnoreCase)
            || leafName.Equals("GSE Saves", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "安全围栏拦截：严禁指向跨游戏共享总目录。");
        }

        // 7. Executable / script / core engine archive extensions.
        string ext = Path.GetExtension(fullTarget);
        if (!string.IsNullOrWhiteSpace(ext) && ForbiddenTargetExtensions.Contains(ext))
        {
            return (false, $"安全围栏拦截：禁止读写可执行文件、脚本或核心资源包 ({ext})。");
        }

        // 8. NTFS ReparsePoint / Junction / SymbolicLink check on target and existing ancestors.
        if (HasDisallowedReparsePointOnPath(fullTarget, out string? reparsePath))
        {
            return (false, $"安全围栏拦截：禁止指向 NTFS 联接点或符号链接 ({reparsePath})。");
        }

        // 9. Core engine directories inside game install folder.
        if (!string.IsNullOrWhiteSpace(fullInstallDir)
            && fullTarget.StartsWith(fullInstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string relPath = Path.GetRelativePath(fullInstallDir, fullTarget);
            string[] relSegments = relPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relSegments.Length == 0)
            {
                return (false, "安全围栏拦截：非法游戏内相对路径。");
            }

            string topSegment = relSegments[0];
            if (topSegment.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("Content", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase)
                || topSegment.Equals("steam_settings", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"安全围栏拦截：禁止将游戏核心引擎目录 ({topSegment}) 作为存档目录。");
            }

            if (relSegments.Any(static s =>
                    s.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("MonoBleedingEdge", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "安全围栏拦截：禁止将引擎二进制目录作为存档目录。");
            }

            return (true, null);
        }

        // 10. Minimum path depth guard outside install folder.
        int minDepth = isUserCustomPath ? 2 : 3;
        if (segments.Length < minDepth)
        {
            return (false, "路径层级过浅，已触发系统防误删围栏。");
        }

        return (true, null);
    }

    /// <summary>
    /// Validates a Windows Registry save target, strictly enforcing <c>HKCU\Software\&lt;Vendor&gt;\&lt;Game&gt;</c>
    /// and blocking <c>HKLM</c> or OS protected namespaces.
    /// </summary>
    public static (bool IsSafe, string? CanonicalKey, string? Reason) ValidateTrustedRegistryTarget(string? rawRegistryKey)
    {
        if (string.IsNullOrWhiteSpace(rawRegistryKey))
        {
            return (false, null, "注册表路径为空。");
        }

        string normalized = rawRegistryKey.Trim().Replace('/', '\\').TrimEnd('\\');
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
            return (false, null, "安全围栏拦截：仅允许管理当前用户 HKCU 注册表项，严禁访问 HKLM 系统注册表。");
        }

        string subKey = normalized[@"HKCU\".Length..];
        const string requiredPrefix = @"Software\";
        if (!subKey.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, @"安全围栏拦截：注册表路径必须位于 HKCU\Software\ 下。");
        }

        string remainder = subKey[requiredPrefix.Length..];
        string[] parts = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (false, null, @"安全围栏拦截：HKCU\Software 注册表深度不足（至少需包含 <开发商>\<游戏名> 两级子键）。");
        }

        string vendor = parts[0];
        if (ProtectedRegistryVendors.Contains(vendor))
        {
            return (false, null, $@"安全围栏拦截：禁止访问系统保护命名空间 HKCU\Software\{vendor}。");
        }

        if (vendor.Equals("Valve", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals("Steam", StringComparison.OrdinalIgnoreCase)
            && parts.Length < 4)
        {
            return (false, null, "安全围栏拦截：禁止访问 Steam 客户端全局注册表根键。");
        }

        return (true, @"HKCU\" + subKey, null);
    }

    /// <summary>
    /// Detects whether <paramref name="fullPath"/> or any existing ancestor directory is an NTFS Junction or Symbolic Link.
    /// </summary>
    public static bool HasDisallowedReparsePointOnPath(string fullPath, out string? offendingPath)
    {
        offendingPath = null;
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string? cursor = Path.TrimEndingDirectorySeparator(fullPath);
        string? root = Path.GetPathRoot(cursor);
        string? normalizedRoot = string.IsNullOrWhiteSpace(root) ? null : Path.TrimEndingDirectorySeparator(root);

        int guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && guard++ < 64)
        {
            if (normalizedRoot is not null
                && string.Equals(cursor, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            try
            {
                if (File.Exists(cursor))
                {
                    var fi = new FileInfo(cursor);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(fi.LinkTarget))
                    {
                        offendingPath = cursor;
                        return true;
                    }
                }
                else if (Directory.Exists(cursor))
                {
                    var di = new DirectoryInfo(cursor);
                    if ((di.Attributes & FileAttributes.ReparsePoint) != 0 && !string.IsNullOrEmpty(di.LinkTarget))
                    {
                        offendingPath = cursor;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore inaccessible parent directories.
            }

            string? parent = Path.GetDirectoryName(cursor);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            cursor = Path.TrimEndingDirectorySeparator(parent);
        }

        return false;
    }

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return !string.IsNullOrEmpty(normalized);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameOrParentDirectory(string potentialParent, string childPath)
    {
        if (string.Equals(potentialParent, childPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return childPath.StartsWith(potentialParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedRootChild(string firstSegmentAfterDrive) =>
        firstSegmentAfterDrive.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
        || firstSegmentAfterDrive.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
        || firstSegmentAfterDrive.Equals("Recovery", StringComparison.OrdinalIgnoreCase)
        || firstSegmentAfterDrive.Equals("Boot", StringComparison.OrdinalIgnoreCase)
        || firstSegmentAfterDrive.Equals("PerfLogs", StringComparison.OrdinalIgnoreCase)
        || firstSegmentAfterDrive.Equals("Windows", StringComparison.OrdinalIgnoreCase);
}
