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
        "Movies",
        "Videos",
    };

    [GeneratedRegex(@"^goggame-(\d+)\.info$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GogInfoFileNameRegex();

    [GeneratedRegex(
        @"^(?:UnityCrashHandler(?:32|64)?|UnrealCEFSubProcess|CrashReportClient(?:Editor)?|CrashSender\d*|crs-handler|ZFGameBrowser|cef_?process|steamerrorreporter(?:64)?|unins\d+|uninstall.*|vc_?redist.*|dxsetup|dxwebsetup|UE4?PrereqSetup.*|ndp4.*|dotnet.*|oalinst|PhysX.*|language_setup|gogdosbox|EasyAntiCheat(?:_EOS|_Setup)?|BEService(?:_x64)?|BEClient.*|SGuard(?:Svc)?64|createdump)\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedExecutableRegex();

    [GeneratedRegex(
        @"(?:^|[\s\._\-])(?:launcher|setup|install|installer|updater|patcher|config|configuration|settings|benchmark|dedicatedserver|server|crash|reporter|modmanager|editor)(?:$|[\s\._\-]|\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PenalizedExecutableStemRegex();

    [GeneratedRegex(@"""appid""\s+""(\d+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfAppIdRegex();

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfInstallDirRegex();

    [GeneratedRegex(@"""name""\s+""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcfNameRegex();

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
    /// Discovers candidate game installation folders inside <paramref name="rootDirectory"/>
    /// (for example <c>.local\games\LIMBO</c> and any sibling GOG / Steamrip folders).
    /// If <paramref name="rootDirectory"/> itself is a single game folder, returns <c>[rootDirectory]</c>.
    /// </summary>
    public IReadOnlyList<string> DiscoverCandidateGameDirectories(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
        {
            return [];
        }

        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));

        // Check if rootDirectory itself is directly a game folder (e.g. contains goggame-*.info, steam_appid.txt, or top-level game exe).
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
        };
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
        string? AcfGameName);

    private static LocalSteamProbeResult ProbeLocalSteamIdentity(string installDir)
    {
        var (emuInfo, inspectedAppId, hasSteamApiDll, hasGbeForkBackup) = GameEngineInspector.InspectSteamFiles(installDir);
        bool hasEmulatorConfig = emuInfo is not null;

        // Check parent Steam library appmanifest_<appid>.acf when inside .../steamapps/common/<Folder>.
        bool isSteamAppsLayout = false;
        string? acfName = null;
        int? acfAppId = null;
        var parentDir = Directory.GetParent(installDir);
        if (parentDir is not null && string.Equals(parentDir.Name, "common", StringComparison.OrdinalIgnoreCase))
        {
            var steamAppsDir = parentDir.Parent;
            if (steamAppsDir is not null && string.Equals(steamAppsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                isSteamAppsLayout = true;
                string gameFolderName = Path.GetFileName(installDir);
                (acfAppId, acfName) = TryFindMatchingAppManifest(steamAppsDir.FullName, gameFolderName);
            }
        }

        int? finalAppId = inspectedAppId ?? acfAppId;

        return new LocalSteamProbeResult(
            SteamAppId: finalAppId,
            HasSteamApiDll: hasSteamApiDll,
            HasEmulatorConfig: hasEmulatorConfig,
            EmulatorChannelLabel: emuInfo?.ChannelLabel,
            HasGbeForkActivatedBackup: hasGbeForkBackup,
            IsSteamAppsLibraryLayout: isSteamAppsLayout,
            AcfGameName: acfName);
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
    /// scoring Unity <c>*_Data</c> pairs, Unreal shipping binaries, file size, folder name affinity,
    /// and PE <c>ProductName</c> while excluding crash handlers, redistributables, and uninstallers.
    /// </summary>
    public static string? SelectBestMainExecutable(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return null;
        }

        var exeFiles = EnumerateFilesDepthBounded(installDir, maxDepth: 3)
            .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exeFiles.Count == 0)
        {
            return null;
        }

        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(installDir));
        string cleanedFolder = GameTitleCleaner.CleanTitle(folderName);

        string? bestRelPath = null;
        double bestScore = double.NegativeInfinity;

        foreach (string exeFullPath in exeFiles)
        {
            string exeName = Path.GetFileName(exeFullPath);
            if (ExcludedExecutableRegex().IsMatch(exeName))
            {
                continue;
            }

            string relPath = Path.GetRelativePath(installDir, exeFullPath);
            int depth = CountDirectorySeparators(relPath);
            string stem = Path.GetFileNameWithoutExtension(exeName);
            string exeDir = Path.GetDirectoryName(exeFullPath) ?? installDir;

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

            // 2. Unreal Engine shipping binary match (*-Win64-Shipping.exe / *-WinGDK-Shipping.exe).
            bool isUeShipping = stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
                                || stem.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase);
            if (isUeShipping)
            {
                score += 42.0;
            }

            // 3. Penalize launcher/config/updater/server helper stems.
            if (PenalizedExecutableStemRegex().IsMatch(stem))
            {
                score -= 55.0;
            }

            // 4. Folder name similarity bonus.
            double nameSim = GameTitleCleaner.ComputeJaroWinklerSimilarity(GameTitleCleaner.CleanTitle(stem), cleanedFolder);
            score += nameSim * 35.0;

            // 5. File size score (log scale up to ~25 pts; tiny <100KB stubs get less weight unless top-level).
            if (lengthBytes > 0)
            {
                double mb = lengthBytes / (1024.0 * 1024.0);
                score += Math.Clamp(Math.Log10(Math.Max(0.05, mb) * 20.0) * 10.0, -5.0, 26.0);
            }

            // 6. PE ProductName validity bonus.
            string? peName = GameTitleCleaner.TryGetCleanPeProductName(exeFullPath);
            if (!string.IsNullOrWhiteSpace(peName))
            {
                score += 10.0;
            }

            // 7. Shallow depth preference (root executable favored unless it is a penalized Launcher stub).
            if (!isUeShipping)
            {
                score -= depth * 12.0;
            }
            else
            {
                score -= Math.Max(0, depth - 2) * 8.0;
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

                        double sim = GameTitleCleaner.ComputeJaroWinklerSimilarity(query, itemName);
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

    private async Task<string?> TryFetchSteamOfficialTitleAsync(
        int appId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3.0) : timeout);

        try
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese";
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

    private static (int? AppId, string? Name) TryFindMatchingAppManifest(string steamAppsDir, string gameFolderName)
    {
        try
        {
            foreach (string acfPath in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
            {
                string text = File.ReadAllText(acfPath);
                var installMatch = AcfInstallDirRegex().Match(text);
                if (!installMatch.Success
                    || !string.Equals(installMatch.Groups[1].Value, gameFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var idMatch = AcfAppIdRegex().Match(text);
                var nameMatch = AcfNameRegex().Match(text);
                int? appId = idMatch.Success && int.TryParse(idMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : null;
                string? name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null;
                return (appId, name);
            }
        }
        catch
        {
            // Ignore inaccessible Steam library manifests.
        }

        return (null, null);
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
                return current.FullName;
            }
        }

        return current.FullName;
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
