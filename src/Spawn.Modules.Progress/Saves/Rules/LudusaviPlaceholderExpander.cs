using Spawn.Contracts;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Expands all 16 Ludusavi Windows path placeholders (<c>&lt;base&gt;</c>, <c>&lt;root&gt;</c>, <c>&lt;game&gt;</c>,
/// <c>&lt;home&gt;</c>, <c>&lt;winAppData&gt;</c>, <c>&lt;winLocalAppData&gt;</c>, <c>&lt;winLocalAppDataLow&gt;</c>,
/// <c>&lt;winDocuments&gt;</c>, <c>&lt;winPublic&gt;</c>, <c>&lt;winProgramData&gt;</c>, <c>&lt;winDir&gt;</c>,
/// <c>&lt;osUserName&gt;</c>, <c>&lt;storeUserId&gt;</c>, <c>&lt;xdgData&gt;</c>, <c>&lt;xdgConfig&gt;</c>,
/// and <c>*</c>/<c>**</c> zero-backtracking globs) and provides round-trip-safe bidirectional
/// <see cref="CanonicalizeToLogicalPath"/> / <see cref="ExpandLogicalPath"/> translation.
/// </summary>
public sealed class LudusaviPlaceholderExpander
{
    public const string RegistryLogicalPrefix = "registry://";
    public const int MaxWalkDepth = 32;

    /// <summary>
    /// Transient cache/diagnostic directories strictly skipped during glob traversal and directory measurement.
    /// </summary>
    public static IReadOnlySet<string> IgnoredDirectoryNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ShaderCache",
        "CrashDumps",
        "Logs",
    };

    private readonly IKnownFolderProvider _knownFolders;

    public LudusaviPlaceholderExpander(IKnownFolderProvider? knownFolders = null)
    {
        _knownFolders = knownFolders ?? WindowsKnownFolderResolver.Instance;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="template"/> relies on Linux XDG placeholders
    /// (<c>&lt;xdgData&gt;</c> or <c>&lt;xdgConfig&gt;</c>).
    /// </summary>
    public static bool UsesXdgPlaceholder(string? template) =>
        !string.IsNullOrWhiteSpace(template)
        && (template.Contains("<xdgData>", StringComparison.OrdinalIgnoreCase)
            || template.Contains("<xdgConfig>", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Expands a Ludusavi path template (supporting all 16 Windows placeholders and <c>*</c>/<c>**</c> globs)
    /// into concrete, deduplicated Windows filesystem paths.
    /// Skips <c>ShaderCache</c>, <c>CrashDumps</c>, <c>Logs</c>, and NTFS ReparsePoints.
    /// </summary>
    public IReadOnlyList<string> ExpandTemplate(
        string template,
        GameRecord game,
        bool hasWindowsNativeAlternative = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(template))
        {
            return [];
        }

        string trimmed = template.Trim();

        // §5.1: On Windows, ignore pure Linux <xdgData>/<xdgConfig> entries when Windows-native alternatives exist;
        // otherwise fall back to %LOCALAPPDATA% for cross-handheld manifest compatibility.
        if (UsesXdgPlaceholder(trimmed) && hasWindowsNativeAlternative)
        {
            return [];
        }

        List<string> concreteTemplates = ExpandPlaceholdersToPathPatterns(trimmed, game);
        if (concreteTemplates.Count == 0)
        {
            return [];
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pattern in concreteTemplates)
        {
            foreach (string expanded in ExpandGlobPattern(pattern))
            {
                if (seen.Add(expanded))
                {
                    results.Add(expanded);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Converts a machine-local absolute path (or <c>HKCU\...</c> / <c>HKEY_CURRENT_USER\...</c> registry key)
    /// into a portable placeholder-normalized logical path (e.g., <c>&lt;winAppData&gt;/Game/slot0.sav</c> or
    /// <c>registry://HKCU/Software/...</c>).
    /// </summary>
    public string CanonicalizeToLogicalPath(string fullPathOrRegistryKey, GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(fullPathOrRegistryKey))
        {
            return string.Empty;
        }

        string trimmed = fullPathOrRegistryKey.Trim();

        // Already a logical registry:// path -> normalize cleanly.
        if (trimmed.StartsWith(RegistryLogicalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string regBody = trimmed[RegistryLogicalPrefix.Length..];
            if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(regBody, out string canonical, out _, out _))
            {
                return RegistryLogicalPrefix + canonical.Replace('\\', '/');
            }

            return RegistryLogicalPrefix + regBody.Replace('\\', '/').Trim('/');
        }

        // Raw HKCU / HKEY_CURRENT_USER registry key -> registry://HKCU/...
        if (LooksLikeRegistryKey(trimmed))
        {
            if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(trimmed, out string canonical, out _, out _))
            {
                return RegistryLogicalPrefix + canonical.Replace('\\', '/');
            }

            return RegistryLogicalPrefix + trimmed.Replace('\\', '/').Trim('/');
        }

        // Already starts with a known <placeholder> -> normalize slashes only.
        if (trimmed.StartsWith('<') && trimmed.IndexOf('>') is > 1 and < 28)
        {
            return NormalizeLogicalSlashes(trimmed);
        }

        string fullPath;
        try
        {
            fullPath = NormalizeFullPath(trimmed);
        }
        catch
        {
            return trimmed.Replace('\\', '/');
        }

        // Longest-prefix-first matching ensures <winLocalAppDataLow>, <winLocalAppData>, <winAppData>,
        // <winDocuments>, and <base> take precedence over <home>.
        List<(string Placeholder, string Root, int TieBreakPriority)> roots = BuildCanonicalizationRoots(game);
        roots.Sort(static (a, b) =>
        {
            int lenCmp = b.Root.Length.CompareTo(a.Root.Length);
            return lenCmp != 0 ? lenCmp : a.TieBreakPriority.CompareTo(b.TieBreakPriority);
        });

        foreach (var (placeholder, root, _) in roots)
        {
            if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return placeholder;
            }

            string prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = fullPath[prefix.Length..].Replace('\\', '/');
                return $"{placeholder}/{remainder}";
            }
        }

        return fullPath.Replace('\\', '/');
    }

    /// <summary>
    /// Expands a portable placeholder-normalized logical path back into a machine-local absolute path
    /// (or canonical <c>HKCU\...</c> registry key). Round-trip safe with <see cref="CanonicalizeToLogicalPath"/>.
    /// </summary>
    public string ExpandLogicalPath(string logicalPath, GameRecord game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (string.IsNullOrWhiteSpace(logicalPath))
        {
            return string.Empty;
        }

        string trimmed = logicalPath.Trim();

        // registry://HKCU/... -> HKCU\...
        if (trimmed.StartsWith(RegistryLogicalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string regBody = trimmed[RegistryLogicalPrefix.Length..];
            if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(regBody, out string canonical, out _, out _))
            {
                return canonical;
            }

            return regBody.Replace('/', '\\').TrimEnd('\\');
        }

        if (LooksLikeRegistryKey(trimmed))
        {
            if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(trimmed, out string canonical, out _, out _))
            {
                return canonical;
            }

            return trimmed.Replace('/', '\\').TrimEnd('\\');
        }

        List<string> expanded = ExpandPlaceholdersToPathPatterns(trimmed, game);
        string candidate = expanded.Count > 0 ? expanded[0] : trimmed;

        // If <storeUserId> was present, substitute the preferred active store user ID when expanding a single logical path.
        if (candidate.Contains("<storeUserId>", StringComparison.OrdinalIgnoreCase))
        {
            List<string> storeIds = GetEffectiveStoreUserIds(game);
            string preferredId = storeIds.Count > 0 ? storeIds[0] : WindowsKnownFolderResolver.DefaultGoldbergUserId;
            candidate = candidate.Replace("<storeUserId>", preferredId, StringComparison.OrdinalIgnoreCase);
        }

        string windowsPath = candidate.Replace('/', '\\');
        try
        {
            return NormalizeFullPath(windowsPath);
        }
        catch
        {
            return windowsPath;
        }
    }

    /// <summary>
    /// Computes regular file count and total bytes for a file or directory, strictly skipping
    /// <see cref="IgnoredDirectoryNames"/> (<c>ShaderCache</c>, <c>CrashDumps</c>, <c>Logs</c>)
    /// and NTFS ReparsePoints.
    /// </summary>
    public static (bool Exists, int FileCount, long TotalBytes) MeasurePathSkippingIgnored(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return (false, 0, 0);
        }

        try
        {
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return (false, 0, 0);
                }

                return (true, 1, fi.Length);
            }

            if (!Directory.Exists(fullPath))
            {
                return (false, 0, 0);
            }

            var rootInfo = new DirectoryInfo(fullPath);
            if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return (false, 0, 0);
            }

            int count = 0;
            long totalBytes = 0;
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((fullPath, 0));

            while (stack.Count > 0)
            {
                var (currentDir, depth) = stack.Pop();
                if (depth > MaxWalkDepth)
                {
                    continue;
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDir);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        count++;
                        totalBytes += fi.Length;
                    }
                    catch
                    {
                        // Skip inaccessible files.
                    }
                }

                if (depth >= MaxWalkDepth)
                {
                    continue;
                }

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(currentDir);
                }
                catch
                {
                    continue;
                }

                foreach (string subDir in subDirs)
                {
                    try
                    {
                        string name = Path.GetFileName(subDir);
                        if (IgnoredDirectoryNames.Contains(name))
                        {
                            continue;
                        }

                        if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        stack.Push((subDir, depth + 1));
                    }
                    catch
                    {
                        // Skip inaccessible subdirectories.
                    }
                }
            }

            return (true, count, totalBytes);
        }
        catch
        {
            return (false, 0, 0);
        }
    }

    /// <summary>
    /// Zero-backtracking O(N*M) linear wildcard matcher supporting <c>*</c> (any sequence) and <c>?</c> (any single char),
    /// case-insensitive for Windows path segments. Immune to regex backtracking / ReDoS.
    /// </summary>
    public static bool WildcardMatchLinear(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0;
        int t = 0;
        int starIdx = -1;
        int matchIdx = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = t;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                t = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="path"/> itself is an NTFS ReparsePoint (junction or symlink).
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static bool LooksLikeRegistryKey(string text) =>
        text.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("HKCU/", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"HKEY_", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("HKLM/", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"HKCR\", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"HKU\", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith(@"HKCC\", StringComparison.OrdinalIgnoreCase);

    private List<string> ExpandPlaceholdersToPathPatterns(string template, GameRecord game)
    {
        string normalizedTemplate = template.Replace('\\', '/');
        string installDir = SafeNormalizeOrEmpty(game.InstallDir);
        string gameFolderName = string.IsNullOrWhiteSpace(installDir) ? string.Empty : Path.GetFileName(installDir);
        (string libraryCommonRoot, string? inferredSteamRoot) = InferLibraryAndSteamRoot(installDir);

        // Handle compound <root>/steamapps/common/<game> and <root>/<game> per §5.1.
        if (!string.IsNullOrWhiteSpace(installDir))
        {
            normalizedTemplate = ReplacePrefixCaseInsensitive(normalizedTemplate, "<root>/steamapps/common/<game>", installDir);
            normalizedTemplate = ReplacePrefixCaseInsensitive(normalizedTemplate, "<root>/<game>", installDir);
        }

        // Special handling for <root>/userdata/...: expand against every known Steam install root + inferred Steam root.
        var rootCandidates = new List<string>(4);
        if (normalizedTemplate.StartsWith("<root>/userdata/", StringComparison.OrdinalIgnoreCase)
            || normalizedTemplate.Equals("<root>/userdata", StringComparison.OrdinalIgnoreCase))
        {
            var seenSteamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(inferredSteamRoot) && seenSteamRoots.Add(inferredSteamRoot))
            {
                rootCandidates.Add(inferredSteamRoot);
            }

            foreach (string steamRoot in _knownFolders.GetSteamRootDirectories())
            {
                string norm = SafeNormalizeOrEmpty(steamRoot);
                if (!string.IsNullOrWhiteSpace(norm) && seenSteamRoots.Add(norm))
                {
                    rootCandidates.Add(norm);
                }
            }

            if (rootCandidates.Count == 0 && !string.IsNullOrWhiteSpace(libraryCommonRoot))
            {
                rootCandidates.Add(libraryCommonRoot);
            }
        }
        else if (normalizedTemplate.Contains("<root>", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(libraryCommonRoot))
            {
                rootCandidates.Add(libraryCommonRoot);
            }
            else if (!string.IsNullOrWhiteSpace(installDir))
            {
                rootCandidates.Add(installDir);
            }
        }
        else
        {
            rootCandidates.Add(string.Empty);
        }

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rootValue in rootCandidates)
        {
            string expanded = normalizedTemplate;

            if (expanded.Contains("<root>", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(rootValue))
                {
                    continue;
                }

                expanded = expanded.Replace("<root>", rootValue, StringComparison.OrdinalIgnoreCase);
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<base>", installDir))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<game>", gameFolderName))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<home>", SafeNormalizeOrEmpty(_knownFolders.Home)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winAppData>", SafeNormalizeOrEmpty(_knownFolders.WinAppData)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winLocalAppData>", SafeNormalizeOrEmpty(_knownFolders.WinLocalAppData)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winLocalAppDataLow>", SafeNormalizeOrEmpty(_knownFolders.WinLocalAppDataLow)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winDocuments>", SafeNormalizeOrEmpty(_knownFolders.WinDocuments)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winPublic>", SafeNormalizeOrEmpty(_knownFolders.WinPublic)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winProgramData>", SafeNormalizeOrEmpty(_knownFolders.WinProgramData)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<winDir>", SafeNormalizeOrEmpty(_knownFolders.WinDir)))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<osUserName>", _knownFolders.OsUserName ?? string.Empty))
            {
                continue;
            }

            // §5.1: <xdgData> / <xdgConfig> fall back to %LOCALAPPDATA% when retained on Windows.
            string xdgFallback = SafeNormalizeOrEmpty(_knownFolders.WinLocalAppData);
            if (!TrySubstitutePlaceholder(ref expanded, "<xdgData>", xdgFallback))
            {
                continue;
            }

            if (!TrySubstitutePlaceholder(ref expanded, "<xdgConfig>", xdgFallback))
            {
                continue;
            }

            // Expand <storeUserId> across existing disk user directories (ordered by most recent activity)
            // plus Steam userdata / Goldberg / gbe_fork discovered IDs.
            if (expanded.Contains("<storeUserId>", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string userExpanded in ExpandStoreUserIdBranches(expanded, game))
                {
                    if (seen.Add(userExpanded))
                    {
                        results.Add(userExpanded);
                    }
                }
            }
            else if (seen.Add(expanded))
            {
                results.Add(expanded);
            }
        }

        return results;
    }

    private IEnumerable<string> ExpandStoreUserIdBranches(string patternWithToken, GameRecord game)
    {
        const string token = "<storeUserId>";
        int idx = patternWithToken.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            yield return patternWithToken;
            yield break;
        }

        var candidates = new List<(string Id, DateTime ActivityUtc, bool ExistsOnDisk)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan actual existing subdirectories directly under the prefix parent directory (e.g., %APPDATA%\EldenRing\<uid>).
        string prefixRaw = patternWithToken[..idx].Replace('/', '\\').TrimEnd('\\');
        if (!string.IsNullOrWhiteSpace(prefixRaw)
            && !prefixRaw.Contains('*')
            && !prefixRaw.Contains('?')
            && !prefixRaw.Contains('<'))
        {
            string? parentDir = null;
            try
            {
                parentDir = NormalizeFullPath(prefixRaw);
            }
            catch
            {
                parentDir = null;
            }

            if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir) && !IsReparsePoint(parentDir))
            {
                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(parentDir))
                    {
                        if (IsReparsePoint(subDir))
                        {
                            continue;
                        }

                        string dirName = Path.GetFileName(subDir);
                        if (string.IsNullOrWhiteSpace(dirName) || IgnoredDirectoryNames.Contains(dirName))
                        {
                            continue;
                        }

                        if (seenIds.Add(dirName))
                        {
                            candidates.Add((dirName, GetDirectoryActivityUtc(subDir), true));
                        }
                    }
                }
                catch
                {
                    // Ignore unreadable parent directory.
                }
            }
        }

        // 2. Include discovered Steam userdata / Goldberg / gbe_fork user IDs (ordered by activity).
        List<string> discoveredIds = GetEffectiveStoreUserIds(game);
        DateTime syntheticRank = DateTime.UnixEpoch;
        for (int i = 0; i < discoveredIds.Count; i++)
        {
            string id = discoveredIds[i];
            if (seenIds.Add(id))
            {
                candidates.Add((id, syntheticRank.AddSeconds(-i), false));
            }
        }

        // Order: existing-on-disk branches sorted by most recent modification time FIRST, then discovered IDs.
        foreach (var entry in candidates
                     .OrderByDescending(c => c.ExistsOnDisk)
                     .ThenByDescending(c => c.ActivityUtc)
                     .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            string replaced = patternWithToken.Replace(token, entry.Id, StringComparison.OrdinalIgnoreCase);
            yield return replaced;
        }
    }

    private List<string> GetEffectiveStoreUserIds(GameRecord game)
    {
        var steamRoots = new List<string>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (_, string? inferredSteamRoot) = InferLibraryAndSteamRoot(SafeNormalizeOrEmpty(game.InstallDir));
        if (!string.IsNullOrWhiteSpace(inferredSteamRoot) && seenRoots.Add(inferredSteamRoot))
        {
            steamRoots.Add(inferredSteamRoot);
        }

        foreach (string root in _knownFolders.GetSteamRootDirectories())
        {
            string norm = SafeNormalizeOrEmpty(root);
            if (!string.IsNullOrWhiteSpace(norm) && seenRoots.Add(norm))
            {
                steamRoots.Add(norm);
            }
        }

        var merged = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First: explicitly provided / configured IDs from IKnownFolderProvider.
        foreach (string id in _knownFolders.GetStoreUserIds())
        {
            if (!string.IsNullOrWhiteSpace(id) && seenIds.Add(id.Trim()))
            {
                merged.Add(id.Trim());
            }
        }

        // Second: live discovery from Steam userdata roots & Goldberg/gbe_fork settings under WinAppData.
        foreach (string id in WindowsKnownFolderResolver.DiscoverStoreUserIds(steamRoots, SafeNormalizeOrEmpty(_knownFolders.WinAppData)))
        {
            if (!string.IsNullOrWhiteSpace(id) && seenIds.Add(id))
            {
                merged.Add(id);
            }
        }

        return merged;
    }

    private static List<string> ExpandGlobPattern(string rawPattern)
    {
        string winPattern = rawPattern.Replace('/', '\\');

        // Reject any residual unexpanded placeholder token.
        if (winPattern.Contains('<') || winPattern.Contains('>'))
        {
            return [];
        }

        // Pure literal path (no wildcards): normalize and return directly.
        if (!winPattern.Contains('*') && !winPattern.Contains('?'))
        {
            try
            {
                string full = NormalizeFullPath(winPattern);
                return IsReparsePoint(full) ? [] : [full];
            }
            catch
            {
                return [];
            }
        }

        // Fast-path: "<concreteDir>\**" (wildcard-free prefix followed only by "\**") -> resolve to the concrete directory
        // so SaveTimeMachine & Watcher get a clean directory target whose files are measured skipping ignored dirs/ReparsePoints.
        if (winPattern.EndsWith(@"\**", StringComparison.Ordinal)
            && !winPattern[..^3].Contains('*')
            && !winPattern[..^3].Contains('?'))
        {
            try
            {
                string baseDir = NormalizeFullPath(winPattern[..^3]);
                return IsReparsePoint(baseDir) ? [] : [baseDir];
            }
            catch
            {
                return [];
            }
        }

        // General zero-backtracking segment-based glob expansion (handles *, ?, and ** anywhere in the path).
        string? root = Path.GetPathRoot(winPattern);
        if (string.IsNullOrWhiteSpace(root))
        {
            return [];
        }

        string remainder = winPattern[root.Length..];
        string[] rawSegments = remainder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            return [];
        }

        // Collapse leading literal segments (including any "..") before the first wildcard segment.
        int firstWildcardIdx = -1;
        for (int i = 0; i < rawSegments.Length; i++)
        {
            if (rawSegments[i].Contains('*') || rawSegments[i].Contains('?'))
            {
                firstWildcardIdx = i;
                break;
            }
        }

        if (firstWildcardIdx < 0)
        {
            try
            {
                string full = NormalizeFullPath(winPattern);
                return IsReparsePoint(full) ? [] : [full];
            }
            catch
            {
                return [];
            }
        }

        string basePrefix = root;
        for (int i = 0; i < firstWildcardIdx; i++)
        {
            basePrefix = Path.Combine(basePrefix, rawSegments[i]);
        }

        string startDir;
        try
        {
            startDir = NormalizeFullPath(basePrefix);
        }
        catch
        {
            return [];
        }

        if (!Directory.Exists(startDir) || IsReparsePoint(startDir))
        {
            return [];
        }

        string[] remainingSegments = rawSegments[firstWildcardIdx..];

        // If remaining segments are literally ["**"], return the startDir itself (already checked non-reparse).
        if (remainingSegments.Length == 1 && remainingSegments[0] == "**")
        {
            return [startDir];
        }

        var matched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Iterative explicit work-stack traversal (zero recursion, zero backtracking explosion).
        var stack = new Stack<(string CurrentDir, int SegmentIndex, int WalkDepth)>();
        stack.Push((startDir, 0, 0));

        while (stack.Count > 0)
        {
            var (currentDir, segIdx, walkDepth) = stack.Pop();
            if (walkDepth > MaxWalkDepth || segIdx >= remainingSegments.Length)
            {
                continue;
            }

            string seg = remainingSegments[segIdx];
            bool isLast = segIdx == remainingSegments.Length - 1;

            if (seg == "**")
            {
                if (isLast)
                {
                    // Trailing ** after an earlier wildcard (e.g. Profiles/*/Savegames/**) -> match currentDir directly!
                    string normDir = NormalizeFullPath(currentDir);
                    if (!IsReparsePoint(normDir) && seen.Add(normDir))
                    {
                        matched.Add(normDir);
                    }

                    continue;
                }

                // Zero-depth match: try next segment in currentDir.
                stack.Push((currentDir, segIdx + 1, walkDepth));

                // Recursive descend into non-ignored, non-reparse child directories.
                if (walkDepth < MaxWalkDepth)
                {
                    foreach (string childDir in SafeEnumerateValidSubDirectories(currentDir))
                    {
                        stack.Push((childDir, segIdx, walkDepth + 1));
                    }
                }

                continue;
            }

            bool hasWildcard = seg.Contains('*') || seg.Contains('?');

            if (!hasWildcard)
            {
                if (IgnoredDirectoryNames.Contains(seg))
                {
                    continue;
                }

                string nextPath = Path.Combine(currentDir, seg);
                if (isLast)
                {
                    if ((File.Exists(nextPath) || Directory.Exists(nextPath)) && !IsReparsePoint(nextPath))
                    {
                        string norm = NormalizeFullPath(nextPath);
                        if (seen.Add(norm))
                        {
                            matched.Add(norm);
                        }
                    }
                }
                else if (Directory.Exists(nextPath) && !IsReparsePoint(nextPath))
                {
                    stack.Push((nextPath, segIdx + 1, walkDepth + 1));
                }

                continue;
            }

            // Wildcard segment (* or ?): match subdirectories (and files if isLast).
            if (isLast)
            {
                foreach (string file in SafeEnumerateValidFiles(currentDir))
                {
                    string fileName = Path.GetFileName(file);
                    if (WildcardMatchLinear(seg, fileName))
                    {
                        string norm = NormalizeFullPath(file);
                        if (seen.Add(norm))
                        {
                            matched.Add(norm);
                        }
                    }
                }

                foreach (string childDir in SafeEnumerateValidSubDirectories(currentDir))
                {
                    string dirName = Path.GetFileName(childDir);
                    if (WildcardMatchLinear(seg, dirName))
                    {
                        string norm = NormalizeFullPath(childDir);
                        if (seen.Add(norm))
                        {
                            matched.Add(norm);
                        }
                    }
                }
            }
            else
            {
                foreach (string childDir in SafeEnumerateValidSubDirectories(currentDir))
                {
                    string dirName = Path.GetFileName(childDir);
                    if (WildcardMatchLinear(seg, dirName))
                    {
                        stack.Push((childDir, segIdx + 1, walkDepth + 1));
                    }
                }
            }
        }

        matched.Sort(StringComparer.OrdinalIgnoreCase);
        return matched;
    }

    private static IEnumerable<string> SafeEnumerateValidSubDirectories(string dir)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(dir);
        }
        catch
        {
            yield break;
        }

        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (string.IsNullOrWhiteSpace(name) || IgnoredDirectoryNames.Contains(name))
            {
                continue;
            }

            if (IsReparsePoint(child))
            {
                continue;
            }

            yield return child;
        }
    }

    private static IEnumerable<string> SafeEnumerateValidFiles(string dir)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch
        {
            yield break;
        }

        foreach (string file in files)
        {
            if (IsReparsePoint(file))
            {
                continue;
            }

            yield return file;
        }
    }

    private List<(string Placeholder, string Root, int TieBreakPriority)> BuildCanonicalizationRoots(GameRecord game)
    {
        var roots = new List<(string, string, int)>(10);

        void TryAdd(string placeholder, string? path, int priority)
        {
            string norm = SafeNormalizeOrEmpty(path);
            if (!string.IsNullOrWhiteSpace(norm))
            {
                roots.Add((placeholder, norm, priority));
            }
        }

        TryAdd("<base>", game.InstallDir, 0);
        TryAdd("<winLocalAppDataLow>", _knownFolders.WinLocalAppDataLow, 1);
        TryAdd("<winLocalAppData>", _knownFolders.WinLocalAppData, 2);
        TryAdd("<winAppData>", _knownFolders.WinAppData, 3);
        TryAdd("<winDocuments>", _knownFolders.WinDocuments, 4);
        TryAdd("<winPublic>", _knownFolders.WinPublic, 5);
        TryAdd("<winProgramData>", _knownFolders.WinProgramData, 6);
        TryAdd("<winDir>", _knownFolders.WinDir, 7);
        TryAdd("<home>", _knownFolders.Home, 8);

        return roots;
    }

    private static (string LibraryCommonRoot, string? SteamRoot) InferLibraryAndSteamRoot(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return (string.Empty, null);
        }

        try
        {
            DirectoryInfo? dir = new DirectoryInfo(installDir);
            DirectoryInfo? parent = dir.Parent;
            if (parent is null)
            {
                return (installDir, null);
            }

            // Detect <SteamLibraryRoot>\steamapps\common\<GameFolder>
            if (string.Equals(parent.Name, "common", StringComparison.OrdinalIgnoreCase)
                && parent.Parent is { } steamApps
                && string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase)
                && steamApps.Parent is { } steamRoot)
            {
                return (NormalizeFullPath(parent.FullName), NormalizeFullPath(steamRoot.FullName));
            }

            return (NormalizeFullPath(parent.FullName), null);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static bool TrySubstitutePlaceholder(ref string text, string placeholder, string replacement)
    {
        if (!text.Contains(placeholder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(replacement))
        {
            return false;
        }

        text = text.Replace(placeholder, replacement, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static string ReplacePrefixCaseInsensitive(string text, string prefix, string replacement)
    {
        if (text.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return replacement;
        }

        if (text.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return replacement + text[prefix.Length..];
        }

        return text;
    }

    private static DateTime GetDirectoryActivityUtc(string directoryPath)
    {
        DateTime latest = Directory.GetLastWriteTimeUtc(directoryPath);
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                DateTime entryTime = File.GetLastWriteTimeUtc(entry);
                if (entryTime > latest)
                {
                    latest = entryTime;
                }
            }
        }
        catch
        {
            // Ignore unreadable children.
        }

        return latest;
    }

    private static string NormalizeLogicalSlashes(string text)
    {
        string replaced = text.Replace('\\', '/');
        while (replaced.Contains("//", StringComparison.Ordinal))
        {
            replaced = replaced.Replace("//", "/", StringComparison.Ordinal);
        }

        return replaced.TrimEnd('/');
    }

    private static string SafeNormalizeOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return NormalizeFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeFullPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return Path.TrimEndingDirectorySeparator(full);
    }
}
