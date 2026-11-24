using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Launcher;

/// <summary>
/// Configuration options controlling offline/online behavior during game directory scanning.
/// </summary>
public sealed class GameScanOptions
{
    /// <summary>
    /// Whether to perform short-timeout online enrichment (Steam <c>storesearch</c> / CDN covers / GOG Product API).
    /// Offline deterministic detection (<c>goggame-*.info</c>, <c>webcache.zip</c>, <c>steam_appid.txt</c>, emulator INIs) always runs first.
    /// </summary>
    public bool AllowOnlineMetadataLookup { get; set; } = true;

    /// <summary>
    /// Per-game upper bound for optional online metadata/artwork queries so offline environments never stall.
    /// </summary>
    public TimeSpan OnlineLookupTimeout { get; set; } = TimeSpan.FromSeconds(3.5);
}

/// <summary>
/// Parsed Steam <c>appmanifest_&lt;appid&gt;.acf</c> identity and version build metadata.
/// </summary>
public sealed record SteamAppManifestMetadata(
    int? AppId,
    string? Name,
    string? InstallDir,
    string? BuildId,
    DateTimeOffset? LastUpdatedUtc);

/// <summary>
/// Intermediate scan result produced by <see cref="GameDirectoryScanner"/> before persistence.
/// </summary>
public sealed record ScannedGameDescriptor
{
    public required string InstallDir { get; init; }
    public required string ExecutableRelPath { get; init; }
    public required string Title { get; init; }
    public required string SortTitle { get; init; }
    public required PlatformSource PlatformSource { get; init; }
    public required AntiCheatTier AntiCheatTier { get; init; }
    public string? AntiCheatSignature { get; init; }
    public int? SteamAppId { get; init; }
    public string? GogGameId { get; init; }
    public string? GogClientId { get; init; }
    public AchievementCaptureMode AchievementCaptureMode { get; init; }
    public string? AchievementChannelLabel { get; init; }
    public string? CoverImagePath { get; init; }
    public string? IconPath { get; init; }
    public string? HeroImagePath { get; init; }
    public string? SteamBuildId { get; init; }
    public DateTimeOffset? SteamLastUpdatedUtc { get; init; }
    public string? EnglishTitle { get; init; }
    public TrainerTitleSource EnglishTitleSource { get; init; } = TrainerTitleSource.Unknown;
}

/// <summary>
/// Intelligent multi-mode game directory scanner supporting GOG Offline backups (<c>goggame-*.info</c> + <c>webcache.zip</c>),
/// Steamrip / portable emulators (4-stage deterministic + <c>storesearch</c> <c>SteamAppId</c> discovery),
/// Steam local libraries, and standalone executables.
/// </summary>
public sealed partial class GameDirectoryScanner
{
    /// <summary>
    /// Deterministic cross-platform GOG Product ID -&gt; Steam AppId lookup table for pure DRM-free GOG titles
    /// so localized Chinese achievement schemas &amp; vertical posters resolve instantaneously even offline.
    /// </summary>
    private static readonly Dictionary<string, int> KnownGogToSteamAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1724299977"] = 48000,   // LIMBO
        ["1454587428"] = 304430,  // INSIDE
        ["1308320804"] = 367520,  // Hollow Knight
        ["1423058311"] = 504230,  // Celeste
        ["1453375253"] = 413150,  // Stardew Valley
        ["1207664643"] = 292030,  // The Witcher 3: Wild Hunt
        ["1423049311"] = 1091500, // Cyberpunk 2077
        ["1760710426"] = 1145360, // Hades
        ["1209328603"] = 588650,  // Dead Cells
        ["1207665893"] = 105600,  // Terraria
        ["1435828767"] = 391540,  // Undertale
        ["1963513391"] = 268910,  // Cuphead
        ["1771589310"] = 632470,  // Disco Elysium
        ["1456460669"] = 1086940, // Baldur's Gate 3
        ["1599361708"] = 460950,  // Katana ZERO
        ["1207658924"] = 108710,  // Alan Wake
        ["2049187585"] = 870780,  // Control
        ["1405725402"] = 774361,  // Blasphemous
    };

    private static readonly HashSet<string> NativeSaveSupportedGogIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1724299977", // LIMBO (savegame.txt native achievement bit/index tracking)
    };

    private static readonly HashSet<string> SkippedSubdirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        ".local",
        "$RECYCLE.BIN",
        "System Volume Information",
        "_CommonRedist",
        "CommonRedist",
        "Redist",
        "Redistributables",
        "DirectX",
        "__Installer",
        "Support",
        "MonoBleedingEdge",
        "SPAWN_Vault",
        "_OST",
        "OST",
        "Soundtrack",
        "StreamingAssets",
        "usd",
        "locales",
        "ShaderCache",
        "Shaders",
        "DerivedDataCache",
        "Paks",
        "CrashReportClient",
        "Movies",
        "Videos",
    };

    private static readonly HashSet<string> StructuralSingleGameSubdirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Engine",
        "Binaries",
        "bin",
        "Win64",
        "Win32",
        "WinGDK",
        "x64",
        "x86",
        "Content",
        "Data",
        "GameData",
        "SteamData",
        "steam_settings",
        "Plugins",
        "Mods",
        "DLC",
        "Bonus",
        "Extras",
        "Tools",
        "ModTools",
        "SDK",
        "Server",
        "DedicatedServer",
        "Editor",
        "Launcher",
        "Saves",
        "SaveGames",
        "Profiles",
        "Config",
        "Logs",
    };

    private static readonly string[] TopLevelSingleGameEmulatorInis =
    [
        "OnlineFix.ini",
        "steam_emu.ini",
        "tenoke.ini",
        "codex.ini",
        "rune.ini",
        "flt.ini",
        "ColdClientLoader.ini",
        "ALI213.ini",
        "Valve.ini",
        "3dmgame.ini",
        "hlm.ini",
        "skidrow.ini",
        "SmartSteamEmu.ini",
    ];

    [GeneratedRegex(@"^goggame-(\d+)\.info$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GogInfoFileNameRegex();

    [GeneratedRegex(
        @"^(?:UnityCrashHandler(?:32|64)?|UnrealCEFSubProcess|CrashReportClient(?:Editor)?|CrashSender\d*|crs-handler|ZFGameBrowser|cef_?process|steamerrorreporter(?:64)?|unins\d+|uninstall.*|vc_?redist.*|dxsetup|dxwebsetup|UE4?PrereqSetup.*|UnrealPrereq.*|BootstrapPackagedGame.*|Rockstar-Games-Launcher.*|Rockstar-Games-Epic.*|RockstarRedirector.*|Social-Club-Setup.*|SocialClubSetup.*|Redist(?:Setup|ributable)?.*|ndp4.*|dotnet.*|oalinst|PhysX.*|language_setup|gogdosbox|EasyAntiCheat(?:_EOS|_Setup)?|BEService(?:_x64)?|BEClient.*|SGuard(?:Svc)?64|createdump)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedExecutableRegex();

    [GeneratedRegex(
        @"(?:^|[\s\._\-])(?:launcher|autorun|autorunner|menu|redirector|bootstrapper|prereq|prerequisite|setup|install|installer|updater|patcher|config|configuration|settings|benchmark|dedicatedserver|server|crash|reporter|modmanager|editor)(?:$|[\s\._\-]|\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PenalizedExecutableStemRegex();

    [GeneratedRegex(@"""appid""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfAppIdRegex();

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfInstallDirRegex();

    [GeneratedRegex(@"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfNameRegex();

    [GeneratedRegex(@"""buildid""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfBuildIdRegex();

    [GeneratedRegex(@"""LastUpdated""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfLastUpdatedRegex();

    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly GameArtworkExtractor _artworkExtractor;
    private readonly HttpClient _httpClient;
    private readonly GameScanOptions _options;
    private readonly ILogger<GameDirectoryScanner> _logger;

    public GameDirectoryScanner(
        IAntiCheatGuardService antiCheatGuard,
        GameArtworkExtractor artworkExtractor,
        HttpClient? httpClient = null,
        GameScanOptions? options = null,
        ILogger<GameDirectoryScanner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(antiCheatGuard);
        ArgumentNullException.ThrowIfNull(artworkExtractor);
        _antiCheatGuard = antiCheatGuard;
        _artworkExtractor = artworkExtractor;
        _httpClient = httpClient ?? GameArtworkExtractor.CreateDefaultHttpClient();
        _options = options ?? new GameScanOptions();
        _logger = logger ?? NullLogger<GameDirectoryScanner>.Instance;
    }

    /// <summary>
    /// Resolves a user-supplied folder path or <c>.exe</c> path into a canonical game installation root
    /// and an optional explicit relative executable path.
    /// </summary>
    public static (string InstallDir, string? ExplicitExeRelPath)? ResolveImportTarget(string directoryOrExePath)
    {
        if (string.IsNullOrWhiteSpace(directoryOrExePath))
        {
            return null;
        }

        string trimmed = directoryOrExePath.Trim().Trim('"');
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trimmed);
        }
        catch
        {
            return null;
        }

        if (File.Exists(fullPath))
        {
            if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string exeDir = Path.GetDirectoryName(fullPath)!;
            string inferredRoot = InferGameRootFromExecutable(fullPath, exeDir);
            string relExe = Path.GetRelativePath(inferredRoot, fullPath);
            return (Path.TrimEndingDirectorySeparator(inferredRoot), relExe);
        }

        if (Directory.Exists(fullPath))
        {
            string unwrapped = UnwrapSingleNestedFolderIfNeeded(fullPath);
            return (Path.TrimEndingDirectorySeparator(unwrapped), null);
        }

        return null;
    }

    /// <summary>
    /// Discovers candidate game installation folders inside <paramref name="rootDirectory"/>.
    /// Multi-game anthology/trilogy containers (holding <c>&gt;= 2</c> independent standalone child games without
    /// a single-game engine root at the parent) are automatically expanded into their standalone child game roots.
    /// If <paramref name="rootDirectory"/> itself is a single game folder, returns <c>[rootDirectory]</c>.
    /// </summary>
    public IReadOnlyList<string> DiscoverCandidateGameDirectories(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));

        // Expand multi-game anthology/trilogy containers before checking top-level menu Launcher.exe.
        var expandedRootChildren = TryExpandMultiGameContainer(normalizedRoot);
        if (expandedRootChildren.Count >= 2)
        {
            return expandedRootChildren;
        }

        // Check if rootDirectory itself is directly a single game folder (e.g. contains goggame-*.info, steam_appid.txt, or top-level game exe).
        if (LooksLikeDirectGameDirectory(normalizedRoot))
        {
            return [normalizedRoot];
        }

        var discovered = new List<string>();
        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(normalizedRoot, "*", enumOpts))
            {
                string folderName = Path.GetFileName(subDir);
                if (SkippedSubdirectoryNames.Contains(folderName))
                {
                    continue;
                }

                string candidate = UnwrapSingleNestedFolderIfNeeded(subDir);
                var expandedChildren = TryExpandMultiGameContainer(candidate);
                if (expandedChildren.Count >= 2)
                {
                    discovered.AddRange(expandedChildren);
                    continue;
                }

                if (HasAnyValidGameSignal(candidate))
                {
                    discovered.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)));
                }
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Error enumerating library root directory {RootDirectory}.", normalizedRoot);
            }
        }

        return discovered
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Inspects <paramref name="directoryPath"/> to determine whether it is a multi-game anthology/pack container
    /// (lacking its own game engine/store identity at depth 0 while containing <c>&gt;= 2</c> independent standalone
    /// child game directories). Returns the expanded standalone child directories, or an empty list if <paramref name="directoryPath"/>
    /// is a single game or non-collection folder.
    /// </summary>
    public static IReadOnlyList<string> TryExpandMultiGameContainer(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        }
        catch
        {
            return [];
        }

        if (HasSingleGameEngineOrStoreRootIdentity(normalized))
        {
            return [];
        }

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var childCandidates = new List<(string Path, int? SteamAppId, string? GogGameId)>();
        try
        {
            foreach (string subDir in Directory.EnumerateDirectories(normalized, "*", enumOpts))
            {
                string folderName = Path.GetFileName(subDir);
                if (SkippedSubdirectoryNames.Contains(folderName)
                    || StructuralSingleGameSubdirectoryNames.Contains(folderName)
                    || folderName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string unwrapped = Path.TrimEndingDirectorySeparator(Path.GetFullPath(UnwrapSingleNestedFolderIfNeeded(subDir)));
                if (IsIndependentStandaloneChildGame(unwrapped, out int? childAppId, out string? childGogId))
                {
                    childCandidates.Add((unwrapped, childAppId, childGogId));
                }
            }
        }
        catch
        {
            return [];
        }

        if (childCandidates.Count < 2)
        {
            return [];
        }

        // Guard against splitting a single game's Client/ + Server/ folders that share an identical non-Spacewar AppId/GogGameId.
        var nonSpacewarSteamIds = childCandidates
            .Where(c => c.SteamAppId is > 0 and not 480)
            .Select(c => c.SteamAppId!.Value)
            .ToList();
        if (nonSpacewarSteamIds.Count >= 2 && nonSpacewarSteamIds.Distinct().Count() == 1)
        {
            return [];
        }

        var gogIds = childCandidates
            .Where(c => !string.IsNullOrWhiteSpace(c.GogGameId))
            .Select(c => c.GogGameId!)
            .ToList();
        if (gogIds.Count >= 2 && gogIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            return [];
        }

        return childCandidates
            .Select(c => c.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Scans a single game installation folder, extracts offline/online metadata &amp; cached artwork,
    /// and classifies anti-cheat tier and achievement capture channel.
    /// </summary>
    public async Task<ScannedGameDescriptor?> ScanGameDirectoryAsync(
        string installDir,
        string targetGameId,
        string? explicitExeRelPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(targetGameId);
        string normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));

        // 1. Inspect Anti-Cheat Tier first via Spawn.Core.AntiCheatGuardService.
        var acResult = _antiCheatGuard.InspectGameDirectory(normalizedInstallDir);

        // 2. Check for GOG Offline installer descriptor (goggame-<gameId>.info).
        var gogInfo = TryParseGogInfo(normalizedInstallDir);
        if (gogInfo is not null)
        {
            return await BuildGogGameDescriptorAsync(
                normalizedInstallDir,
                targetGameId,
                gogInfo,
                explicitExeRelPath,
                acResult,
                cancellationToken).ConfigureAwait(false);
        }

        // 3. Steamrip / SteamLocal / Standalone pipeline.
        return await BuildSteamOrStandaloneDescriptorAsync(
            normalizedInstallDir,
            targetGameId,
            explicitExeRelPath,
            acResult,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScannedGameDescriptor?> BuildGogGameDescriptorAsync(
        string installDir,
        string targetGameId,
        ParsedGogInfo gogInfo,
        string? explicitExeRelPath,
        AntiCheatInspectionResult acResult,
        CancellationToken cancellationToken)
    {
        string? exeRelPath = ResolveValidatedExeRelPath(installDir, explicitExeRelPath)
            ?? ResolveValidatedExeRelPath(installDir, gogInfo.PrimaryTaskRelPath)
            ?? SelectBestMainExecutable(installDir);

        if (string.IsNullOrWhiteSpace(exeRelPath))
        {
            return null;
        }

        string exeFullPath = Path.GetFullPath(Path.Combine(installDir, exeRelPath));
        string title = !string.IsNullOrWhiteSpace(gogInfo.Name)
            ? gogInfo.Name.Trim()
            : GameTitleCleaner.CleanTitle(Path.GetFileName(installDir));

        // Cross-map SteamAppId when known (e.g., LIMBO 1724299977 -> 48000) or via local steam_appid / online exact match.
        int? steamAppId = null;
        if (!string.IsNullOrWhiteSpace(gogInfo.GameId)
            && KnownGogToSteamAppIds.TryGetValue(gogInfo.GameId, out int mappedSteamId))
        {
            steamAppId = mappedSteamId;
        }
        else
        {
            var localSteamProbe = ProbeLocalSteamIdentity(installDir);
            steamAppId = localSteamProbe.SteamAppId;
        }

        // Instant zero-network artwork extraction from webcache.zip (or goggame-*.ico / EXE icon).
        var artwork = _artworkExtractor.ExtractGogOfflineArtwork(
            installDir,
            targetGameId,
            gogInfo.GameId,
            exeFullPath);

        if (_options.AllowOnlineMetadataLookup)
        {
            if (!steamAppId.HasValue)
            {
                var onlineMatch = await TrySearchSteamStoreAsync([title], _options.OnlineLookupTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (onlineMatch is { Score: >= 0.90 })
                {
                    steamAppId = onlineMatch.Value.AppId;
                }
            }

            artwork = await _artworkExtractor.EnrichOnlineArtworkAsync(
                targetGameId,
                steamAppId,
                gogInfo.GameId,
                artwork,
                _options.OnlineLookupTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        var (captureMode, channelLabel) = ClassifyGogAchievementChannel(installDir, gogInfo.GameId);

        string? englishTitle = !string.IsNullOrWhiteSpace(gogInfo.Name)
            ? gogInfo.Name.Trim()
            : GameTitleCleaner.CleanEnglishTitle(Path.GetFileName(installDir));
        TrainerTitleSource englishSource = !string.IsNullOrWhiteSpace(gogInfo.Name)
            ? TrainerTitleSource.GogInfo
            : (!string.IsNullOrWhiteSpace(englishTitle) ? TrainerTitleSource.Cleaner : TrainerTitleSource.Unknown);

        return new ScannedGameDescriptor
        {
            InstallDir = installDir,
            ExecutableRelPath = exeRelPath,
            Title = title,
            SortTitle = GameTitleCleaner.BuildSortTitle(title),
            PlatformSource = PlatformSource.GogOffline,
            AntiCheatTier = acResult.Tier,
            AntiCheatSignature = acResult.Signature,
            SteamAppId = steamAppId,
            GogGameId = gogInfo.GameId,
            GogClientId = gogInfo.ClientId,
            AchievementCaptureMode = captureMode,
            AchievementChannelLabel = channelLabel,
            CoverImagePath = artwork.CoverImagePath,
            IconPath = artwork.IconPath,
            HeroImagePath = artwork.HeroImagePath,
            EnglishTitle = englishTitle,
            EnglishTitleSource = englishSource,
        };
    }

    private async Task<ScannedGameDescriptor?> BuildSteamOrStandaloneDescriptorAsync(
        string installDir,
        string targetGameId,
        string? explicitExeRelPath,
        AntiCheatInspectionResult acResult,
        CancellationToken cancellationToken)
    {
        string? exeRelPath = ResolveValidatedExeRelPath(installDir, explicitExeRelPath)
            ?? SelectBestMainExecutable(installDir);

        if (string.IsNullOrWhiteSpace(exeRelPath))
        {
            return null;
        }

        string exeFullPath = Path.GetFullPath(Path.Combine(installDir, exeRelPath));

        // Stage 1 & 2: Deterministic local SteamAppId, nested Plugins/Steamworks emulator & ACF inspection.
        var steamProbe = ProbeLocalSteamIdentity(installDir);
        int? steamAppId = steamProbe.SteamAppId;

        // Stage 3: Unity app.info ProductName + Cleaned folder name & PE ProductName candidates.
        var unityInfo = GameEngineInspector.TryReadUnityAppInfo(installDir);
        string? unityProductTitle = unityInfo is not null && GameEngineInspector.IsUsefulUnityProductName(unityInfo.ProductName)
            ? unityInfo.ProductName.Trim()
            : null;

        var searchCandidates = new List<string>(5);
        if (!string.IsNullOrWhiteSpace(unityProductTitle))
        {
            searchCandidates.Add(unityProductTitle);
        }

        foreach (string c in GameTitleCleaner.BuildSearchCandidates(installDir, exeFullPath))
        {
            if (!searchCandidates.Contains(c, StringComparer.OrdinalIgnoreCase))
            {
                searchCandidates.Add(c);
            }
        }

        string title = !string.IsNullOrWhiteSpace(steamProbe.AcfGameName)
            ? steamProbe.AcfGameName!
            : (!string.IsNullOrWhiteSpace(unityProductTitle)
                ? unityProductTitle!
                : (searchCandidates.Count > 0 ? searchCandidates[0] : GameTitleCleaner.CleanTitle(Path.GetFileName(installDir))));

        // Stage 4: Keyless Steam storesearch fallback when SteamAppId wasn't found locally (or to refine noisy title).
        if (_options.AllowOnlineMetadataLookup)
        {
            if (!steamAppId.HasValue && searchCandidates.Count > 0)
            {
                var onlineMatch = await TrySearchSteamStoreAsync(searchCandidates, _options.OnlineLookupTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (onlineMatch is { Score: >= 0.82 })
                {
                    steamAppId = onlineMatch.Value.AppId;
                    if (!string.IsNullOrWhiteSpace(onlineMatch.Value.Name))
                    {
                        title = onlineMatch.Value.Name;
                    }
                }
            }
            else if (steamAppId.HasValue && string.IsNullOrWhiteSpace(steamProbe.AcfGameName))
            {
                string? officialTitle = await TryFetchSteamOfficialTitleAsync(steamAppId.Value, _options.OnlineLookupTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(officialTitle))
                {
                    title = officialTitle;
                }
            }
        }

        // Determine PlatformSource & AchievementCaptureMode.
        PlatformSource platformSource;
        if (steamProbe.IsSteamAppsLibraryLayout && !steamProbe.HasEmulatorConfig)
        {
            platformSource = PlatformSource.SteamLocal;
        }
        else if (steamProbe.HasSteamApiDll || steamProbe.HasEmulatorConfig || steamAppId.HasValue)
        {
            platformSource = PlatformSource.Steamrip;
        }
        else
        {
            platformSource = PlatformSource.Standalone;
        }

        var (captureMode, channelLabel) = ClassifySteamOrStandaloneChannel(
            platformSource,
            acResult.Tier,
            steamProbe);

        // Extract local artwork + enrich online Steam cover/hero if SteamAppId is available.
        var artwork = _artworkExtractor.ExtractLocalStandaloneArtwork(installDir, targetGameId, exeFullPath);
        if (_options.AllowOnlineMetadataLookup && steamAppId.HasValue)
        {
            artwork = await _artworkExtractor.EnrichOnlineArtworkAsync(
                targetGameId,
                steamAppId,
                gogGameId: null,
                artwork,
                _options.OnlineLookupTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        // Resolve canonical English title without disturbing localized (Chinese) display Title.
        string? englishTitle = null;
        TrainerTitleSource englishSource = TrainerTitleSource.Unknown;

        if (!string.IsNullOrWhiteSpace(steamProbe.AcfGameName) && GameTitleCleaner.IsLikelyEnglishTitle(steamProbe.AcfGameName))
        {
            englishTitle = steamProbe.AcfGameName.Trim();
            englishSource = TrainerTitleSource.AcfName;
        }
        else if (steamAppId.HasValue && _options.AllowOnlineMetadataLookup && GameTitleCleaner.ContainsCjkCharacters(title))
        {
            string? fetchedEnglish = await TryFetchSteamEnglishTitleAsync(steamAppId.Value, _options.OnlineLookupTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fetchedEnglish))
            {
                englishTitle = fetchedEnglish;
                englishSource = TrainerTitleSource.SteamAppDetails;
            }
        }

        if (string.IsNullOrWhiteSpace(englishTitle))
        {
            string? cleanedEnglish = !string.IsNullOrWhiteSpace(steamProbe.AcfGameName)
                ? steamProbe.AcfGameName.Trim()
                : (GameTitleCleaner.CleanEnglishTitle(title)
                    ?? GameTitleCleaner.CleanEnglishTitle(Path.GetFileName(installDir))
                    ?? searchCandidates.FirstOrDefault(GameTitleCleaner.IsLikelyEnglishTitle));

            if (!string.IsNullOrWhiteSpace(cleanedEnglish))
            {
                englishTitle = cleanedEnglish;
                englishSource = !string.IsNullOrWhiteSpace(steamProbe.AcfGameName)
                    ? TrainerTitleSource.AcfName
                    : TrainerTitleSource.Cleaner;
            }
        }

        return new ScannedGameDescriptor
        {
            InstallDir = installDir,
            ExecutableRelPath = exeRelPath,
            Title = title,
            SortTitle = GameTitleCleaner.BuildSortTitle(title),
            PlatformSource = platformSource,
            AntiCheatTier = acResult.Tier,
            AntiCheatSignature = acResult.Signature,
            SteamAppId = steamAppId,
            GogGameId = null,
            GogClientId = null,
            AchievementCaptureMode = captureMode,
            AchievementChannelLabel = channelLabel,
            CoverImagePath = artwork.CoverImagePath,
            IconPath = artwork.IconPath,
            HeroImagePath = artwork.HeroImagePath,
            SteamBuildId = steamProbe.SteamBuildId,
            SteamLastUpdatedUtc = steamProbe.SteamLastUpdatedUtc,
            EnglishTitle = englishTitle,
            EnglishTitleSource = englishSource,
        };
    }

    /// <summary>
    /// Applies scanned Steam ACF build fingerprint metadata (<c>buildid</c> and <c>LastUpdated</c>) onto a <see cref="GameRecord"/>
    /// while preserving existing values when the rescan did not observe an ACF manifest.
    /// </summary>
    public static GameRecord ApplySteamBuildFingerprint(GameRecord game, ScannedGameDescriptor scanned)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(scanned);

        return game with
        {
            SteamBuildId = scanned.SteamBuildId ?? game.SteamBuildId,
            SteamLastUpdatedUtc = scanned.SteamLastUpdatedUtc ?? game.SteamLastUpdatedUtc,
        };
    }

    /// <summary>
    /// Lazily resolves a game's canonical English title following the M3 waterfall
    /// (User override → cached <c>trainer_title_map</c> → Steam ACF <c>"name"</c> →
    /// <c>appdetails?appids=&lt;id&gt;&amp;l=english&amp;filters=basic</c> → GOG <c>goggame-*.info</c> <c>"name"</c> →
    /// <see cref="GameTitleCleaner"/> fallback) and permanently caches the result in <c>trainer_title_map</c>.
    /// Never modifies <see cref="GameRecord.Title"/> so Chinese display titles remain intact.
    /// </summary>
    public async Task<TrainerTitleMapRecord> ResolveAndCacheEnglishTitleAsync(
        GameRecord game,
        SpawnDatabase database,
        string? userSearchOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(database);

        var existingMap = await database.GetTrainerTitleMapAsync(game.GameId, cancellationToken).ConfigureAwait(false);
        string? trimmedOverride = string.IsNullOrWhiteSpace(userSearchOverride) ? null : userSearchOverride.Trim();

        // 1. Explicit user search override takes highest precedence and is persisted.
        if (!string.IsNullOrWhiteSpace(trimmedOverride))
        {
            var overrideRecord = new TrainerTitleMapRecord
            {
                GameId = game.GameId,
                EnglishTitle = !string.IsNullOrWhiteSpace(existingMap?.EnglishTitle) ? existingMap.EnglishTitle : trimmedOverride,
                TitleSource = existingMap is not null && existingMap.TitleSource != TrainerTitleSource.Unknown
                    ? existingMap.TitleSource
                    : TrainerTitleSource.User,
                UserSearchOverride = trimmedOverride,
                FlingPageUrl = existingMap?.FlingPageUrl,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await database.UpsertTrainerTitleMapAsync(overrideRecord, cancellationToken).ConfigureAwait(false);
            return overrideRecord;
        }

        // 2. Reuse permanent cache when present (including previously saved user override).
        if (existingMap is not null
            && (!string.IsNullOrWhiteSpace(existingMap.UserSearchOverride)
                || (!string.IsNullOrWhiteSpace(existingMap.EnglishTitle)
                    && existingMap.TitleSource is TrainerTitleSource.AcfName
                        or TrainerTitleSource.SteamAppDetails
                        or TrainerTitleSource.GogInfo
                        or TrainerTitleSource.User)))
        {
            return existingMap;
        }

        string? resolvedEnglish = null;
        TrainerTitleSource resolvedSource = TrainerTitleSource.Cleaner;

        // 3. Steam appmanifest_<id>.acf "name".
        var acf = TryFindAppManifestForInstallDir(game.InstallDir, game.SteamAppId);
        if (!string.IsNullOrWhiteSpace(acf?.Name) && GameTitleCleaner.IsLikelyEnglishTitle(acf.Name))
        {
            resolvedEnglish = acf.Name.Trim();
            resolvedSource = TrainerTitleSource.AcfName;
        }

        // 4. Lazy Steam appdetails?appids=<id>&l=english&filters=basic query (permanently cached once fetched).
        if (string.IsNullOrWhiteSpace(resolvedEnglish)
            && game.SteamAppId.HasValue
            && _options.AllowOnlineMetadataLookup)
        {
            string? steamEnglish = await TryFetchSteamEnglishTitleAsync(
                game.SteamAppId.Value,
                _options.OnlineLookupTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(steamEnglish))
            {
                resolvedEnglish = steamEnglish;
                resolvedSource = TrainerTitleSource.SteamAppDetails;
            }
        }

        // 5. GOG goggame-*.info "name".
        if (string.IsNullOrWhiteSpace(resolvedEnglish) && Directory.Exists(game.InstallDir))
        {
            var gogInfo = TryParseGogInfo(game.InstallDir);
            if (!string.IsNullOrWhiteSpace(gogInfo?.Name))
            {
                resolvedEnglish = gogInfo.Name.Trim();
                resolvedSource = TrainerTitleSource.GogInfo;
            }
        }

        // 6. GameTitleCleaner fallback (folder name / PE ProductName / game.Title).
        if (string.IsNullOrWhiteSpace(resolvedEnglish))
        {
            resolvedEnglish = GameTitleCleaner.CleanEnglishTitle(Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir)))
                ?? GameTitleCleaner.CleanEnglishTitle(GameTitleCleaner.TryGetCleanPeProductName(game.ExecutableFullPath))
                ?? GameTitleCleaner.CleanEnglishTitle(game.Title)
                ?? (!string.IsNullOrWhiteSpace(acf?.Name) ? acf.Name.Trim() : null)
                ?? GameTitleCleaner.CleanTitle(game.Title);
            resolvedSource = TrainerTitleSource.Cleaner;
        }

        var persisted = new TrainerTitleMapRecord
        {
            GameId = game.GameId,
            EnglishTitle = resolvedEnglish,
            TitleSource = resolvedSource,
            UserSearchOverride = existingMap?.UserSearchOverride,
            FlingPageUrl = existingMap?.FlingPageUrl,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await database.UpsertTrainerTitleMapAsync(persisted, cancellationToken).ConfigureAwait(false);
        return persisted;
    }

    // -------------------------------------------------------------------------
    // GOG Offline Metadata & Channel Helpers
    // -------------------------------------------------------------------------

    private sealed record ParsedGogInfo(
        string? GameId,
        string? ClientId,
        string? Name,
        string? PrimaryTaskRelPath);

    private static ParsedGogInfo? TryParseGogInfo(string installDir)
    {
        string? infoFilePath = null;
        string? fileNameId = null;

        try
        {
            foreach (string file in Directory.EnumerateFiles(installDir, "goggame-*.info", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(file);
                var match = GogInfoFileNameRegex().Match(fileName);
                if (match.Success)
                {
                    infoFilePath = file;
                    fileNameId = match.Groups[1].Value;
                    break;
                }
            }
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(infoFilePath))
        {
            return null;
        }

        try
        {
            string rawJson = File.ReadAllText(infoFilePath);
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? gameId = ReadStringOrNumber(root, "gameId")
                ?? fileNameId
                ?? ReadStringOrNumber(root, "rootGameId");
            string? clientId = ReadStringOrNumber(root, "clientId");
            string? name = ReadStringOrNumber(root, "name");
            string? primaryPath = null;
            string? categoryGamePath = null;
            string? fallbackTaskPath = null;

            if (root.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (var task in tasks.EnumerateArray())
                {
                    if (task.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? type = ReadStringOrNumber(task, "type");
                    if (string.Equals(type, "URLTask", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? relPath = ReadStringOrNumber(task, "path");
                    if (string.IsNullOrWhiteSpace(relPath))
                    {
                        continue;
                    }

                    string exeName = Path.GetFileName(relPath);
                    if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || ExcludedExecutableRegex().IsMatch(exeName))
                    {
                        continue;
                    }

                    string? category = ReadStringOrNumber(task, "category");
                    if (string.Equals(category, "tool", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(category, "document", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isPrimary = task.TryGetProperty("isPrimary", out var primProp) && primProp.ValueKind == JsonValueKind.True;
                    if (isPrimary && primaryPath is null)
                    {
                        primaryPath = relPath;
                    }
                    else if (string.Equals(category, "game", StringComparison.OrdinalIgnoreCase) && categoryGamePath is null)
                    {
                        categoryGamePath = relPath;
                    }
                    else
                    {
                        fallbackTaskPath ??= relPath;
                    }
                }
            }

            return new ParsedGogInfo(
                GameId: gameId,
                ClientId: clientId,
                Name: name,
                PrimaryTaskRelPath: primaryPath ?? categoryGamePath ?? fallbackTaskPath);
        }
        catch
        {
            return fileNameId is not null
                ? new ParsedGogInfo(fileNameId, null, null, null)
                : null;
        }
    }

    private static (AchievementCaptureMode Mode, string Label) ClassifyGogAchievementChannel(
        string installDir,
        string? gogGameId)
    {
        bool hasGalaxyDll = HasAnyFileBounded(
            installDir,
            maxDepth: 3,
            static fileName =>
                fileName.Equals("Galaxy.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("Galaxy64.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Galaxy64", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("GalaxyCSharpGlue.dll", StringComparison.OrdinalIgnoreCase));

        if (hasGalaxyDll)
        {
            return (AchievementCaptureMode.GogCometLocal, "GOG Comet 9977 · 就绪");
        }

        if (!string.IsNullOrWhiteSpace(gogGameId) && NativeSaveSupportedGogIds.Contains(gogGameId))
        {
            return (AchievementCaptureMode.GogNativeSave, "GOG 原生存档监听 · 就绪");
        }

        return (AchievementCaptureMode.None, "纯 DRM-Free 离线版");
    }

    // -------------------------------------------------------------------------
    // Steamrip / Emulator 4-Stage Identity & Heuristic Executable Selection
    // -------------------------------------------------------------------------

    private sealed record LocalSteamProbeResult(
        int? SteamAppId,
        bool HasSteamApiDll,
        bool HasEmulatorConfig,
        string? EmulatorChannelLabel,
        bool HasGbeForkActivatedBackup,
        bool IsSteamAppsLibraryLayout,
        string? AcfGameName,
        string? SteamBuildId,
        DateTimeOffset? SteamLastUpdatedUtc);

    private static LocalSteamProbeResult ProbeLocalSteamIdentity(string installDir)
    {
        var (emuInfo, inspectedAppId, hasSteamApiDll, hasGbeForkBackup) = GameEngineInspector.InspectSteamFiles(installDir);
        bool hasEmulatorConfig = emuInfo is not null;

        // Check parent Steam library appmanifest_<appid>.acf when inside .../steamapps/common/<Folder>.
        bool isSteamAppsLayout = false;
        SteamAppManifestMetadata? acfMetadata = null;
        var parentDir = Directory.GetParent(installDir);
        if (parentDir is not null && string.Equals(parentDir.Name, "common", StringComparison.OrdinalIgnoreCase))
        {
            var steamAppsDir = parentDir.Parent;
            if (steamAppsDir is not null && string.Equals(steamAppsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                isSteamAppsLayout = true;
                string gameFolderName = Path.GetFileName(installDir);
                acfMetadata = TryFindMatchingAppManifest(steamAppsDir.FullName, gameFolderName, inspectedAppId);
            }
        }

        int? finalAppId = inspectedAppId ?? acfMetadata?.AppId;

        return new LocalSteamProbeResult(
            SteamAppId: finalAppId,
            HasSteamApiDll: hasSteamApiDll,
            HasEmulatorConfig: hasEmulatorConfig,
            EmulatorChannelLabel: emuInfo?.ChannelLabel,
            HasGbeForkActivatedBackup: hasGbeForkBackup,
            IsSteamAppsLibraryLayout: isSteamAppsLayout,
            AcfGameName: acfMetadata?.Name,
            SteamBuildId: acfMetadata?.BuildId,
            SteamLastUpdatedUtc: acfMetadata?.LastUpdatedUtc);
    }

    private static (AchievementCaptureMode Mode, string Label) ClassifySteamOrStandaloneChannel(
        PlatformSource platformSource,
        AntiCheatTier tier,
        LocalSteamProbeResult probe)
    {
        if (probe.HasGbeForkActivatedBackup)
        {
            return (AchievementCaptureMode.GbeForkActivated, "Goldberg GSE (已无损激活) · 就绪");
        }

        if (platformSource == PlatformSource.SteamLocal)
        {
            return (AchievementCaptureMode.SteamLocalCache, "Steam 本地缓存监听 · 就绪");
        }

        if (probe.HasEmulatorConfig)
        {
            return (AchievementCaptureMode.SteamEmulator, probe.EmulatorChannelLabel ?? "离线 Steam 模拟器监听 · 就绪");
        }

        if (probe.HasSteamApiDll && tier == AntiCheatTier.Tier0)
        {
            return (AchievementCaptureMode.PendingGbeForkActivation, "待激活离线成就补丁");
        }

        return (AchievementCaptureMode.None, "纯单机运行模式");
    }

    /// <summary>
    /// Heuristically selects the primary game executable relative to <paramref name="installDir"/>,
    /// scoring Unity <c>*_Data</c> pairs, Unreal <c>&lt;Project&gt;\Binaries\Win64|WinGDK</c> binaries
    /// (both <c>*-Shipping.exe</c> and custom-named UE binaries), file size, folder name affinity,
    /// and PE metadata while penalizing/excluding redirector stubs, prerequisite bootstrappers,
    /// crash handlers, redistributables, and uninstallers.
    /// </summary>
    public static string? SelectBestMainExecutable(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        string normalizedInstallDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        var exeFiles = EnumerateExecutablesDepthBounded(normalizedInstallDir, maxDepth: 5)
            .Where(f => !ExcludedExecutableRegex().IsMatch(Path.GetFileName(f)))
            .ToList();

        if (exeFiles.Count == 0)
        {
            return null;
        }

        // Pre-index Unreal Engine roots that possess at least one real <Project>\Binaries\Win64|WinGDK\*.exe binary
        // so root-level redirector/bootstrapper stubs (<UeRoot>\Play*.exe / <UeRoot>\<Stub>.exe) never beat the real UE binary.
        var ueRootsWithProjectBinaries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string exeFullPath in exeFiles)
        {
            if (TryGetUnrealProjectRootForBinary(exeFullPath, normalizedInstallDir, out string? ueRoot)
                && !string.IsNullOrWhiteSpace(ueRoot))
            {
                ueRootsWithProjectBinaries.Add(ueRoot);
            }
        }

        string folderName = Path.GetFileName(normalizedInstallDir);
        string cleanedFolder = GameTitleCleaner.CleanTitle(folderName, preserveRemasterEdition: true);

        string? bestRelPath = null;
        double bestScore = double.NegativeInfinity;

        foreach (string exeFullPath in exeFiles)
        {
            string exeName = Path.GetFileName(exeFullPath);
            string relPath = Path.GetRelativePath(normalizedInstallDir, exeFullPath);
            int depth = CountDirectorySeparators(relPath);
            string stem = Path.GetFileNameWithoutExtension(exeName);
            string exeDir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exeFullPath) ?? normalizedInstallDir);

            long lengthBytes = 0;
            try
            {
                lengthBytes = new FileInfo(exeFullPath).Length;
            }
            catch
            {
                // Ignore transient stat failure.
            }

            double score = 0.0;

            // 1. Unity deterministic match: <ExeStem>_Data folder next to <ExeStem>.exe.
            if (Directory.Exists(Path.Combine(exeDir, stem + "_Data")))
            {
                score += 80.0;
            }

            // 2. Unreal Engine binary match: *-Win64-Shipping.exe / *-WinGDK-Shipping.exe OR <Project>\Binaries\Win64|WinGDK\*.exe alongside Engine\.
            bool isUeShipping = stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
                                || stem.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase);
            bool isUeProjectBinary = TryGetUnrealProjectRootForBinary(exeFullPath, normalizedInstallDir, out _);

            if (isUeShipping && isUeProjectBinary)
            {
                score += 46.0;
            }
            else if (isUeShipping)
            {
                score += 42.0;
            }
            else if (isUeProjectBinary)
            {
                score += 40.0;
            }

            // Engine\Binaries\ or Engine\Extras\ helpers are engine runtime tools, never the game binary.
            if (IsUnderUnrealEngineSubtree(exeFullPath, normalizedInstallDir))
            {
                score -= 90.0;
            }

            // 3. Penalize launcher/config/updater/server/autorun/redirector helper stems.
            bool hasPenalizedStem = PenalizedExecutableStemRegex().IsMatch(stem);
            if (hasPenalizedStem)
            {
                score -= 55.0;
            }

            // 4. PE role & Unreal root redirector/prerequisite stub trap detection.
            var peRole = GameTitleCleaner.InspectPeExecutableRole(exeFullPath);
            if (peRole == PeExecutableRole.RedirectorOrPrerequisiteStub)
            {
                score -= 130.0;
            }
            else if (peRole == PeExecutableRole.LauncherOrMenuWrapper && !hasPenalizedStem)
            {
                score -= 55.0;
            }
            else if (peRole == PeExecutableRole.LauncherOrMenuWrapper)
            {
                score -= 20.0;
            }

            if (!isUeProjectBinary && ueRootsWithProjectBinaries.Contains(exeDir))
            {
                if (HasPrerequisiteRedirectorMarkers(exeDir)
                    || stem.StartsWith("Play", StringComparison.OrdinalIgnoreCase))
                {
                    score -= 125.0;
                }
                else if (lengthBytes <= 8L * 1024 * 1024)
                {
                    score -= 65.0;
                }
            }

            // 5. Folder name similarity bonus.
            string cleanedStem = GameTitleCleaner.CleanTitle(stem, preserveRemasterEdition: true);
            double nameSim = Math.Max(
                GameTitleCleaner.ComputeJaroWinklerSimilarity(cleanedStem, cleanedFolder),
                GameTitleCleaner.ComputeJaroWinklerSimilarity(
                    GameTitleCleaner.ExpandSeriesAbbreviations(cleanedStem),
                    GameTitleCleaner.ExpandSeriesAbbreviations(cleanedFolder)));
            score += nameSim * 35.0;

            // 6. File size score (log scale up to ~26 pts; tiny <100KB stubs get less weight unless top-level).
            if (lengthBytes > 0)
            {
                double mb = lengthBytes / (1024.0 * 1024.0);
                score += Math.Clamp(Math.Log10(Math.Max(0.05, mb) * 20.0) * 10.0, -5.0, 26.0);
            }

            // 7. PE ProductName validity bonus (only awarded to genuine game binaries).
            if (peRole == PeExecutableRole.GameBinary)
            {
                string? peName = GameTitleCleaner.TryGetCleanPeProductName(exeFullPath);
                if (!string.IsNullOrWhiteSpace(peName))
                {
                    score += 10.0;
                }
            }

            // 8. Shallow depth preference (waived up to depth 4 for Unreal <Project>\Binaries\Win64|WinGDK binaries).
            if (isUeShipping || isUeProjectBinary)
            {
                score -= Math.Max(0, depth - 4) * 8.0;
            }
            else
            {
                score -= depth * 12.0;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestRelPath = relPath;
            }
        }

        return bestRelPath;
    }

    // -------------------------------------------------------------------------
    // Keyless Steam Store Search & AppDetails Helpers
    // -------------------------------------------------------------------------

    private async Task<(int AppId, string Name, double Score)?> TrySearchSteamStoreAsync(
        IReadOnlyList<string> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3.5) : timeout);
        var ct = linkedCts.Token;

        (int AppId, string Name, double Score)? bestOverall = null;

        foreach (string query in candidates)
        {
            if (string.IsNullOrWhiteSpace(query) || ct.IsCancellationRequested)
            {
                continue;
            }

            foreach (string locale in new[] { "l=schinese&cc=CN", "l=english&cc=US" })
            {
                try
                {
                    string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&{locale}";
                    using var resp = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(stream);
                    if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var item in items.EnumerateArray())
                    {
                        if (!item.TryGetProperty("id", out var idProp) || !idProp.TryGetInt32(out int appId) || appId <= 0)
                        {
                            continue;
                        }

                        string? itemName = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(itemName))
                        {
                            continue;
                        }

                        if (GameTitleCleaner.HasConflictingSequelOrAnthologyTokens(query, itemName))
                        {
                            continue;
                        }

                        double sim = Math.Max(
                            GameTitleCleaner.ComputeJaroWinklerSimilarity(query, itemName),
                            GameTitleCleaner.ComputeJaroWinklerSimilarity(
                                GameTitleCleaner.ExpandSeriesAbbreviations(query),
                                GameTitleCleaner.ExpandSeriesAbbreviations(itemName)));

                        if (GameTitleCleaner.HasRemasterEditionMismatch(query, itemName))
                        {
                            sim -= 0.12;
                        }

                        if (bestOverall is null || sim > bestOverall.Value.Score)
                        {
                            bestOverall = (appId, itemName.Trim(), sim);
                        }

                        if (sim >= 0.96)
                        {
                            return bestOverall;
                        }
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(ex, "Steam storesearch skipped/timed out for query '{Query}'.", query);
                    }

                    break;
                }
            }

            if (bestOverall is { Score: >= 0.88 })
            {
                break;
            }
        }

        return bestOverall;
    }

    private Task<string?> TryFetchSteamOfficialTitleAsync(
        int appId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TryFetchSteamAppDetailsNameAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese",
            appId,
            timeout,
            cancellationToken);

    private Task<string?> TryFetchSteamEnglishTitleAsync(
        int appId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TryFetchSteamAppDetailsNameAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&l=english&filters=basic",
            appId,
            timeout,
            cancellationToken);

    private async Task<string?> TryFetchSteamAppDetailsNameAsync(
        string url,
        int appId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3.0) : timeout);

        try
        {
            using var resp = await _httpClient.GetAsync(url, linkedCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(stream);
            string key = appId.ToString(CultureInfo.InvariantCulture);
            if (doc.RootElement.TryGetProperty(key, out var appEntry)
                && appEntry.TryGetProperty("success", out var okProp)
                && okProp.ValueKind == JsonValueKind.True
                && appEntry.TryGetProperty("data", out var data)
                && data.TryGetProperty("name", out var nameProp)
                && nameProp.ValueKind == JsonValueKind.String)
            {
                return nameProp.GetString()?.Trim();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            // Offline or rate-limited; caller keeps cleaned local title.
        }

        return null;
    }

    // -------------------------------------------------------------------------
    // Static Filesystem & Parsing Utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses Steam VDF <c>appmanifest_&lt;id&gt;.acf</c> text to extract <c>appid</c>, <c>name</c>,
    /// <c>installdir</c>, <c>buildid</c>, and <c>LastUpdated</c> (Unix epoch seconds to UTC).
    /// </summary>
    public static SteamAppManifestMetadata? TryParseAppManifestText(string? acfText)
    {
        if (string.IsNullOrWhiteSpace(acfText))
        {
            return null;
        }

        var idMatch = AcfAppIdRegex().Match(acfText);
        var nameMatch = AcfNameRegex().Match(acfText);
        var installMatch = AcfInstallDirRegex().Match(acfText);
        var buildMatch = AcfBuildIdRegex().Match(acfText);
        var updatedMatch = AcfLastUpdatedRegex().Match(acfText);

        int? appId = idMatch.Success
                     && int.TryParse(idMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedId)
                     && parsedId > 0
            ? parsedId
            : null;

        string? name = nameMatch.Success && !string.IsNullOrWhiteSpace(nameMatch.Groups[1].Value)
            ? nameMatch.Groups[1].Value.Trim()
            : null;

        string? installDir = installMatch.Success && !string.IsNullOrWhiteSpace(installMatch.Groups[1].Value)
            ? installMatch.Groups[1].Value.Trim()
            : null;

        string? rawBuildId = buildMatch.Success ? buildMatch.Groups[1].Value.Trim() : null;
        string? buildId = !string.IsNullOrWhiteSpace(rawBuildId) && !string.Equals(rawBuildId, "0", StringComparison.Ordinal)
            ? rawBuildId
            : null;

        DateTimeOffset? lastUpdatedUtc = null;
        if (updatedMatch.Success)
        {
            string rawUpdated = updatedMatch.Groups[1].Value.Trim();
            if (long.TryParse(rawUpdated, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSec) && unixSec > 0)
            {
                try
                {
                    lastUpdatedUtc = DateTimeOffset.FromUnixTimeSeconds(unixSec);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Ignore out-of-range timestamps.
                }
            }
            else if (DateTimeOffset.TryParse(
                rawUpdated,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsedDto))
            {
                lastUpdatedUtc = parsedDto;
            }
        }

        if (appId is null && name is null && installDir is null && buildId is null && lastUpdatedUtc is null)
        {
            return null;
        }

        return new SteamAppManifestMetadata(appId, name, installDir, buildId, lastUpdatedUtc);
    }

    /// <summary>
    /// Locates and parses the Steam <c>appmanifest_&lt;id&gt;.acf</c> corresponding to <paramref name="installDir"/>
    /// when the game resides inside a <c>steamapps\common\&lt;Folder&gt;</c> library layout.
    /// </summary>
    public static SteamAppManifestMetadata? TryFindAppManifestForInstallDir(string? installDir, int? expectedAppId = null)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
            var parentDir = Directory.GetParent(normalized);
            if (parentDir is null || !string.Equals(parentDir.Name, "common", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var steamAppsDir = parentDir.Parent;
            if (steamAppsDir is null || !string.Equals(steamAppsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return TryFindMatchingAppManifest(steamAppsDir.FullName, Path.GetFileName(normalized), expectedAppId);
        }
        catch
        {
            return null;
        }
    }

    private static SteamAppManifestMetadata? TryFindMatchingAppManifest(
        string steamAppsDir,
        string gameFolderName,
        int? expectedAppId = null)
    {
        try
        {
            if (expectedAppId.HasValue && expectedAppId.Value > 0)
            {
                string directAcf = Path.Combine(steamAppsDir, $"appmanifest_{expectedAppId.Value.ToString(CultureInfo.InvariantCulture)}.acf");
                if (File.Exists(directAcf))
                {
                    var directMeta = TryParseAppManifestText(File.ReadAllText(directAcf));
                    if (directMeta is not null
                        && (string.IsNullOrWhiteSpace(directMeta.InstallDir)
                            || string.Equals(directMeta.InstallDir, gameFolderName, StringComparison.OrdinalIgnoreCase)))
                    {
                        return directMeta;
                    }
                }
            }

            foreach (string acfPath in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                var meta = TryParseAppManifestText(File.ReadAllText(acfPath));
                if (meta is null
                    || string.IsNullOrWhiteSpace(meta.InstallDir)
                    || !string.Equals(meta.InstallDir, gameFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return meta;
            }
        }
        catch
        {
            // Ignore inaccessible Steam library manifests.
        }

        return null;
    }

    private static string? ResolveValidatedExeRelPath(string installDir, string? candidateRelPath)
    {
        if (string.IsNullOrWhiteSpace(candidateRelPath))
        {
            return null;
        }

        string cleanedRel = candidateRelPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(installDir, cleanedRel));
        string prefix = Path.TrimEndingDirectorySeparator(installDir) + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(combined))
        {
            return null;
        }

        // Preserve exact on-disk casing for the filename when possible.
        try
        {
            string dir = Path.GetDirectoryName(combined)!;
            string targetFile = Path.GetFileName(combined);
            string? exactMatch = Directory.EnumerateFiles(dir, targetFile, SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(exactMatch))
            {
                return Path.GetRelativePath(installDir, exactMatch);
            }
        }
        catch
        {
            // Fallback to normalized relative path.
        }

        return Path.GetRelativePath(installDir, combined);
    }

    private static string InferGameRootFromExecutable(string exeFullPath, string exeDir)
    {
        // Walk up at most 3 levels to see if exe sits inside Binaries\Win64, bin\x64, etc. under a true game root.
        var current = new DirectoryInfo(exeDir);
        for (int step = 0; step < 3 && current.Parent is not null; step++)
        {
            string dirName = current.Name;
            bool looksLikeBinSubfolder =
                dirName.Equals("Win64", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("WinGDK", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("Win32", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("x64", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("x86", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("bin", StringComparison.OrdinalIgnoreCase);

            var parent = current.Parent;
            bool parentHasRootMarkers =
                Directory.EnumerateFiles(parent.FullName, "goggame-*.info", SearchOption.TopDirectoryOnly).Any()
                || File.Exists(Path.Combine(parent.FullName, "steam_appid.txt"))
                || Directory.Exists(Path.Combine(parent.FullName, "Engine"))
                || Directory.Exists(Path.Combine(parent.FullName, "Content"));

            if (!looksLikeBinSubfolder && !parentHasRootMarkers)
            {
                break;
            }

            current = parent;
            if (parentHasRootMarkers)
            {
                // In Unreal Engine (<GameRoot>\Engine + <GameRoot>\<Project>\Content + <GameRoot>\<Project>\Binaries\Win64\*.exe),
                // <Project> has Content\, while its parent <GameRoot> holds Engine\. Step up to <GameRoot> when present.
                if (current.Parent is not null
                    && !current.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(current.Parent.FullName, "Engine")))
                {
                    return current.Parent.FullName;
                }

                return current.FullName;
            }
        }

        return current.FullName;
    }

    private static bool HasSingleGameEngineOrStoreRootIdentity(string dirPath)
    {
        try
        {
            if (TryParseGogInfo(dirPath) is not null
                || File.Exists(Path.Combine(dirPath, "steam_appid.txt"))
                || Directory.Exists(Path.Combine(dirPath, "steam_settings"))
                || File.Exists(Path.Combine(dirPath, "steam_api.dll"))
                || File.Exists(Path.Combine(dirPath, "steam_api64.dll"))
                || Directory.Exists(Path.Combine(dirPath, "Engine")))
            {
                return true;
            }

            foreach (string iniName in TopLevelSingleGameEmulatorInis)
            {
                if (File.Exists(Path.Combine(dirPath, iniName)))
                {
                    return true;
                }
            }

            if (Directory.EnumerateDirectories(dirPath, "*_Data", SearchOption.TopDirectoryOnly).Any())
            {
                return true;
            }

            foreach (string exe in Directory.EnumerateFiles(dirPath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string exeName = Path.GetFileName(exe);
                if (ExcludedExecutableRegex().IsMatch(exeName))
                {
                    continue;
                }

                string stem = Path.GetFileNameWithoutExtension(exeName);
                if (PenalizedExecutableStemRegex().IsMatch(stem))
                {
                    continue;
                }

                var peRole = GameTitleCleaner.InspectPeExecutableRole(exe);
                if (peRole is PeExecutableRole.LauncherOrMenuWrapper or PeExecutableRole.RedirectorOrPrerequisiteStub)
                {
                    continue;
                }

                // Non-penalized, non-wrapper top-level game executable indicates a single game root.
                return true;
            }
        }
        catch
        {
            // Ignore IO errors.
        }

        return false;
    }

    private static bool IsIndependentStandaloneChildGame(
        string childDir,
        out int? steamAppId,
        out string? gogGameId)
    {
        steamAppId = null;
        gogGameId = null;

        if (string.IsNullOrWhiteSpace(childDir) || !Directory.Exists(childDir))
        {
            return false;
        }

        try
        {
            var gogInfo = TryParseGogInfo(childDir);
            if (gogInfo is not null)
            {
                gogGameId = gogInfo.GameId;
                return SelectBestMainExecutable(childDir) is not null;
            }

            var steamProbe = GameEngineInspector.InspectSteamFiles(childDir);
            steamAppId = steamProbe.DeterministicTxtAppId;

            string? mainExe = SelectBestMainExecutable(childDir);
            if (string.IsNullOrWhiteSpace(mainExe))
            {
                return false;
            }

            if (Directory.Exists(Path.Combine(childDir, "Engine"))
                || Directory.EnumerateDirectories(childDir, "*_Data", SearchOption.TopDirectoryOnly).Any()
                || steamProbe.HasSteamApiDll
                || steamProbe.Emulator is not null
                || steamProbe.DeterministicTxtAppId.HasValue)
            {
                return true;
            }

            // Pure standalone child game inside an anthology container: require a non-penalized, non-wrapper executable at depth 0 or 1.
            int exeDepth = CountDirectorySeparators(mainExe);
            if (exeDepth <= 1)
            {
                string mainExeFullPath = Path.Combine(childDir, mainExe);
                string stem = Path.GetFileNameWithoutExtension(mainExe);
                if (!PenalizedExecutableStemRegex().IsMatch(stem)
                    && GameTitleCleaner.InspectPeExecutableRole(mainExeFullPath) is not (PeExecutableRole.LauncherOrMenuWrapper or PeExecutableRole.RedirectorOrPrerequisiteStub))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore inaccessible child folders.
        }

        return false;
    }

    private static bool TryGetUnrealProjectRootForBinary(
        string exeFullPath,
        string installDir,
        out string? ueRootDir)
    {
        ueRootDir = null;
        try
        {
            var winDir = Directory.GetParent(exeFullPath);
            if (winDir is null
                || (!winDir.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)
                    && !winDir.Name.Equals("WinGDK", StringComparison.OrdinalIgnoreCase)
                    && !winDir.Name.Equals("Win32", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var binariesDir = winDir.Parent;
            if (binariesDir is null || !binariesDir.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var projectDir = binariesDir.Parent;
            if (projectDir is null || projectDir.Name.Equals("Engine", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidateUeRoot = projectDir.Parent;
            if (candidateUeRoot is null)
            {
                return false;
            }

            string normalizedUeRoot = Path.TrimEndingDirectorySeparator(candidateUeRoot.FullName);
            string normalizedInstall = Path.TrimEndingDirectorySeparator(installDir);
            bool withinInstallDir = string.Equals(normalizedUeRoot, normalizedInstall, StringComparison.OrdinalIgnoreCase)
                || normalizedUeRoot.StartsWith(normalizedInstall + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!withinInstallDir || !Directory.Exists(Path.Combine(normalizedUeRoot, "Engine")))
            {
                return false;
            }

            ueRootDir = normalizedUeRoot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderUnrealEngineSubtree(string exeFullPath, string installDir)
    {
        try
        {
            string rel = Path.GetRelativePath(installDir, exeFullPath);
            string[] segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < segments.Length; i++)
            {
                if (segments[i].Equals("Engine", StringComparison.OrdinalIgnoreCase)
                    && (segments[i + 1].Equals("Binaries", StringComparison.OrdinalIgnoreCase)
                        || segments[i + 1].Equals("Extras", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore path errors.
        }

        return false;
    }

    private static bool HasPrerequisiteRedirectorMarkers(string dirPath)
    {
        try
        {
            if (File.Exists(Path.Combine(dirPath, "title.rgl")))
            {
                return true;
            }

            foreach (string vdfFile in Directory.EnumerateFiles(dirPath, "installscript*.vdf", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(vdfFile);
                if (text.Contains("Redistributables", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Rockstar-Games-Launcher", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("Social-Club-Setup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore unreadable VDF files.
        }

        return false;
    }

    private static string UnwrapSingleNestedFolderIfNeeded(string directoryPath)
    {
        // Handles archives extracted as D:\Games\GameName\GameName\game.exe
        string current = directoryPath;
        for (int depth = 0; depth < 2; depth++)
        {
            if (LooksLikeDirectGameDirectory(current))
            {
                return current;
            }

            try
            {
                var topFiles = Directory.EnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly).ToList();
                if (topFiles.Count > 0)
                {
                    return current;
                }

                var subDirs = Directory.EnumerateDirectories(current)
                    .Where(d => !SkippedSubdirectoryNames.Contains(Path.GetFileName(d)))
                    .ToList();

                if (subDirs.Count == 1)
                {
                    current = subDirs[0];
                    continue;
                }
            }
            catch
            {
                break;
            }

            break;
        }

        return current;
    }

    private static bool LooksLikeDirectGameDirectory(string dirPath)
    {
        try
        {
            if (Directory.EnumerateFiles(dirPath, "goggame-*.info", SearchOption.TopDirectoryOnly).Any()
                || File.Exists(Path.Combine(dirPath, "steam_appid.txt"))
                || Directory.Exists(Path.Combine(dirPath, "steam_settings")))
            {
                return true;
            }

            foreach (string exe in Directory.EnumerateFiles(dirPath, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (!ExcludedExecutableRegex().IsMatch(Path.GetFileName(exe)))
                {
                    return true;
                }
            }

            // Unreal root layout: Engine/ + <Project>/Binaries/Win64/*.exe
            if (Directory.Exists(Path.Combine(dirPath, "Engine")))
            {
                return SelectBestMainExecutable(dirPath) is not null;
            }
        }
        catch
        {
            // Ignore inaccessible directories.
        }

        return false;
    }

    private static bool HasAnyValidGameSignal(string dirPath) =>
        TryParseGogInfo(dirPath) is not null || SelectBestMainExecutable(dirPath) is not null;

    private static bool HasAnyFileBounded(string rootDir, int maxDepth, Func<string, bool> fileNamePredicate)
    {
        foreach (string path in EnumerateFilesDepthBounded(rootDir, maxDepth))
        {
            if (fileNamePredicate(Path.GetFileName(path)))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> EnumerateExecutablesDepthBounded(string rootDir, int maxDepth)
    {
        var results = new List<string>(32);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        void Walk(string dir, int depth)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(dir, "*.exe", opts));
                if (depth >= maxDepth)
                {
                    return;
                }

                bool hasSiblingBinaries = Directory.Exists(Path.Combine(dir, "Binaries"))
                                          || Directory.Exists(Path.Combine(dir, "Engine"));

                foreach (string sub in Directory.EnumerateDirectories(dir, "*", opts))
                {
                    string subName = Path.GetFileName(sub);
                    if (SkippedSubdirectoryNames.Contains(subName))
                    {
                        continue;
                    }

                    // Skip Unreal asset Content\ trees when Binaries\ or Engine\ is present.
                    if (hasSiblingBinaries && subName.Equals("Content", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Walk(sub, depth + 1);
                }
            }
            catch
            {
                // Ignore inaccessible subfolders.
            }
        }

        Walk(rootDir, 0);
        return results;
    }

    private static List<string> EnumerateFilesDepthBounded(string rootDir, int maxDepth)
    {
        var results = new List<string>(64);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        void Walk(string dir, int depth)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(dir, "*", opts));
                if (depth >= maxDepth)
                {
                    return;
                }

                foreach (string sub in Directory.EnumerateDirectories(dir, "*", opts))
                {
                    if (SkippedSubdirectoryNames.Contains(Path.GetFileName(sub)))
                    {
                        continue;
                    }

                    Walk(sub, depth + 1);
                }
            }
            catch
            {
                // Ignore inaccessible subfolders.
            }
        }

        Walk(rootDir, 0);
        return results;
    }

    private static int CountDirectorySeparators(string relPath)
    {
        int count = 0;
        foreach (char ch in relPath)
        {
            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                count++;
            }
        }

        return count;
    }

    private static string? ReadStringOrNumber(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null,
        };
    }
}
