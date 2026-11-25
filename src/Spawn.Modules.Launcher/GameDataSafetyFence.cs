using System.Text;
using System.Text.RegularExpressions;
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
    public string? EnglishTitle { get; init; }
    public string? InstallFolderTitle { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }

    /// <summary>Fuzzy-matchable names (title, English title, Unity <c>productName</c>, non-generic Unreal project name, multi-token install folder).</summary>
    public IReadOnlyList<string> ProductNames { get; init; } = [];

    /// <summary>Exact-match-only names (executable base name, install folder name) that may be short or generic.</summary>
    public IReadOnlyList<string> ExactNames { get; init; } = [];

    public static GameDataTargetIdentity FromGame(
        GameRecord game,
        IEnumerable<string?>? extraProductNames = null,
        string? englishTitle = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        var products = new List<string>();

        static void AddUniqueProduct(List<string> target, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !string.Equals(value.Trim(), "Unknown Game", StringComparison.OrdinalIgnoreCase)
                && !target.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value.Trim());
            }
        }

        string? resolvedEnglishTitle = GameDataSafetyFence.ResolveUsableEnglishTitleCandidate(englishTitle, game.Title)
            ?? GameDataSafetyFence.ResolveUsableEnglishTitleCandidate(game.Title, game.Title);

        AddUniqueProduct(products, game.Title);
        AddUniqueProduct(products, resolvedEnglishTitle);

        foreach (string? extra in extraProductNames ?? [])
        {
            AddUniqueProduct(products, extra);
        }

        var exact = new List<string>();
        string exeBase = Path.GetFileNameWithoutExtension(game.ExecutableRelPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(exeBase) && !GameDataSafetyFence.IsGenericOrWrapperExecutableStem(exeBase))
        {
            exact.Add(exeBase);
        }

        string? acceptedInstallFolderTitle = null;
        string dirLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(dirLeaf))
        {
            string cleanedDirTitle = GameTitleCleaner.CleanTitle(dirLeaf, preserveRemasterEdition: true);
            string? referenceTitle = GameTitleCleaner.IsLikelyEnglishTitle(game.Title)
                ? game.Title
                : (resolvedEnglishTitle ?? game.Title);

            if (GameDataSafetyFence.CanAcceptInstallFolderForIdentity(dirLeaf, cleanedDirTitle, referenceTitle))
            {
                exact.Add(dirLeaf);

                if (!string.Equals(cleanedDirTitle, "Unknown Game", StringComparison.OrdinalIgnoreCase)
                    && GameTitleCleaner.IsLikelyEnglishTitle(cleanedDirTitle))
                {
                    acceptedInstallFolderTitle = cleanedDirTitle;
                }

                if (!string.Equals(cleanedDirTitle, "Unknown Game", StringComparison.OrdinalIgnoreCase)
                    && GameDataSafetyFence.ShouldPromoteInstallFolderToProductName(cleanedDirTitle, referenceTitle))
                {
                    AddUniqueProduct(products, cleanedDirTitle);
                }
            }
        }

        return new GameDataTargetIdentity
        {
            Title = game.Title,
            EnglishTitle = resolvedEnglishTitle,
            InstallFolderTitle = acceptedInstallFolderTitle,
            SteamAppId = game.SteamAppId,
            GogGameId = game.GogGameId,
            ProductNames = products,
            ExactNames = exact,
        };
    }
}

/// <summary>
/// Ironclad safety fence preventing accidental deletion of game installation roots, executables/DLLs,
/// engine asset directories, SPAWN's own database/vault, OS system roots, publisher/emulator shared containers,
/// or <c>HKLM</c> registry hives.
/// </summary>
public static partial class GameDataSafetyFence
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

        // Also block shared Steam / Social Club emulator & publisher parent roots directly (e.g. ...\Steam\CODEX, ...\Rockstar Games, ...\GTA_DEF).
        string leafName = Path.GetFileName(fullTarget);
        if (ProtectedSharedContainerLeafNames.Contains(leafName))
        {
            return (false, $"安全围栏拦截：严禁清理跨游戏共享发行商或模拟器总目录 ({leafName})。");
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

        // Multi-game shared pack/emulator containers (such as Goldberg SocialClub Emu's shared GTA_DEF folder across the trilogy)
        // are never safe for single-game deletion or overwrite even when nested profile UID folders exist underneath.
        if (segments.Any(static seg => MultiGameSharedSubtreeSegments.Contains(seg)))
        {
            return (false, $"安全围栏拦截：目标位于跨游戏共享存档容器内 ({relative})，禁止单游戏清理以防误删同系列其他作品数据。");
        }

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

    internal static bool IsGenericOrWrapperExecutableStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return true;
        }

        string trimmed = stem.Trim();
        if (ProtectedSharedContainerLeafNames.Contains(trimmed)
            || MultiGameSharedSubtreeSegments.Contains(trimmed)
            || ForbiddenExecutableStems.Contains(trimmed)
            || trimmed.StartsWith("PlayGTA", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string norm = NormalizeName(trimmed);
        if (norm.Length < 4 || GenericSegmentNames.Contains(norm) || ForbiddenExecutableStems.Contains(norm))
        {
            return true;
        }

        PeExecutableRole role = GameTitleCleaner.ClassifyPeVersionMetadata(trimmed, trimmed, null, null);
        return role is PeExecutableRole.RedirectorOrPrerequisiteStub or PeExecutableRole.LauncherOrMenuWrapper;
    }

    internal static bool CanAcceptInstallFolderForIdentity(string rawDirLeaf, string cleanedDirTitle, string? gameTitle)
    {
        if (string.IsNullOrWhiteSpace(rawDirLeaf))
        {
            return false;
        }

        string trimmedLeaf = rawDirLeaf.Trim();
        if (ProtectedSharedContainerLeafNames.Contains(trimmedLeaf) || MultiGameSharedSubtreeSegments.Contains(trimmedLeaf))
        {
            return false;
        }

        string normRaw = NormalizeName(trimmedLeaf);
        if (normRaw.Length < 4 || GenericSegmentNames.Contains(normRaw))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(gameTitle))
        {
            return true;
        }

        string effectiveDir = string.IsNullOrWhiteSpace(cleanedDirTitle) || string.Equals(cleanedDirTitle, "Unknown Game", StringComparison.OrdinalIgnoreCase)
            ? trimmedLeaf
            : cleanedDirTitle;

        if (GameTitleCleaner.ContainsCjkCharacters(gameTitle))
        {
            CanonicalTitleProfile dirForCjk = BuildCanonicalProfile(effectiveDir, allowTrailingSaveFolderSuffix: false);
            CanonicalTitleProfile cjkProfile = BuildCjkFallbackReferenceProfile(gameTitle);
            return IsEnglishCandidateConsistentWithCjkTitle(dirForCjk, cjkProfile);
        }

        if (!GameTitleCleaner.IsLikelyEnglishTitle(gameTitle))
        {
            return true;
        }

        if (string.Equals(NormalizeName(effectiveDir), NormalizeName(gameTitle), StringComparison.Ordinal))
        {
            return true;
        }

        CanonicalTitleProfile dirProfile = BuildCanonicalProfile(effectiveDir, allowTrailingSaveFolderSuffix: false);
        CanonicalTitleProfile titleProfile = BuildCanonicalProfile(gameTitle, allowTrailingSaveFolderSuffix: false);
        if (!HasCompatibleMarkersAndInstallments(dirProfile, titleProfile))
        {
            return false;
        }

        // When gameTitle is already a valid English title, an install folder name may only supplement identity
        // if it shares at least one distinguishing core token with gameTitle (e.g. "GTA" <-> "Grand Theft Auto").
        foreach (string token in dirProfile.CoreTokens)
        {
            if (titleProfile.CoreTokens.Contains(token, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldPromoteInstallFolderToProductName(string cleanedDirTitle, string? gameTitle)
    {
        string normDir = NormalizeName(cleanedDirTitle);
        if (normDir.Length < 4 || GenericSegmentNames.Contains(normDir) || ProtectedSharedContainerLeafNames.Contains(cleanedDirTitle))
        {
            return false;
        }

        if (!CanAcceptInstallFolderForIdentity(cleanedDirTitle, cleanedDirTitle, gameTitle))
        {
            return false;
        }

        var profile = BuildCanonicalProfile(cleanedDirTitle, allowTrailingSaveFolderSuffix: false);
        if (profile.CoreTokens.Count >= 2)
        {
            return true;
        }

        return GameTitleCleaner.ContainsCjkCharacters(gameTitle)
               && GameTitleCleaner.IsLikelyEnglishTitle(cleanedDirTitle)
               && normDir.Length >= 5;
    }

    internal static bool IsGameSpecificSegment(string segment, GameDataTargetIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        string trimmed = segment.Trim();
        if (ProtectedSharedContainerLeafNames.Contains(trimmed) || MultiGameSharedSubtreeSegments.Contains(trimmed))
        {
            return false;
        }

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
        if (norm.Length < 4 || GenericSegmentNames.Contains(norm))
        {
            return false;
        }

        CanonicalTitleProfile segmentProfile = BuildCanonicalProfile(trimmed, allowTrailingSaveFolderSuffix: true);
        CanonicalTitleProfile? primaryTitleProfile = ResolveAuthoritativeReferenceProfile(identity);

        foreach (string exact in identity.ExactNames)
        {
            if (IsGenericOrWrapperExecutableStem(exact))
            {
                continue;
            }

            string ne = NormalizeName(exact);
            if (ne.Length < 4 || GenericSegmentNames.Contains(ne) || !string.Equals(norm, ne, StringComparison.Ordinal))
            {
                continue;
            }

            // Enforce primary title remaster/bundle/installment vetoes on ExactNames too so remaster binaries
            // (e.g. SanAndreas.exe, ViceCity.exe, Crysis.exe, Mafia2.exe) never match original classic save folders!
            if (primaryTitleProfile is { } primary && !HasCompatibleMarkersAndInstallments(segmentProfile, primary))
            {
                continue;
            }

            return true;
        }

        string normSegmentWithoutSaveSuffix = StripSafeSaveFolderSuffixNormalized(norm);

        foreach (string product in identity.ProductNames)
        {
            if (ProtectedSharedContainerLeafNames.Contains(product))
            {
                continue;
            }

            string np = NormalizeName(product);
            if (np.Length < 4 || GenericSegmentNames.Contains(np))
            {
                continue;
            }

            CanonicalTitleProfile productProfile = BuildCanonicalProfile(product, allowTrailingSaveFolderSuffix: false);

            if (string.Equals(norm, np, StringComparison.Ordinal))
            {
                if (primaryTitleProfile is { } primary && !HasCompatibleMarkersAndInstallments(segmentProfile, primary))
                {
                    continue;
                }

                return true;
            }

            // Universal conflict veto: never cross-match trilogy/anthology bundles vs individual titles,
            // original releases vs remasters/definitive editions, or differing sequel/installment numbers!
            if (!HasCompatibleMarkersAndInstallments(segmentProfile, productProfile))
            {
                continue;
            }

            if (primaryTitleProfile is { } primaryCheck && !HasCompatibleMarkersAndInstallments(segmentProfile, primaryCheck))
            {
                continue;
            }

            // Guarded platform-tag & save-folder suffix match (e.g. "LIMBO (GOG)" <-> "Limbo", "Limbo" <-> "LimboSaves")
            // without permitting arbitrary subtitle prefixes/suffixes (e.g. "Vice City" != "Vice City Stories").
            string npWithoutStoreTag = NormalizeName(StripParenthesizedPlatformTags(product));
            if (npWithoutStoreTag.Length >= 4 && !GenericSegmentNames.Contains(npWithoutStoreTag))
            {
                if (string.Equals(norm, npWithoutStoreTag, StringComparison.Ordinal))
                {
                    return true;
                }

                if (normSegmentWithoutSaveSuffix.Length >= 4
                    && string.Equals(normSegmentWithoutSaveSuffix, npWithoutStoreTag, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            // Canonical token-sequence equivalence: supports unambiguous franchise initialisms ("Grand Theft Auto" <-> "GTA",
            // "Red Dead Redemption" <-> "RDR"), Roman/Arabic installment normalization ("III" <-> "3"), and optional English
            // articles ("the"), strictly requiring >= 2 distinguishing core tokens and exact core/subtitle/edition agreement.
            if (DoesCanonicalTokenProfileMatch(segmentProfile, productProfile))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? ResolveUsableEnglishTitleCandidate(string? rawCandidate, string? primaryTitle)
    {
        if (string.IsNullOrWhiteSpace(rawCandidate))
        {
            return null;
        }

        string? cleaned = GameTitleCleaner.CleanEnglishTitle(rawCandidate)
            ?? (GameTitleCleaner.IsLikelyEnglishTitle(rawCandidate) ? rawCandidate.Trim() : null);

        if (string.IsNullOrWhiteSpace(cleaned)
            || string.Equals(cleaned, "Unknown Game", StringComparison.OrdinalIgnoreCase)
            || !GameTitleCleaner.IsLikelyEnglishTitle(cleaned))
        {
            return null;
        }

        if (NormalizeName(cleaned).Length < 4 || ProtectedSharedContainerLeafNames.Contains(cleaned))
        {
            return null;
        }

        CanonicalTitleProfile candidateProfile = BuildCanonicalProfile(cleaned, allowTrailingSaveFolderSuffix: false);
        if (!candidateProfile.CoreTokens.Any(static token => !token.All(char.IsAsciiDigit)))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(primaryTitle) && GameTitleCleaner.ContainsCjkCharacters(primaryTitle))
        {
            CanonicalTitleProfile cjkProfile = BuildCjkFallbackReferenceProfile(primaryTitle);
            if (!IsEnglishCandidateConsistentWithCjkTitle(candidateProfile, cjkProfile))
            {
                return null;
            }
        }

        return cleaned;
    }

    private static bool IsEnglishCandidateConsistentWithCjkTitle(CanonicalTitleProfile englishProfile, CanonicalTitleProfile cjkProfile)
    {
        if (!englishProfile.BundleMarkers.SetEquals(cjkProfile.BundleMarkers)
            || !englishProfile.RemasterMarkers.SetEquals(cjkProfile.RemasterMarkers))
        {
            return false;
        }

        return cjkProfile.Installments.Count == 0
               || englishProfile.Installments.SequenceEqual(cjkProfile.Installments, StringComparer.Ordinal);
    }

    private static bool HasCompatibleMarkersAndInstallments(CanonicalTitleProfile left, CanonicalTitleProfile right) =>
        left.BundleMarkers.SetEquals(right.BundleMarkers)
        && left.RemasterMarkers.SetEquals(right.RemasterMarkers)
        && left.Installments.SequenceEqual(right.Installments);

    private static CanonicalTitleProfile? ResolveAuthoritativeReferenceProfile(GameDataTargetIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(identity.Title) && GameTitleCleaner.IsLikelyEnglishTitle(identity.Title))
        {
            return BuildCanonicalProfile(identity.Title, allowTrailingSaveFolderSuffix: false);
        }

        if (!string.IsNullOrWhiteSpace(identity.EnglishTitle) && GameTitleCleaner.IsLikelyEnglishTitle(identity.EnglishTitle))
        {
            return BuildCanonicalProfile(identity.EnglishTitle, allowTrailingSaveFolderSuffix: false);
        }

        if (!string.IsNullOrWhiteSpace(identity.InstallFolderTitle) && GameTitleCleaner.IsLikelyEnglishTitle(identity.InstallFolderTitle))
        {
            return BuildCanonicalProfile(identity.InstallFolderTitle, allowTrailingSaveFolderSuffix: false);
        }

        if (!string.IsNullOrWhiteSpace(identity.Title) && GameTitleCleaner.ContainsCjkCharacters(identity.Title))
        {
            return BuildCjkFallbackReferenceProfile(identity.Title);
        }

        return null;
    }

    private static CanonicalTitleProfile BuildCjkFallbackReferenceProfile(string rawCjkTitle)
    {
        string cleaned = GameTitleCleaner.CleanTitle(rawCjkTitle, preserveRemasterEdition: true);
        var installments = new List<string>(2);
        var remasterMarkers = new HashSet<string>(StringComparer.Ordinal);
        var bundleMarkers = new HashSet<string>(StringComparer.Ordinal);

        // Extract explicit CJK remaster/edition qualifiers (never mapping in-word CJK numerals like 四海兄弟 / 影逝二度 to Installments!).
        if (cleaned.Contains("决定版", StringComparison.Ordinal)
            || cleaned.Contains("最终版", StringComparison.Ordinal)
            || cleaned.Contains("決定版", StringComparison.Ordinal)
            || cleaned.Contains("最終版", StringComparison.Ordinal))
        {
            remasterMarkers.Add("definitive");
        }

        if (cleaned.Contains("重制版", StringComparison.Ordinal)
            || cleaned.Contains("高清重制", StringComparison.Ordinal)
            || cleaned.Contains("复刻版", StringComparison.Ordinal)
            || cleaned.Contains("重製版", StringComparison.Ordinal)
            || cleaned.Contains("復刻版", StringComparison.Ordinal))
        {
            remasterMarkers.Add("remastered");
        }

        if (cleaned.Contains("周年纪念版", StringComparison.Ordinal)
            || cleaned.Contains("周年版", StringComparison.Ordinal)
            || cleaned.Contains("週年紀念版", StringComparison.Ordinal))
        {
            remasterMarkers.Add("anniversary");
        }

        if (cleaned.Contains("增强版", StringComparison.Ordinal)
            || cleaned.Contains("次世代版", StringComparison.Ordinal)
            || cleaned.Contains("增強版", StringComparison.Ordinal))
        {
            remasterMarkers.Add("enhanced");
        }

        if (cleaned.Contains("三部曲", StringComparison.Ordinal))
        {
            bundleMarkers.Add("trilogy");
        }

        if (cleaned.Contains("选集", StringComparison.Ordinal) || cleaned.Contains("選集", StringComparison.Ordinal))
        {
            bundleMarkers.Add("anthology");
        }

        if (cleaned.Contains("合集", StringComparison.Ordinal) || cleaned.Contains("收藏集", StringComparison.Ordinal))
        {
            bundleMarkers.Add("collection");
        }

        // Also inspect embedded ASCII tokens (e.g. "III" in "侠盗猎车手III：决定版", "II" in "四海兄弟 II：决定版", "2077" in "赛博朋克 2077").
        // AlphanumericTokenRegex splits strictly between ASCII [A-Za-z0-9]+ runs and surrounding CJK characters!
        foreach (Match match in AlphanumericTokenRegex().Matches(cleaned))
        {
            string lower = match.Groups[1].Value.ToLowerInvariant();
            if (lower is "trilogy" or "anthology" or "collection")
            {
                bundleMarkers.Add(lower);
                continue;
            }

            if (TryMapRemasterMarker(lower, out string? mappedRemaster))
            {
                remasterMarkers.Add(mappedRemaster!);
                continue;
            }

            if (RomanToArabicInstallments.TryGetValue(lower, out string? romanDigits))
            {
                installments.Add(romanDigits);
                continue;
            }

            if (lower.All(char.IsAsciiDigit))
            {
                string normDigits = lower.TrimStart('0');
                installments.Add(normDigits.Length == 0 ? "0" : normDigits);
            }
        }

        return new CanonicalTitleProfile([], installments, remasterMarkers, bundleMarkers);
    }

    private static bool DoesCanonicalTokenProfileMatch(CanonicalTitleProfile segment, CanonicalTitleProfile product)
    {
        if (segment.CoreTokens.Count < 2 || segment.CoreTokens.Count != product.CoreTokens.Count)
        {
            return false;
        }

        if (!segment.CoreTokens.SequenceEqual(product.CoreTokens, StringComparer.Ordinal))
        {
            return false;
        }

        int totalCoreChars = 0;
        foreach (string token in segment.CoreTokens)
        {
            totalCoreChars += token.Length;
        }

        return totalCoreChars >= 4;
    }

    private static CanonicalTitleProfile BuildCanonicalProfile(string rawText, bool allowTrailingSaveFolderSuffix)
    {
        string working = StripParenthesizedPlatformTags(rawText);

        // Expand trailing "- DE" / "- The DE" shorthand consistently with GameTitleCleaner.
        if (TrailingShorthandDeRegex().IsMatch(working))
        {
            working = TrailingShorthandDeRegex().Replace(working, " Definitive Edition");
        }

        // Collapse apostrophes inside words ("Assassin's" -> "Assassins") and dotted initials ("L.A." -> "LA").
        working = InWordApostropheRegex().Replace(working, "$1$2");
        working = DottedInitialsRegex().Replace(working, m => m.Value.Replace(".", string.Empty, StringComparison.Ordinal));

        var rawTokens = new List<string>(8);
        foreach (Match match in AlphanumericTokenRegex().Matches(working))
        {
            string lower = match.Groups[1].Value.ToLowerInvariant();
            var fused = FusedFranchiseInstallmentRegex().Match(lower);
            if (fused.Success)
            {
                rawTokens.Add(fused.Groups[1].Value);
                rawTokens.Add(fused.Groups[2].Value);
            }
            else
            {
                rawTokens.Add(lower);
            }
        }

        // Collapse unambiguous multi-word franchise names to their single canonical franchise unit token
        // so that an acronym ("GTA") counts as 1 logical token rather than 3.
        var collapsed = CollapseFranchisePhrases(rawTokens);

        if (allowTrailingSaveFolderSuffix)
        {
            TrimTrailingSaveFolderSuffixTokens(collapsed);
        }

        var coreTokens = new List<string>(collapsed.Count);
        var installments = new List<string>(2);
        var remasterMarkers = new HashSet<string>(StringComparer.Ordinal);
        var bundleMarkers = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < collapsed.Count; i++)
        {
            string token = collapsed[i];

            if (token is "the" or "a" or "an")
            {
                continue;
            }

            if (token is "trilogy" or "anthology" or "collection")
            {
                bundleMarkers.Add(token);
                continue;
            }

            if (TryMapRemasterMarker(token, out string? canonicalRemaster))
            {
                remasterMarkers.Add(canonicalRemaster!);
                if (i + 1 < collapsed.Count && collapsed[i + 1] == "edition")
                {
                    i++;
                }

                continue;
            }

            if (RomanToArabicInstallments.TryGetValue(token, out string? romanDigits))
            {
                installments.Add(romanDigits);
                coreTokens.Add(romanDigits);
                continue;
            }

            if (token.All(char.IsAsciiDigit))
            {
                string normDigits = token.TrimStart('0');
                if (normDigits.Length == 0)
                {
                    normDigits = "0";
                }

                installments.Add(normDigits);
                coreTokens.Add(normDigits);
                continue;
            }

            coreTokens.Add(token);
        }

        return new CanonicalTitleProfile(coreTokens, installments, remasterMarkers, bundleMarkers);
    }

    private static List<string> CollapseFranchisePhrases(List<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return tokens;
        }

        var output = new List<string>(tokens.Count);
        int i = 0;
        while (i < tokens.Count)
        {
            if (i + 2 < tokens.Count
                && tokens[i] == "grand"
                && tokens[i + 1] == "theft"
                && tokens[i + 2] == "auto")
            {
                output.Add("gta");
                i += 3;
                continue;
            }

            if (i + 2 < tokens.Count
                && tokens[i] == "red"
                && tokens[i + 1] == "dead"
                && tokens[i + 2] == "redemption")
            {
                output.Add("rdr");
                i += 3;
                continue;
            }

            output.Add(tokens[i]);
            i++;
        }

        return output;
    }

    private static void TrimTrailingSaveFolderSuffixTokens(List<string> tokens)
    {
        // Strip trailing multi-word or single-word save directory suffixes (e.g. "GTA San Andreas User Files" -> "GTA San Andreas")
        // only when at least one distinguishing token precedes the suffix.
        if (tokens.Count >= 3
            && tokens[^2] is "user" or "save"
            && tokens[^1] is "files" or "data" or "games")
        {
            tokens.RemoveRange(tokens.Count - 2, 2);
            return;
        }

        if (tokens.Count >= 2 && tokens[^1] is "saves" or "savedata" or "savegames" or "userfiles")
        {
            tokens.RemoveAt(tokens.Count - 1);
        }
    }

    private static string StripSafeSaveFolderSuffixNormalized(string normalized)
    {
        ReadOnlySpan<string> suffixes = ["savedata", "savegames", "savegame", "userfiles", "saves", "save"];
        foreach (string suffix in suffixes)
        {
            if (normalized.Length >= suffix.Length + 4 && normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                return normalized[..^suffix.Length];
            }
        }

        return normalized;
    }

    private static string StripParenthesizedPlatformTags(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ParenthesizedPlatformTagRegex().Replace(value, " ");

    private static bool TryMapRemasterMarker(string token, out string? canonical)
    {
        canonical = token switch
        {
            "definitive" => "definitive",
            "remaster" or "remastered" => "remastered",
            "anniversary" => "anniversary",
            "enhanced" => "enhanced",
            "redux" => "redux",
            _ => null,
        };
        return canonical is not null;
    }

    private static string NormalizeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private readonly record struct CanonicalTitleProfile(
        IReadOnlyList<string> CoreTokens,
        IReadOnlyList<string> Installments,
        HashSet<string> RemasterMarkers,
        HashSet<string> BundleMarkers);

    [GeneratedRegex(@"[\(\[\{]\s*(?:GOG(?:\.com)?|Steam|SteamRIP|Epic|Windows|PC|Win64|Win32|x64|x86)\s*[\)\]\}]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParenthesizedPlatformTagRegex();

    [GeneratedRegex(@"(?:[\s\._]*[-–—][\s\._]*)(?:(?:[Tt]he)[\s\._\-]+)?DE\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingShorthandDeRegex();

    [GeneratedRegex(@"([A-Za-z])['’]([A-Za-z])", RegexOptions.CultureInvariant)]
    private static partial Regex InWordApostropheRegex();

    [GeneratedRegex(@"\b(?:[A-Za-z]\.){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DottedInitialsRegex();

    [GeneratedRegex(@"([A-Za-z0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AlphanumericTokenRegex();

    [GeneratedRegex(@"^(gta|rdr)(\d+|ii|iii|iv|v|vi)$", RegexOptions.CultureInvariant)]
    private static partial Regex FusedFranchiseInstallmentRegex();

    private static readonly Dictionary<string, string> RomanToArabicInstallments = new(StringComparer.Ordinal)
    {
        ["ii"] = "2",
        ["iii"] = "3",
        ["iv"] = "4",
        ["v"] = "5",
        ["vi"] = "6",
        ["vii"] = "7",
        ["viii"] = "8",
        ["ix"] = "9",
        ["x"] = "10",
        ["xi"] = "11",
        ["xii"] = "12",
        ["xiii"] = "13",
        ["xiv"] = "14",
        ["xv"] = "15",
        ["xvi"] = "16",
    };

    private static readonly HashSet<string> ProtectedSharedContainerLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam",
        "SteamApps",
        "Steamworks",
        "common",
        "CODEX",
        "RUNE",
        "EMPRESS",
        "SKIDROW",
        "Hoodlum",
        "SmartSteamEmu",
        "OnlineFix",
        "Goldberg SteamEmu Saves",
        "GSE Saves",
        "Goldberg SocialClub Emu Saves",
        "Social Club",
        "GTA_DEF",
        "My Games",
        "Rockstar",
        "Rockstar Games",
        "Capcom",
        "Ubisoft",
        "Ubisoft Game Launcher",
        "Square Enix",
        "EA",
        "EA Games",
        "EA Desktop",
        "Origin",
        "Electronic Arts",
        "Epic",
        "Epic Games",
        "EpicGamesLauncher",
        "Riot Games",
        "Blizzard Entertainment",
        "Battle.net",
        "GOG.com",
        "GOG Galaxy",
        "FromSoftware",
        "Bandai Namco",
        "Bandai Namco Entertainment",
        "2K Games",
        "Take-Two Interactive",
        "Bethesda Softworks",
        "Activision",
        "SEGA",
        "KoeiTecmo",
        "KOEI TECMO",
        "Konami",
        "CD Projekt Red",
        "THQ Nordic",
        "Paradox Interactive",
        "WB Games",
        "Warner Bros. Interactive Entertainment",
        "RenPy",
        "Godot",
        "app_userdata",
        "LOVE",
        "RPG Maker",
        "RPG Maker MV",
        "RPG Maker MZ",
        "GameMaker",
        "GameMakerStudio2",
        "NWJS",
        "Electron",
        "Unity",
        "UnrealEngine",
        "Gameface",
        "UE4Game",
        "UE5Game",
        "UnrealGame",
        "DefaultCompany",
    };

    private static readonly HashSet<string> MultiGameSharedSubtreeSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTA_DEF",
    };

    private static readonly HashSet<string> ForbiddenExecutableStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "renpy",
        "godot",
        "love",
        "nwjs",
        "electron",
        "gamemaker",
        "gamemakerstudio",
        "gamemakerstudio2",
        "rpgmaker",
        "rpgmakermv",
        "rpgmakermz",
        "unityplayer",
        "bootstrappackagedgame",
        "rockstarredirector",
        "rockstargameslauncher",
        "compactautorunner",
        "autorunner",
        "ue4prereqsetup",
        "ueprereqsetup",
        "prereqsetup",
        "crashreportclient",
        "unrealcefsubprocess",
        "unitycrashhandler",
        "unitycrashhandler32",
        "unitycrashhandler64",
        "socialclubsetup",
        "social-club-setup",
        "steamerrorreporter",
    };

    private static readonly HashSet<string> GenericSegmentNames = new(StringComparer.Ordinal)
    {
        "game", "games", "mygames", "savedgames", "savegames", "savegame", "saves", "save", "savedata", "saved",
        "steam", "steamapps", "steamworks", "common", "userdata", "user", "users", "data", "config", "configs", "settings",
        "local", "locallow", "roaming", "appdata", "documents", "public", "programdata", "launcher", "bin",
        "win64", "win32", "x64", "x86", "binaries", "shipping", "client", "app", "application", "remote",
        "codex", "rune", "empress", "skidrow", "hoodlum", "gsesaves", "goldbergsteamemusaves", "smartsteamemu",
        "onlinefix", "valve", "ali213", "temp", "cache", "logs", "unity", "unrealengine", "engine",
        "profile", "profiles", "windowsnoeditor", "windows", "crashreportclient", "ngx",
        "gameface", "ue4game", "ue5game", "unrealgame", "defaultcompany",
        "renpy", "godot", "appuserdata", "love", "nwjs", "electron", "gamemaker", "gamemakerstudio", "gamemakerstudio2",
        "rpgmaker", "rpgmakermv", "rpgmakermz", "unityplayer", "bootstrappackagedgame", "rockstarredirector",
        "compactautorunner", "autorunner", "ue4prereqsetup", "ueprereqsetup", "unrealcefsubprocess", "unitycrashhandler",
        "goldbergsocialclubemusaves", "socialclub", "gtadef",
        "rockstargames", "rockstar", "capcom", "ubisoft", "ubisoftgamelauncher", "squareenix",
        "ea", "eagames", "eadesktop", "origin", "electronicarts", "epic", "epicgames", "epicgameslauncher",
        "riotgames", "blizzard", "blizzardentertainment", "battlenet", "gogcom", "goggalaxy",
        "fromsoftware", "bandainamco", "bandainamcoentertainment",
        "2kgames", "taketwo", "taketwointeractive", "bethesda", "bethesdasoftworks", "activision",
        "sega", "koeitecmo", "konami", "cdprojektred", "thqnordic", "paradoxinteractive", "wbgames",
        "definitiveedition", "definitive", "remastered", "remaster", "anniversaryedition", "edition",
        "trilogy", "anthology", "collection", "main", "play", "boot", "init", "core", "host", "stub", "start", "setup",
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
