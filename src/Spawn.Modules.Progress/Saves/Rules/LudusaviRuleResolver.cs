using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Snapshots;

namespace Spawn.Modules.Progress.Saves.Rules;

/// <summary>
/// Resolves per-game save (<c>[save]</c>), configuration (<c>[config]</c>), and <c>HKCU</c> registry
/// layouts by combining SQLite-indexed <c>ludusavi_rules</c> (all 16 Windows placeholders + zero-backtracking
/// globs) with <see cref="IGameDataManagementService"/> multi-source discovery (<c>PCGamingWiki</c>,
/// <c>Unity</c>, <c>Unreal</c>, <c>Steam</c>, <c>Emulator</c>, <c>Native</c>), strictly skipping
/// NTFS ReparsePoints and unsafe paths validated via
/// <see cref="IGameDataManagementService.ValidateSaveRestoreTargetAsync"/>.
/// </summary>
public sealed partial class LudusaviRuleResolver : ILudusaviRuleResolver
{
    private static readonly JsonDocumentOptions ParseJsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"^(?:\s*(?:\[[^\]]*\]|【[^】]*】|\(\d{4}[\.\-_]?\d{2}[\.\-_]?\d{2}\))\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketNoiseRegex();

    [GeneratedRegex(@"(?:\s*(?:\[[^\]]*\]|【[^】]*】|\((?:SteamRIP|Repack|Portable|GOG|x64|x86|Win64|Build\s*\d+)[^)]*\))\s*)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingNoiseRegex();

    [GeneratedRegex(@"(?i)(?:[\s\._\-]+)(?:v?\d+(?:\.\d+){1,4}[a-z]?|build[\s\._\-]*\d+|update[\s\._\-]*\d+|steamrip(?:\.com)?|fitgirl|dodi|codex|rune|tenoke|goldberg|repack|portable|免安装.*|绿色版.*|整合版.*).*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseAndVersionTailRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    private readonly SpawnDatabase _database;
    private readonly IGameDataManagementService _gameDataService;
    private readonly IKnownFolderProvider _knownFolderProvider;
    private readonly LudusaviPlaceholderExpander _expander;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool> _registryKeyExistsProbe;
    private readonly ILogger<LudusaviRuleResolver> _logger;
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private volatile bool _builtInSeedVerified;

    public LudusaviRuleResolver(
        SpawnDatabase database,
        IGameDataManagementService gameDataService,
        IKnownFolderProvider? knownFolderProvider = null,
        TimeProvider? timeProvider = null,
        ILogger<LudusaviRuleResolver>? logger = null)
        : this(database, gameDataService, knownFolderProvider, timeProvider, DefaultRegistryKeyExists, logger)
    {
    }

    internal LudusaviRuleResolver(
        SpawnDatabase database,
        IGameDataManagementService gameDataService,
        IKnownFolderProvider? knownFolderProvider,
        TimeProvider? timeProvider,
        Func<string, bool> registryKeyExistsProbe,
        ILogger<LudusaviRuleResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(gameDataService);
        ArgumentNullException.ThrowIfNull(registryKeyExistsProbe);

        _database = database;
        _gameDataService = gameDataService;
        _knownFolderProvider = knownFolderProvider ?? WindowsKnownFolderResolver.Instance;
        _expander = new LudusaviPlaceholderExpander(_knownFolderProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _registryKeyExistsProbe = registryKeyExistsProbe;
        _logger = logger ?? NullLogger<LudusaviRuleResolver>.Instance;
    }

    public async Task<int> EnsureBuiltInRulesSeededAsync(CancellationToken ct = default)
    {
        if (_builtInSeedVerified)
        {
            int cachedCount = await _database.GetLudusaviRuleCountAsync(ct).ConfigureAwait(false);
            if (cachedCount > 0)
            {
                return cachedCount;
            }
        }

        await _seedLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int existingCount = await _database.GetLudusaviRuleCountAsync(ct).ConfigureAwait(false);
            IReadOnlyList<LudusaviRuleRecord> builtInRules = LudusaviRuleSeedData.GetBuiltInRules(_timeProvider.GetUtcNow());

            if (existingCount == 0)
            {
                int upserted = await _database.UpsertLudusaviRulesBatchAsync(builtInRules, ct).ConfigureAwait(false);
                _builtInSeedVerified = true;
                return upserted;
            }

            // Preserve any user-customized rules that already exist in ludusavi_rules; only insert missing built-in rules.
            var missingRules = new List<LudusaviRuleRecord>();
            foreach (LudusaviRuleRecord rule in builtInRules)
            {
                LudusaviRuleRecord? existing = await _database
                    .FindLudusaviRuleAsync(title: rule.RuleName, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (existing is null || !string.Equals(existing.RuleName, rule.RuleName, StringComparison.OrdinalIgnoreCase))
                {
                    missingRules.Add(rule);
                }
            }

            if (missingRules.Count > 0)
            {
                await _database.UpsertLudusaviRulesBatchAsync(missingRules, ct).ConfigureAwait(false);
            }

            _builtInSeedVerified = true;
            return await _database.GetLudusaviRuleCountAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _seedLock.Release();
        }
    }

    public async Task<int> ImportRulesJsonAsync(string rulesJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesJson);

        if (!LudusaviYamlManifestParser.LooksLikeJson(rulesJson))
        {
            return await ImportRulesYamlAsync(rulesJson, ct).ConfigureAwait(false);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<LudusaviRuleRecord> parsed = ParseImportJson(rulesJson, now);
        if (parsed.Count == 0)
        {
            return 0;
        }

        return await _database.UpsertLudusaviRulesBatchAsync(parsed, ct).ConfigureAwait(false);
    }

    public async Task<int> ImportRulesYamlAsync(string manifestYaml, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestYaml);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<LudusaviRuleRecord> parsed = LudusaviYamlManifestParser.Parse(manifestYaml, now);
        if (parsed.Count == 0)
        {
            return 0;
        }

        return await _database.UpsertLudusaviRulesBatchAsync(parsed, ct).ConfigureAwait(false);
    }

    public Task<int> GetRuleCountAsync(CancellationToken ct = default) =>
        _database.GetLudusaviRuleCountAsync(ct);

    public Task<IReadOnlyList<SaveCustomLocationRecord>> GetCustomLocationsAsync(
        string gameId,
        CancellationToken ct = default) =>
        _database.GetSaveCustomLocationsByGameAsync(gameId, ct);

    public async Task<SaveCustomLocationRecord> AddCustomLocationAsync(
        string gameId,
        string pathOrRegistryKey,
        SaveCustomLocationKind locationKind,
        SaveFileCategory category = SaveFileCategory.Save,
        string? label = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrRegistryKey);

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            throw new InvalidOperationException($"未找到游戏记录：{gameId}");
        }

        string trimmedInput = pathOrRegistryKey.Trim();
        string normalizedStoredPath;
        SaveFileCategory effectiveCategory = category;

        if (locationKind == SaveCustomLocationKind.RegistryKey)
        {
            var (isSafe, canonicalKey, reason) = TrustedSavePathFence.ValidateTrustedRegistryTarget(trimmedInput);
            if (!isSafe || string.IsNullOrWhiteSpace(canonicalKey))
            {
                throw new InvalidOperationException(reason ?? $"安全围栏已拦截非法的注册表项：{trimmedInput}");
            }

            normalizedStoredPath = canonicalKey;
            effectiveCategory = SaveFileCategory.RegistryExport;
        }
        else
        {
            string expanded = trimmedInput.StartsWith('<')
                ? _expander.ExpandLogicalPath(trimmedInput, game)
                : trimmedInput;

            var (isSafe, reason) = TrustedSavePathFence.ValidateTrustedFilesystemTarget(
                expanded,
                game.InstallDir,
                _knownFolderProvider,
                spawnDataRoot: _database.DataRootPath,
                isUserCustomPath: true);
            if (!isSafe)
            {
                throw new InvalidOperationException(reason ?? $"安全围栏已拦截不安全的自定义路径：{expanded}");
            }

            normalizedStoredPath = NormalizePathForDedup(expanded);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var record = new SaveCustomLocationRecord
        {
            LocationId = SpawnId.NewUlid(now),
            GameId = gameId,
            PathOrRegistryKey = normalizedStoredPath,
            LocationKind = locationKind,
            Category = effectiveCategory,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            CreatedAtUtc = now,
        };

        await _database.UpsertSaveCustomLocationAsync(record, ct).ConfigureAwait(false);
        IReadOnlyList<SaveCustomLocationRecord> all = await _database
            .GetSaveCustomLocationsByGameAsync(gameId, ct)
            .ConfigureAwait(false);
        return all.FirstOrDefault(
            l => string.Equals(l.PathOrRegistryKey, normalizedStoredPath, StringComparison.OrdinalIgnoreCase))
            ?? record;
    }

    public Task<bool> RemoveCustomLocationAsync(string locationId, CancellationToken ct = default) =>
        _database.DeleteSaveCustomLocationAsync(locationId, ct);

    public string CanonicalizeToLogicalPath(string fullPathOrRegistryKey, GameRecord game) =>
        _expander.CanonicalizeToLogicalPath(fullPathOrRegistryKey, game);

    public string ExpandLogicalPath(string logicalPath, GameRecord game) =>
        _expander.ExpandLogicalPath(logicalPath, game);

    public async Task<ResolvedSaveLayout> ResolveGameSaveLayoutAsync(
        string gameId,
        bool refreshOnlineRules = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return new ResolvedSaveLayout
            {
                GameId = gameId ?? string.Empty,
                Warnings = ["游戏标识不能为空。"],
            };
        }

        GameRecord? game = await _database.GetGameByIdAsync(gameId, ct).ConfigureAwait(false);
        if (game is null)
        {
            return new ResolvedSaveLayout
            {
                GameId = gameId,
                Warnings = [$"未在游戏库中找到游戏记录：{gameId}"],
            };
        }

        await EnsureBuiltInRulesSeededAsync(ct).ConfigureAwait(false);

        var warnings = new List<string>();
        var fsLocations = new Dictionary<string, ResolvedSaveLocation>(StringComparer.OrdinalIgnoreCase);
        var regLocations = new Dictionary<string, ResolvedSaveLocation>(StringComparer.OrdinalIgnoreCase);

        // 0. Merge user-configured custom save locations (save_custom_locations) first so they always take priority.
        IReadOnlyList<SaveCustomLocationRecord> customLocations = await _database
            .GetSaveCustomLocationsByGameAsync(game.GameId, ct)
            .ConfigureAwait(false);
        foreach (SaveCustomLocationRecord custom in customLocations)
        {
            ct.ThrowIfCancellationRequested();
            string customLabel = string.IsNullOrWhiteSpace(custom.Label) ? "自定义路径" : $"自定义 · {custom.Label}";

            if (custom.LocationKind == SaveCustomLocationKind.RegistryKey)
            {
                var (isSafeReg, canonicalKey, reason) = TrustedSavePathFence.ValidateTrustedRegistryTarget(custom.PathOrRegistryKey);
                if (!isSafeReg || string.IsNullOrWhiteSpace(canonicalKey))
                {
                    warnings.Add(reason ?? $"已跳过不安全的自定义注册表项：{custom.PathOrRegistryKey}");
                    continue;
                }

                string subKey = canonicalKey[@"HKCU\".Length..];
                bool exists = _registryKeyExistsProbe(subKey);
                regLocations[canonicalKey] = new ResolvedSaveLocation
                {
                    LogicalTemplatePath = _expander.CanonicalizeToLogicalPath(canonicalKey, game),
                    ResolvedFullPath = canonicalKey,
                    Category = SaveFileCategory.RegistryExport,
                    SourceRule = GameDataSourceRule.NativeRule,
                    SourceLabel = customLabel,
                    Exists = exists,
                    IsSafe = true,
                    IsCustom = true,
                    CustomLocationId = custom.LocationId,
                    FileCount = exists ? 1 : 0,
                    TotalBytes = 0,
                };
                continue;
            }

            string expandedCustom = custom.PathOrRegistryKey.StartsWith('<')
                ? _expander.ExpandLogicalPath(custom.PathOrRegistryKey, game)
                : custom.PathOrRegistryKey;

            var (isSafeFs, fsReason) = TrustedSavePathFence.ValidateTrustedFilesystemTarget(
                expandedCustom,
                game.InstallDir,
                _knownFolderProvider,
                spawnDataRoot: _database.DataRootPath,
                isUserCustomPath: true);
            if (!isSafeFs)
            {
                warnings.Add(fsReason ?? $"安全围栏已跳过不安全的自定义路径：{expandedCustom}");
                continue;
            }

            string normCustom = NormalizePathForDedup(expandedCustom);
            var (diskExists, fileCount, totalBytes) = LudusaviPlaceholderExpander.MeasurePathSkippingIgnored(normCustom);
            SaveFileCategory cat = custom.Category == SaveFileCategory.Config
                ? SaveFileCategory.Config
                : SaveFileCategory.Save;

            MergeFilesystemLocation(
                fsLocations,
                normCustom,
                new ResolvedSaveLocation
                {
                    LogicalTemplatePath = _expander.CanonicalizeToLogicalPath(normCustom, game),
                    ResolvedFullPath = normCustom,
                    Category = cat,
                    SourceRule = GameDataSourceRule.NativeRule,
                    SourceLabel = customLabel,
                    Exists = diskExists,
                    IsSafe = true,
                    IsCustom = true,
                    CustomLocationId = custom.LocationId,
                    FileCount = fileCount,
                    TotalBytes = totalBytes,
                });
        }

        // 1. Resolve from SQLite ludusavi_rules (indexed by SteamAppId / GogId / cleaned title & aliases).
        LudusaviRuleRecord? matchedRule = await FindMatchingRuleAsync(game, ct).ConfigureAwait(false);
        if (matchedRule is not null)
        {
            bool hasWindowsNativeSave = matchedRule.SavePaths.Any(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p));
            foreach (string template in matchedRule.SavePaths)
            {
                ct.ThrowIfCancellationRequested();
                await ExpandAndAddFilesystemRuleAsync(
                    game,
                    template,
                    SaveFileCategory.Save,
                    hasWindowsNativeSave,
                    fsLocations,
                    warnings,
                    ct).ConfigureAwait(false);
            }

            bool hasWindowsNativeConfig = matchedRule.ConfigPaths.Any(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p));
            foreach (string template in matchedRule.ConfigPaths)
            {
                ct.ThrowIfCancellationRequested();
                await ExpandAndAddFilesystemRuleAsync(
                    game,
                    template,
                    SaveFileCategory.Config,
                    hasWindowsNativeConfig,
                    fsLocations,
                    warnings,
                    ct).ConfigureAwait(false);
            }

            foreach (string rawRegKey in matchedRule.RegistryRules)
            {
                ct.ThrowIfCancellationRequested();
                await AddRegistryRuleAsync(
                    game,
                    rawRegKey,
                    GameDataSourceRule.Ludusavi,
                    "Ludusavi",
                    regLocations,
                    warnings,
                    ct).ConfigureAwait(false);
            }
        }

        // 2. Merge multi-source discoveries from IGameDataManagementService
        //    (PCGamingWiki + Unity + Unreal + Steam + Emulator + Native).
        IReadOnlyList<GameDataEntryDto> inspectedEntries = [];
        try
        {
            inspectedEntries = await _gameDataService
                .InspectGameDataAsync(gameId, refreshOnlineRules, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "IGameDataManagementService.InspectGameDataAsync failed for game {GameId}.", gameId);
            }

            warnings.Add($"辅助存档发现服务调用失败，已仅使用内置规则：{ex.Message}");
        }

        foreach (GameDataEntryDto entry in inspectedEntries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Category == GameDataCategory.AchievementData || string.IsNullOrWhiteSpace(entry.PathOrRegistryKey))
            {
                continue;
            }

            if (!entry.IsSafeToClean)
            {
                warnings.Add(entry.SafetyNote ?? $"已跳过不安全的发现路径：{entry.PathOrRegistryKey}");
                continue;
            }

            if (entry.IsRegistryEntry)
            {
                await AddRegistryRuleAsync(
                    game,
                    entry.PathOrRegistryKey,
                    entry.SourceRule,
                    entry.EffectiveSourceLabel,
                    regLocations,
                    warnings,
                    ct).ConfigureAwait(false);
                continue;
            }

            SaveFileCategory category = entry.Category == GameDataCategory.ConfigAndLogs
                ? SaveFileCategory.Config
                : SaveFileCategory.Save;

            string normalizedPath;
            try
            {
                normalizedPath = NormalizePathForDedup(entry.PathOrRegistryKey);
            }
            catch
            {
                warnings.Add($"已跳过格式非法的发现路径：{entry.PathOrRegistryKey}");
                continue;
            }

            if (LudusaviPlaceholderExpander.IsReparsePoint(normalizedPath))
            {
                warnings.Add($"已跳过符号链接或目录联接点：{normalizedPath}");
                continue;
            }

            GameDataSafetyCheckResult safety = await _gameDataService
                .ValidateSaveRestoreTargetAsync(game.GameId, normalizedPath, ct)
                .ConfigureAwait(false);
            if (!safety.IsSafe)
            {
                warnings.Add(safety.Reason ?? $"安全围栏已跳过不安全路径：{normalizedPath}");
                continue;
            }

            var (diskExists, diskFileCount, diskBytes) = LudusaviPlaceholderExpander.MeasurePathSkippingIgnored(normalizedPath);
            bool effectiveExists = diskExists || entry.Exists;
            int effectiveFileCount = diskExists ? diskFileCount : entry.FileCount;
            long effectiveTotalBytes = diskExists ? diskBytes : entry.TotalBytes;

            var candidate = new ResolvedSaveLocation
            {
                LogicalTemplatePath = _expander.CanonicalizeToLogicalPath(normalizedPath, game),
                ResolvedFullPath = normalizedPath,
                Category = category,
                SourceRule = entry.SourceRule,
                SourceLabel = entry.EffectiveSourceLabel,
                Exists = effectiveExists,
                IsSafe = true,
                FileCount = effectiveFileCount,
                TotalBytes = effectiveTotalBytes,
            };

            MergeFilesystemLocation(fsLocations, normalizedPath, candidate);
        }

        List<ResolvedSaveLocation> saveLocations = fsLocations.Values
            .Where(static l => l.Category == SaveFileCategory.Save)
            .OrderByDescending(static l => l.Exists)
            .ThenBy(static l => l.ResolvedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ResolvedSaveLocation> configLocations = fsLocations.Values
            .Where(static l => l.Category == SaveFileCategory.Config)
            .OrderByDescending(static l => l.Exists)
            .ThenBy(static l => l.ResolvedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<ResolvedSaveLocation> registryKeys = regLocations.Values
            .OrderByDescending(static l => l.Exists)
            .ThenBy(static l => l.ResolvedFullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allLocations = new List<ResolvedSaveLocation>(saveLocations.Count + configLocations.Count + registryKeys.Count);
        allLocations.AddRange(saveLocations);
        allLocations.AddRange(configLocations);
        allLocations.AddRange(registryKeys);

        return new ResolvedSaveLayout
        {
            GameId = game.GameId,
            MatchedRuleName = matchedRule?.RuleName,
            Locations = allLocations,
            SaveLocations = saveLocations,
            ConfigLocations = configLocations,
            RegistryKeys = registryKeys,
            Warnings = warnings,
        };
    }

    private async Task<LudusaviRuleRecord?> FindMatchingRuleAsync(GameRecord game, CancellationToken ct)
    {
        if (game.SteamAppId is > 0)
        {
            LudusaviRuleRecord? bySteam = await _database
                .FindLudusaviRuleAsync(steamAppId: game.SteamAppId, cancellationToken: ct)
                .ConfigureAwait(false);
            if (bySteam is not null)
            {
                return bySteam;
            }
        }

        if (!string.IsNullOrWhiteSpace(game.GogGameId))
        {
            LudusaviRuleRecord? byGog = await _database
                .FindLudusaviRuleAsync(gogId: game.GogGameId, cancellationToken: ct)
                .ConfigureAwait(false);
            if (byGog is not null)
            {
                return byGog;
            }
        }

        foreach (string candidateTitle in BuildTitleLookupCandidates(game))
        {
            LudusaviRuleRecord? byTitle = await _database
                .FindLudusaviRuleAsync(title: candidateTitle, cancellationToken: ct)
                .ConfigureAwait(false);
            if (byTitle is not null)
            {
                return byTitle;
            }
        }

        return null;
    }

    private async Task ExpandAndAddFilesystemRuleAsync(
        GameRecord game,
        string template,
        SaveFileCategory category,
        bool hasWindowsNativeAlternative,
        Dictionary<string, ResolvedSaveLocation> fsLocations,
        List<string> warnings,
        CancellationToken ct)
    {
        IReadOnlyList<string> expandedPaths = _expander.ExpandTemplate(template, game, hasWindowsNativeAlternative);
        foreach (string resolvedPath in expandedPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (LudusaviPlaceholderExpander.IsReparsePoint(resolvedPath))
            {
                warnings.Add($"已跳过符号链接或目录联接点：{resolvedPath}");
                continue;
            }

            // Hard redlines are always enforced first (system dirs, drive root, ReparsePoint, executables, SPAWN data, engine dirs).
            var (trustedSafe, trustedReason) = TrustedSavePathFence.ValidateTrustedFilesystemTarget(
                resolvedPath,
                game.InstallDir,
                _knownFolderProvider,
                spawnDataRoot: _database.DataRootPath,
                isUserCustomPath: false);
            if (!trustedSafe)
            {
                warnings.Add(trustedReason ?? $"安全围栏已跳过不安全路径：{resolvedPath}");
                continue;
            }

            // Also consult IGameDataManagementService; only relax its heuristic 10-folder / 3-char whitelist
            // for matched Ludusavi rules (fixing e.g. b1/Saved/SaveGames, preferences, runs, SNAppData).
            GameDataSafetyCheckResult safety = await _gameDataService
                .ValidateSaveRestoreTargetAsync(game.GameId, resolvedPath, ct)
                .ConfigureAwait(false);
            if (!safety.IsSafe && !TrustedSavePathFence.IsHeuristicStrictFenceRejection(safety.Reason))
            {
                warnings.Add(safety.Reason ?? $"安全围栏已跳过不安全路径：{resolvedPath}");
                continue;
            }

            var (exists, fileCount, totalBytes) = LudusaviPlaceholderExpander.MeasurePathSkippingIgnored(resolvedPath);
            string normalizedKey = NormalizePathForDedup(resolvedPath);

            var location = new ResolvedSaveLocation
            {
                LogicalTemplatePath = _expander.CanonicalizeToLogicalPath(normalizedKey, game),
                ResolvedFullPath = normalizedKey,
                Category = category,
                SourceRule = GameDataSourceRule.Ludusavi,
                SourceLabel = "Ludusavi",
                Exists = exists,
                IsSafe = true,
                FileCount = fileCount,
                TotalBytes = totalBytes,
            };

            MergeFilesystemLocation(fsLocations, normalizedKey, location);
        }
    }

    private async Task AddRegistryRuleAsync(
        GameRecord game,
        string rawRegKey,
        GameDataSourceRule sourceRule,
        string sourceLabel,
        Dictionary<string, ResolvedSaveLocation> regLocations,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(
                rawRegKey,
                out string canonicalKey,
                out string subKeyUnderCurrentUser,
                out string? error))
        {
            warnings.Add(error ?? $"已跳过非法的注册表规则：{rawRegKey}");
            return;
        }

        GameDataSafetyCheckResult safety = await _gameDataService
            .ValidateSaveRestoreTargetAsync(game.GameId, canonicalKey, ct)
            .ConfigureAwait(false);
        if (!safety.IsSafe)
        {
            warnings.Add(safety.Reason ?? $"安全围栏已跳过不安全注册表项：{canonicalKey}");
            return;
        }

        if (regLocations.ContainsKey(canonicalKey))
        {
            return;
        }

        bool exists = _registryKeyExistsProbe(subKeyUnderCurrentUser);
        regLocations[canonicalKey] = new ResolvedSaveLocation
        {
            LogicalTemplatePath = _expander.CanonicalizeToLogicalPath(canonicalKey, game),
            ResolvedFullPath = canonicalKey,
            Category = SaveFileCategory.RegistryExport,
            SourceRule = sourceRule,
            SourceLabel = sourceLabel,
            Exists = exists,
            IsSafe = true,
            FileCount = exists ? 1 : 0,
            TotalBytes = 0,
        };
    }

    private static void MergeFilesystemLocation(
        Dictionary<string, ResolvedSaveLocation> map,
        string key,
        ResolvedSaveLocation incoming)
    {
        if (!map.TryGetValue(key, out ResolvedSaveLocation? existing))
        {
            map[key] = incoming;
            return;
        }

        // Never downgrade a Save location to Config on duplicate merge.
        SaveFileCategory mergedCategory = existing.Category == SaveFileCategory.Save || incoming.Category == SaveFileCategory.Save
            ? SaveFileCategory.Save
            : existing.Category;

        bool mergedExists = existing.Exists || incoming.Exists;
        int mergedFileCount = Math.Max(existing.FileCount, incoming.FileCount);
        long mergedBytes = Math.Max(existing.TotalBytes, incoming.TotalBytes);

        map[key] = existing with
        {
            Category = mergedCategory,
            Exists = mergedExists,
            IsCustom = existing.IsCustom || incoming.IsCustom,
            CustomLocationId = existing.CustomLocationId ?? incoming.CustomLocationId,
            FileCount = mergedFileCount,
            TotalBytes = mergedBytes,
        };
    }

    // -------------------------------------------------------------------------
    // Dual-Format JSON Rule Import (SPAWN Rule Array + Ludusavi Manifest Object)
    // -------------------------------------------------------------------------

    private static List<LudusaviRuleRecord> ParseImportJson(string rulesJson, DateTimeOffset nowUtc)
    {
        using JsonDocument doc = JsonDocument.Parse(rulesJson, ParseJsonOptions);
        JsonElement root = doc.RootElement;

        return root.ValueKind switch
        {
            JsonValueKind.Array => ParseSpawnRuleArray(root, nowUtc),
            JsonValueKind.Object => ParseLudusaviManifestObject(root, nowUtc),
            _ => throw new JsonException("规则 JSON 根节点必须为数组（SPAWN 规则数组）或对象（Ludusavi 清单格式）。"),
        };
    }

    private static List<LudusaviRuleRecord> ParseSpawnRuleArray(JsonElement array, DateTimeOffset nowUtc)
    {
        var results = new List<LudusaviRuleRecord>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? ruleName = ReadStringProperty(item, "ruleName", "rule_name", "name");
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                continue;
            }

            int? steamAppId = ReadIntProperty(item, "steamAppId", "steam_app_id");
            string? gogId = ReadStringOrIntProperty(item, "gogId", "gog_id");
            string[] aliases = ReadStringArrayProperty(item, "aliases", "aliases_json");
            string[] savePaths = ReadStringArrayProperty(item, "savePaths", "save_paths", "save_paths_json");
            string[] configPaths = ReadStringArrayProperty(item, "configPaths", "config_paths", "config_paths_json");
            string[] rawRegistry = ReadStringArrayProperty(item, "registryRules", "registry_rules", "registry_rules_json");

            var safeRegistry = new List<string>();
            foreach (string key in rawRegistry)
            {
                if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(key, out string canonical, out _, out _))
                {
                    safeRegistry.Add(canonical);
                }
            }

            results.Add(new LudusaviRuleRecord
            {
                RuleName = ruleName.Trim(),
                SteamAppId = steamAppId,
                GogId = gogId,
                Aliases = aliases,
                SavePaths = savePaths,
                ConfigPaths = configPaths,
                RegistryRules = safeRegistry,
                UpdatedAtUtc = nowUtc,
            });
        }

        return results;
    }

    private static List<LudusaviRuleRecord> ParseLudusaviManifestObject(JsonElement manifestRoot, DateTimeOffset nowUtc)
    {
        var results = new List<LudusaviRuleRecord>();
        var aliasesByTarget = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: collect pure alias redirects (Ludusavi uses { "Alias Title": { "alias": "Canonical Title" } }).
        foreach (JsonProperty entry in manifestRoot.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? aliasTarget = ReadStringProperty(entry.Value, "alias");
            if (!string.IsNullOrWhiteSpace(aliasTarget)
                && !TryGetPropertyCaseInsensitive(entry.Value, "files", out _)
                && !TryGetPropertyCaseInsensitive(entry.Value, "registry", out _))
            {
                if (!aliasesByTarget.TryGetValue(aliasTarget.Trim(), out List<string>? list))
                {
                    list = [];
                    aliasesByTarget[aliasTarget.Trim()] = list;
                }

                list.Add(entry.Name.Trim());
            }
        }

        // Pass 2: parse full Ludusavi game entries with files/registry/tags/when filtering.
        foreach (JsonProperty entry in manifestRoot.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string ruleName = entry.Name.Trim();
            if (string.IsNullOrWhiteSpace(ruleName))
            {
                continue;
            }

            JsonElement body = entry.Value;

            // Skip pure alias redirect entries.
            if (!string.IsNullOrWhiteSpace(ReadStringProperty(body, "alias"))
                && !TryGetPropertyCaseInsensitive(body, "files", out _)
                && !TryGetPropertyCaseInsensitive(body, "registry", out _))
            {
                continue;
            }

            int? steamAppId = ReadNestedIntId(body, "steam");
            string? gogId = ReadNestedStringOrIntId(body, "gog");

            var aliases = new List<string>(ReadStringArrayProperty(body, "aliases"));
            if (aliasesByTarget.TryGetValue(ruleName, out List<string>? mappedAliases))
            {
                foreach (string a in mappedAliases)
                {
                    if (!aliases.Contains(a, StringComparer.OrdinalIgnoreCase))
                    {
                        aliases.Add(a);
                    }
                }
            }

            var savePaths = new List<string>();
            var configPaths = new List<string>();
            var registryRules = new List<string>();

            if (TryGetPropertyCaseInsensitive(body, "files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty fileProp in filesEl.EnumerateObject())
                {
                    string pathTemplate = fileProp.Name.Trim();
                    if (string.IsNullOrWhiteSpace(pathTemplate))
                    {
                        continue;
                    }

                    JsonElement meta = fileProp.Value;
                    if (meta.ValueKind == JsonValueKind.Object && !MatchesWindowsWhenFilter(meta))
                    {
                        continue;
                    }

                    (bool isSave, bool isConfig) = ClassifyLudusaviTags(meta);
                    if (isConfig && !isSave)
                    {
                        if (!configPaths.Contains(pathTemplate, StringComparer.OrdinalIgnoreCase))
                        {
                            configPaths.Add(pathTemplate);
                        }
                    }
                    else
                    {
                        if (!savePaths.Contains(pathTemplate, StringComparer.OrdinalIgnoreCase))
                        {
                            savePaths.Add(pathTemplate);
                        }
                    }
                }
            }

            if (TryGetPropertyCaseInsensitive(body, "registry", out JsonElement regEl) && regEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty regProp in regEl.EnumerateObject())
                {
                    string rawKey = regProp.Name.Trim();
                    if (string.IsNullOrWhiteSpace(rawKey))
                    {
                        continue;
                    }

                    JsonElement meta = regProp.Value;
                    if (meta.ValueKind == JsonValueKind.Object && !MatchesWindowsWhenFilter(meta))
                    {
                        continue;
                    }

                    (bool isSave, bool isConfig) = ClassifyLudusaviTags(meta);
                    if (isConfig && !isSave)
                    {
                        continue;
                    }

                    if (DeterministicHkcuRegistrySerializer.TryNormalizeHkcuSubKey(rawKey, out string canonicalKey, out _, out _)
                        && !registryRules.Contains(canonicalKey, StringComparer.OrdinalIgnoreCase))
                    {
                        registryRules.Add(canonicalKey);
                    }
                }
            }

            if (savePaths.Count == 0 && configPaths.Count == 0 && registryRules.Count == 0)
            {
                continue;
            }

            results.Add(new LudusaviRuleRecord
            {
                RuleName = ruleName,
                SteamAppId = steamAppId,
                GogId = gogId,
                Aliases = aliases,
                SavePaths = FilterRedundantXdgPaths(savePaths),
                ConfigPaths = FilterRedundantXdgPaths(configPaths),
                RegistryRules = registryRules,
                UpdatedAtUtc = nowUtc,
            });
        }

        return results;
    }

    private static List<string> FilterRedundantXdgPaths(List<string> paths)
    {
        bool hasWindowsNative = paths.Any(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p));
        if (!hasWindowsNative)
        {
            return paths;
        }

        return paths.Where(static p => !LudusaviPlaceholderExpander.UsesXdgPlaceholder(p)).ToList();
    }

    private static bool MatchesWindowsWhenFilter(JsonElement metaObject)
    {
        if (!TryGetPropertyCaseInsensitive(metaObject, "when", out JsonElement whenEl))
        {
            return true;
        }

        if (whenEl.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        int clauseCount = 0;
        foreach (JsonElement clause in whenEl.EnumerateArray())
        {
            clauseCount++;
            if (clause.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (!TryGetPropertyCaseInsensitive(clause, "os", out JsonElement osEl)
                || osEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return true;
            }

            if (osEl.ValueKind == JsonValueKind.String
                && string.Equals(osEl.GetString()?.Trim(), "windows", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return clauseCount == 0;
    }

    private static (bool IsSave, bool IsConfig) ClassifyLudusaviTags(JsonElement metaObject)
    {
        if (metaObject.ValueKind != JsonValueKind.Object
            || !TryGetPropertyCaseInsensitive(metaObject, "tags", out JsonElement tagsEl)
            || tagsEl.ValueKind != JsonValueKind.Array)
        {
            return (true, false);
        }

        bool isSave = false;
        bool isConfig = false;
        int tagCount = 0;

        foreach (JsonElement tag in tagsEl.EnumerateArray())
        {
            if (tag.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? val = tag.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(val))
            {
                continue;
            }

            tagCount++;
            if (string.Equals(val, "save", StringComparison.OrdinalIgnoreCase))
            {
                isSave = true;
            }
            else if (string.Equals(val, "config", StringComparison.OrdinalIgnoreCase))
            {
                isConfig = true;
            }
        }

        return tagCount == 0 ? (true, false) : (isSave, isConfig);
    }

    private static List<string> BuildTitleLookupCandidates(GameRecord game)
    {
        var candidates = new List<string>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                string trimmed = value.Trim();
                if (seen.Add(trimmed))
                {
                    candidates.Add(trimmed);
                }
            }
        }

        void AddWithCleaned(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            Add(raw);

            // Split bilingual titles like "艾尔登法环 / Elden Ring" or "黑神话：悟空 | Black Myth: Wukong".
            string[] parts = raw.Split(['/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1)
            {
                foreach (string part in parts)
                {
                    Add(part);
                    Add(CleanTitleForRuleLookup(part));
                }
            }

            Add(CleanTitleForRuleLookup(raw));
        }

        AddWithCleaned(game.Title);
        AddWithCleaned(game.SortTitle);

        if (!string.IsNullOrWhiteSpace(game.InstallDir))
        {
            string leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(game.InstallDir));
            AddWithCleaned(leaf);
        }

        return candidates;
    }

    internal static string CleanTitleForRuleLookup(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string working = rawTitle.Trim();
        working = LeadingBracketNoiseRegex().Replace(working, string.Empty).Trim();
        working = TrailingNoiseRegex().Replace(working, string.Empty).Trim();
        working = ReleaseAndVersionTailRegex().Replace(working, string.Empty).Trim();

        if (!working.Contains(' ') && (working.Contains('.') || working.Contains('_')))
        {
            working = working.Replace('.', ' ').Replace('_', ' ');
        }
        else if (working.Contains('_'))
        {
            working = working.Replace('_', ' ');
        }

        working = MultiSpaceRegex().Replace(working, " ").Trim(' ', '-', '_', '.', ':', '–', '—');
        return working;
    }

    private static bool DefaultRegistryKeyExists(string subKeyUnderCurrentUser)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(subKeyUnderCurrentUser))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKeyUnderCurrentUser, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePathForDedup(string path)
    {
        string full = Path.GetFullPath(path.Trim());
        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrWhiteSpace(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return full;
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (JsonProperty prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadStringProperty(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetPropertyCaseInsensitive(obj, name, out JsonElement el) && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }

        return null;
    }

    private static int? ReadIntProperty(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetPropertyCaseInsensitive(obj, name, out JsonElement el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int num))
            {
                return num;
            }

            if (el.ValueKind == JsonValueKind.String
                && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadStringOrIntProperty(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetPropertyCaseInsensitive(obj, name, out JsonElement el))
            {
                continue;
            }

            if (el.ValueKind == JsonValueKind.String)
            {
                string? s = el.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            }

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long n))
            {
                return n.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static int? ReadNestedIntId(JsonElement obj, string parentName)
    {
        if (TryGetPropertyCaseInsensitive(obj, parentName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return ReadIntProperty(nested, "id");
        }

        return null;
    }

    private static string? ReadNestedStringOrIntId(JsonElement obj, string parentName)
    {
        if (TryGetPropertyCaseInsensitive(obj, parentName, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
        {
            return ReadStringOrIntProperty(nested, "id");
        }

        return null;
    }

    private static string[] ReadStringArrayProperty(JsonElement obj, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryGetPropertyCaseInsensitive(obj, name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var list = new List<string>();
            foreach (JsonElement item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        list.Add(s.Trim());
                    }
                }
            }

            return [.. list];
        }

        return [];
    }
}
