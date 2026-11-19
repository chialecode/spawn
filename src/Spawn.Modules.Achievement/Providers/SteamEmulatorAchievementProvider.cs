using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Spawn.Contracts;

namespace Spawn.Modules.Achievement;

/// <summary>
/// Discovers and parses 15+ offline Steam emulator save formats (<c>gbe_fork</c>, <c>Goldberg</c>, <c>RUNE</c>,
/// <c>CODEX</c>, <c>PLAZA</c>, <c>EMPRESS</c>, <c>TENOKE</c>, <c>Online-Fix</c>, <c>ALI213</c>, <c>3DM</c>,
/// <c>RLD!</c>, <c>SKIDROW</c>, <c>Hoodlum</c>, <c>SmartSteamEmu</c>, <c>GreenLuma</c>, <c>CreamAPI</c>).
/// </summary>
public sealed class SteamEmulatorAchievementProvider : IAchievementSourceProvider
{
    public const string Id = "steam.emulators";

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public string ProviderId => Id;
    public int Priority => 10;

    public IReadOnlyList<AchievementWatchTarget> DiscoverTargets(AchievementSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var targets = new List<AchievementWatchTarget>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string installDir = context.Game.InstallDir;
        int? appId = context.EffectiveSteamAppId ?? SteamEmulatorFormatParsers.TryDetectSteamAppIdFromDirectory(installDir);
        string? appIdStr = appId?.ToString(CultureInfo.InvariantCulture);

        string appData = context.GetAppDataRoaming();
        string localAppData = context.GetLocalAppData();
        string publicDocs = context.GetPublicDocuments();
        string userDocs = context.GetUserDocuments();
        string programData = context.GetProgramData();

        void AddFileTarget(
            EmulatorFlavor flavor,
            string label,
            string? path,
            bool includeWhenMissing = false,
            AchievementCaptureMode mode = AchievementCaptureMode.SteamEmulator)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!includeWhenMissing && !File.Exists(full))
            {
                return;
            }

            if (!seenPaths.Add(full))
            {
                return;
            }

            targets.Add(new AchievementWatchTarget
            {
                ProviderId = ProviderId,
                Flavor = flavor,
                CaptureMode = mode,
                ChannelLabel = label,
                FilePath = full,
                IsPrimary = targets.Count == 0,
            });
        }

        bool hasGbeSettings = false;
        string customGseFolderName = "GSE Saves";
        var (sceneFlavor, sceneAppId, sceneLocalSave, sceneEmuDir) = SteamEmulatorFormatParsers.DetectSceneSteamEmulatorInfo(installDir);
        if (appId is null && sceneAppId is > 0)
        {
            appId = sceneAppId;
            appIdStr = appId.Value.ToString(CultureInfo.InvariantCulture);
        }

        // Inspect local steam_settings inside InstallDir for gbe_fork / Goldberg portable or custom folder overrides
        if (!string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
        {
            foreach (string cfgIni in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "configs.user.ini", maxDepth: 6))
            {
                hasGbeSettings = true;
                try
                {
                    var (folderName, localSavePath) = SteamEmulatorFormatParsers.ParseGbeConfigsUserIni(File.ReadAllText(cfgIni));
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        customGseFolderName = folderName;
                    }

                    if (!string.IsNullOrWhiteSpace(localSavePath))
                    {
                        string settingsDir = Path.GetDirectoryName(cfgIni) ?? installDir;
                        string baseDir = Directory.GetParent(settingsDir)?.FullName ?? installDir;
                        string resolvedLocal = Path.IsPathRooted(localSavePath)
                            ? localSavePath
                            : Path.Combine(baseDir, localSavePath);
                        if (appIdStr is not null)
                        {
                            AddFileTarget(EmulatorFlavor.GbeFork, "Goldberg GSE (本地存档) · 就绪",
                                Path.Combine(resolvedLocal, appIdStr, "achievements.json"), includeWhenMissing: true);
                        }
                        AddFileTarget(EmulatorFlavor.GbeFork, "Goldberg GSE (本地存档) · 就绪",
                            Path.Combine(resolvedLocal, "achievements.json"));
                    }
                }
                catch
                {
                    // Ignore malformed user ini.
                }
            }

            foreach (string localSaveTxt in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "local_save.txt", maxDepth: 6))
            {
                hasGbeSettings = true;
                try
                {
                    string rel = File.ReadAllText(localSaveTxt).Trim();
                    string dllDir = Path.GetDirectoryName(localSaveTxt) ?? installDir;
                    string saveRoot = string.IsNullOrWhiteSpace(rel) ? Path.Combine(dllDir, "steam_saves") : Path.Combine(dllDir, rel);
                    if (appIdStr is not null)
                    {
                        AddFileTarget(EmulatorFlavor.Goldberg, "Goldberg (便携存档) · 就绪",
                            Path.Combine(saveRoot, appIdStr, "achievements.json"), includeWhenMissing: true);
                    }
                }
                catch
                {
                    // Ignore.
                }
            }

            foreach (string schemaJson in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "achievements.json", maxDepth: 6))
            {
                string? parentName = Path.GetFileName(Path.GetDirectoryName(schemaJson));
                if (string.Equals(parentName, "steam_settings", StringComparison.OrdinalIgnoreCase))
                {
                    hasGbeSettings = true;
                }
                else
                {
                    AddFileTarget(EmulatorFlavor.GbeFork, "Goldberg GSE · 就绪", schemaJson);
                }
            }

            if (sceneLocalSave && appIdStr is not null && !string.IsNullOrWhiteSpace(sceneEmuDir))
            {
                string groupFolder = sceneFlavor == EmulatorFlavor.Codex ? "CODEX" : "RUNE";
                string groupLabel = sceneFlavor == EmulatorFlavor.Codex ? "CODEX 离线成就 · 就绪" : "RUNE 离线成就 · 就绪";
                var flavor = sceneFlavor == EmulatorFlavor.Unknown ? EmulatorFlavor.Rune : sceneFlavor;
                AddFileTarget(flavor, groupLabel,
                    Path.Combine(sceneEmuDir, "Steam", groupFolder, appIdStr, "achievements.ini"), includeWhenMissing: true);
                AddFileTarget(flavor, groupLabel,
                    Path.Combine(installDir, "Steam", groupFolder, appIdStr, "achievements.ini"), includeWhenMissing: true);
            }

            // 5. TENOKE: <GameDir>/SteamData/user_stats.ini
            foreach (string tenokeStats in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "user_stats.ini", maxDepth: 6))
            {
                AddFileTarget(EmulatorFlavor.Tenoke, "TENOKE 离线成就 · 就绪", tenokeStats);
            }

            if (SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "tenoke.ini", maxDepth: 6).Any())
            {
                AddFileTarget(EmulatorFlavor.Tenoke, "TENOKE 离线成就 · 就绪",
                    Path.Combine(installDir, "SteamData", "user_stats.ini"), includeWhenMissing: true);
            }

            // 7. ALI213: <GameDir>/Profile/*/Stats/Achievements.Bin
            foreach (string aliBin in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "Achievements.Bin", maxDepth: 6))
            {
                AddFileTarget(EmulatorFlavor.Ali213, "ALI213 离线成就 · 就绪", aliBin);
            }

            if (SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "ALI213.ini", maxDepth: 6).Any()
                || SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "valve.ini", maxDepth: 6).Any())
            {
                AddFileTarget(EmulatorFlavor.Ali213, "ALI213 离线成就 · 就绪",
                    Path.Combine(installDir, "Profile", "ALI213", "Stats", "Achievements.Bin"), includeWhenMissing: true);
                AddFileTarget(EmulatorFlavor.Ali213, "ALI213 离线成就 · 就绪",
                    Path.Combine(installDir, "Profile", "VALVE", "Stats", "Achievements.Bin"), includeWhenMissing: true);
            }

            // 8. 3DM local directory: <GameDir>/3DMGAME/*/stats|storage/achievements.ini
            string threeDmDir = Path.Combine(installDir, "3DMGAME");
            if (Directory.Exists(threeDmDir))
            {
                foreach (string ini in SteamEmulatorFormatParsers.EnumerateFilesSafe(threeDmDir, "achievements.ini", maxDepth: 4))
                {
                    AddFileTarget(EmulatorFlavor.ThreeDm, "3DM 离线成就 · 就绪", ini);
                }
            }

            // 9. SKIDROW local SteamEmu/UserStats/achiev.ini
            foreach (string achIni in SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "achiev.ini", maxDepth: 4))
            {
                AddFileTarget(EmulatorFlavor.Skidrow, "SKIDROW 离线成就 · 就绪", achIni);
            }
        }

        if (appIdStr is not null)
        {
            var gbeMode = context.Game.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated
                ? AchievementCaptureMode.GbeForkActivated
                : AchievementCaptureMode.SteamEmulator;
            string gbeLabel = gbeMode == AchievementCaptureMode.GbeForkActivated
                ? "Goldberg GSE (已无损激活) · 就绪"
                : "Goldberg GSE · 就绪";

            bool isSceneCodex = sceneFlavor == EmulatorFlavor.Codex;
            bool isSceneRune = sceneFlavor == EmulatorFlavor.Rune;
            bool isScenePlaza = sceneFlavor == EmulatorFlavor.Plaza;
            bool hasSceneSteamEmu = sceneFlavor is EmulatorFlavor.Codex or EmulatorFlavor.Rune or EmulatorFlavor.Plaza;

            // Prioritize Public Documents CODEX / RUNE when *.cdx / *.rne / steam_emu.ini is detected in game subdirectories
            if (!string.IsNullOrWhiteSpace(publicDocs) && hasSceneSteamEmu)
            {
                if (isSceneCodex)
                {
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "CODEX", appIdStr, "achievements.ini"), includeWhenMissing: true);
                    AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "RUNE", appIdStr, "achievements.ini"), includeWhenMissing: true);
                }
                else if (isScenePlaza)
                {
                    AddFileTarget(EmulatorFlavor.Plaza, "PLAZA 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "PLAZA", appIdStr, "achievements.ini"), includeWhenMissing: true);
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "CODEX", appIdStr, "achievements.ini"), includeWhenMissing: true);
                }
                else
                {
                    AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "RUNE", appIdStr, "achievements.ini"), includeWhenMissing: true);
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(publicDocs, "Steam", "CODEX", appIdStr, "achievements.ini"), includeWhenMissing: true);
                }
            }

            // 1 & 2. gbe_fork (GSE Saves + custom) & original Goldberg
            if (!string.IsNullOrWhiteSpace(appData))
            {
                AddFileTarget(EmulatorFlavor.GbeFork, gbeLabel,
                    Path.Combine(appData, customGseFolderName, appIdStr, "achievements.json"),
                    includeWhenMissing: hasGbeSettings || gbeMode == AchievementCaptureMode.GbeForkActivated,
                    mode: gbeMode);

                if (!string.Equals(customGseFolderName, "GSE Saves", StringComparison.OrdinalIgnoreCase))
                {
                    AddFileTarget(EmulatorFlavor.GbeFork, gbeLabel,
                        Path.Combine(appData, "GSE Saves", appIdStr, "achievements.json"),
                        includeWhenMissing: hasGbeSettings,
                        mode: gbeMode);
                }

                AddFileTarget(EmulatorFlavor.Goldberg, "Goldberg Emu · 就绪",
                    Path.Combine(appData, "Goldberg SteamEmu Saves", appIdStr, "achievements.json"),
                    includeWhenMissing: hasGbeSettings);

                // 3. CODEX / RUNE in AppData (includeWhenMissing when scene steam_emu.ini is present so AppData fallback is also watched!)
                if (isSceneCodex)
                {
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "CODEX", appIdStr, "achievements.ini"), includeWhenMissing: true);
                    AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "RUNE", appIdStr, "achievements.ini"));
                }
                else if (isSceneRune)
                {
                    AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "RUNE", appIdStr, "achievements.ini"), includeWhenMissing: true);
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "CODEX", appIdStr, "achievements.ini"));
                }
                else
                {
                    AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "CODEX", appIdStr, "achievements.ini"));
                    AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                        Path.Combine(appData, "Steam", "RUNE", appIdStr, "achievements.ini"));
                }

                // 4. EMPRESS in AppData
                AddFileTarget(EmulatorFlavor.Empress, "EMPRESS 离线成就 · 就绪",
                    Path.Combine(appData, "EMPRESS", appIdStr, "remote", appIdStr, "achievements.json"));
                AddFileTarget(EmulatorFlavor.Empress, "EMPRESS 离线成就 · 就绪",
                    Path.Combine(appData, "EMPRESS", appIdStr, "remote", appIdStr, "achievements.ini"));

                // 9. Hoodlum in AppData
                string hoodlumDir = Path.Combine(appData, "Hoodlum", appIdStr);
                if (Directory.Exists(hoodlumDir))
                {
                    foreach (string f in SteamEmulatorFormatParsers.EnumerateFilesSafe(hoodlumDir, "*achiev*", maxDepth: 3))
                    {
                        AddFileTarget(EmulatorFlavor.Hoodlum, "Hoodlum 离线成就 · 就绪", f);
                    }
                }

                // 10. SmartSteamEmu (SSE): %APPDATA%/SmartSteamEmu/<AppID>/stats.bin
                bool hasSseIni = !string.IsNullOrWhiteSpace(installDir)
                                 && SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "SmartSteamEmu.ini", maxDepth: 6).Any();
                AddFileTarget(EmulatorFlavor.SmartSteamEmu, "SmartSteamEmu · 就绪",
                    Path.Combine(appData, "SmartSteamEmu", appIdStr, "stats.bin"),
                    includeWhenMissing: hasSseIni);

                // 11b. CreamAPI: %APPDATA%/CreamAPI/<AppID>/stats/CreamAPI.Achievements.cfg
                AddFileTarget(EmulatorFlavor.CreamApi, "CreamAPI 离线成就 · 就绪",
                    Path.Combine(appData, "CreamAPI", appIdStr, "stats", "CreamAPI.Achievements.cfg"));
            }

            if (!string.IsNullOrWhiteSpace(publicDocs))
            {
                bool hasOnlineFixIni = !string.IsNullOrWhiteSpace(installDir)
                                       && SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "OnlineFix.ini", maxDepth: 6).Any();

                // 3. RUNE / CODEX / PLAZA in Public Documents (when already existing on disk even if game dir lacks .cdx/.rne)
                AddFileTarget(EmulatorFlavor.Rune, "RUNE 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "RUNE", appIdStr, "achievements.ini"));
                AddFileTarget(EmulatorFlavor.Codex, "CODEX 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "CODEX", appIdStr, "achievements.ini"));
                AddFileTarget(EmulatorFlavor.Plaza, "PLAZA 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "PLAZA", appIdStr, "achievements.ini"));

                // 4. EMPRESS in Public Documents
                AddFileTarget(EmulatorFlavor.Empress, "EMPRESS 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "EMPRESS", appIdStr, "remote", appIdStr, "achievements.json"));
                AddFileTarget(EmulatorFlavor.Empress, "EMPRESS 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "EMPRESS", appIdStr, "remote", appIdStr, "achievements.ini"));
                AddFileTarget(EmulatorFlavor.Empress, "EMPRESS 离线成就 · 就绪",
                    Path.Combine(publicDocs, "EMPRESS", appIdStr, "remote", appIdStr, "achievements.json"));

                // 5b. TENOKE repack in Public Documents
                AddFileTarget(EmulatorFlavor.Tenoke, "TENOKE 离线成就 · 就绪",
                    Path.Combine(publicDocs, "Steam", "TENOKE", appIdStr, "user_stats.ini"));

                // 6. Online-Fix: %PUBLIC%/Documents/OnlineFix/<RealAppId>/Stats/Achievements.ini
                AddFileTarget(EmulatorFlavor.OnlineFix, "Online-Fix 联机成就 · 就绪",
                    Path.Combine(publicDocs, "OnlineFix", appIdStr, "Stats", "Achievements.ini"),
                    includeWhenMissing: hasOnlineFixIni);
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                // 9. SKIDROW in LocalAppData
                string skidrowDir = Path.Combine(localAppData, "SKIDROW", appIdStr);
                if (Directory.Exists(skidrowDir))
                {
                    foreach (string f in SteamEmulatorFormatParsers.EnumerateFilesSafe(skidrowDir, "*achiev*", maxDepth: 3))
                    {
                        AddFileTarget(EmulatorFlavor.Skidrow, "SKIDROW 离线成就 · 就绪", f);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(userDocs))
            {
                // ALI213 SaveType=1: Documents/VALVE/<AppID>/*/Stats/Achievements.Bin
                string valveDocDir = Path.Combine(userDocs, "VALVE", appIdStr);
                if (Directory.Exists(valveDocDir))
                {
                    foreach (string f in SteamEmulatorFormatParsers.EnumerateFilesSafe(valveDocDir, "Achievements.Bin", maxDepth: 3))
                    {
                        AddFileTarget(EmulatorFlavor.Ali213, "ALI213 离线成就 · 就绪", f);
                    }
                }

                string skidrowDocs = Path.Combine(userDocs, "SKIDROW", appIdStr);
                if (Directory.Exists(skidrowDocs))
                {
                    foreach (string f in SteamEmulatorFormatParsers.EnumerateFilesSafe(skidrowDocs, "achiev.ini", maxDepth: 3))
                    {
                        AddFileTarget(EmulatorFlavor.Skidrow, "SKIDROW 离线成就 · 就绪", f);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(programData))
            {
                // 8b. Reloaded (RLD!) & 3DM in ProgramData/Steam/<Player>/<AppID>/storage|stats/achievements.ini
                string pdSteam = Path.Combine(programData, "Steam");
                if (Directory.Exists(pdSteam))
                {
                    string[] playerDirs;
                    try
                    {
                        playerDirs = Directory.GetDirectories(pdSteam);
                    }
                    catch
                    {
                        playerDirs = [];
                    }

                    foreach (string playerDir in playerDirs)
                    {
                        AddFileTarget(EmulatorFlavor.Reloaded, "RLD! 离线成就 · 就绪",
                            Path.Combine(playerDir, appIdStr, "storage", "achievements.ini"));
                        AddFileTarget(EmulatorFlavor.Reloaded, "RLD! 离线成就 · 就绪",
                            Path.Combine(playerDir, appIdStr, "stats", "achievements.ini"));
                    }
                }
            }

            // 11. GreenLuma Registry: HKCU\Software\GLR\AppID\<AppID>\Achievements & GL2020
            foreach (string glRoot in GetGreenLumaRegistryPaths(context, appIdStr))
            {
                if (RegistryKeyExists(glRoot))
                {
                    targets.Add(new AchievementWatchTarget
                    {
                        ProviderId = ProviderId,
                        Flavor = EmulatorFlavor.GreenLuma,
                        CaptureMode = AchievementCaptureMode.SteamEmulator,
                        ChannelLabel = "GreenLuma 注册表成就 · 就绪",
                        RegistryKeyPath = glRoot,
                        IsPrimary = targets.Count == 0,
                    });
                }
            }
        }

        return targets;
    }

    public async ValueTask<IReadOnlyList<ParsedAchievementEntry>> ReadUnlocksAsync(
        AchievementWatchTarget target,
        AchievementSourceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);

        if (target.Flavor == EmulatorFlavor.GreenLuma && !string.IsNullOrWhiteSpace(target.RegistryKeyPath))
        {
            return ReadGreenLumaRegistry(target.RegistryKeyPath);
        }

        if (string.IsNullOrWhiteSpace(target.FilePath))
        {
            return [];
        }

        byte[]? raw = await ResilientFileReader.ReadSharedBytesWithRetryAsync(target.FilePath, cancellationToken).ConfigureAwait(false);
        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        return ParseBytesByFlavor(target.Flavor, target.FilePath, raw, context.Crc32ToApiName);
    }

    public static IReadOnlyList<ParsedAchievementEntry> ParseBytesByFlavor(
        EmulatorFlavor flavor,
        string? filePath,
        ReadOnlySpan<byte> raw,
        IReadOnlyDictionary<uint, string>? crc32Lookup = null)
    {
        return flavor switch
        {
            EmulatorFlavor.Goldberg or EmulatorFlavor.GbeFork => SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(raw),
            EmulatorFlavor.Empress => IsJsonFile(filePath, raw)
                ? SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(raw)
                : SteamEmulatorFormatParsers.ParseCodexRunePlazaIni(Encoding.UTF8.GetString(raw)),
            EmulatorFlavor.Codex or EmulatorFlavor.Rune or EmulatorFlavor.Plaza =>
                SteamEmulatorFormatParsers.ParseCodexRunePlazaIni(Encoding.UTF8.GetString(raw)),
            EmulatorFlavor.Tenoke => SteamEmulatorFormatParsers.ParseTenokeIni(Encoding.UTF8.GetString(raw)),
            EmulatorFlavor.OnlineFix => SteamEmulatorFormatParsers.ParseOnlineFixIni(Encoding.UTF8.GetString(raw)),
            EmulatorFlavor.Ali213 => SteamEmulatorFormatParsers.ParseAli213BinIni(raw),
            EmulatorFlavor.ThreeDm or EmulatorFlavor.Reloaded =>
                SteamEmulatorFormatParsers.ParseThreeDmAndReloadedIni(Encoding.UTF8.GetString(raw)),
            EmulatorFlavor.Skidrow or EmulatorFlavor.Hoodlum =>
                SteamEmulatorFormatParsers.ParseSkidrowHoodlum(raw),
            EmulatorFlavor.SmartSteamEmu =>
                SteamEmulatorFormatParsers.ParseSmartSteamEmuStatsBin(raw, crc32Lookup),
            EmulatorFlavor.CreamApi =>
                SteamEmulatorFormatParsers.ParseCreamApiCfg(Encoding.UTF8.GetString(raw)),
            _ => IsJsonFile(filePath, raw)
                ? SteamEmulatorFormatParsers.ParseGoldbergOrGbeForkJson(raw)
                : SteamEmulatorFormatParsers.ParseCodexRunePlazaIni(Encoding.UTF8.GetString(raw)),
        };
    }

    /// <summary>
    /// Synchronizes a manual unlock/reset operation back to a local Goldberg / <c>gbe_fork</c> JSON save when present.
    /// </summary>
    public static async Task TrySyncLocalGoldbergSaveAsync(
        string jsonFilePath,
        string apiName,
        bool unlocked,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
        {
            return;
        }

        try
        {
            var dict = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(jsonFilePath))
            {
                byte[]? existing = await ResilientFileReader.ReadSharedBytesWithRetryAsync(jsonFilePath, cancellationToken).ConfigureAwait(false);
                if (existing is { Length: > 0 })
                {
                    using var doc = JsonDocument.Parse(existing);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var inner = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            foreach (var f in prop.Value.EnumerateObject())
                            {
                                inner[f.Name] = f.Value.ValueKind switch
                                {
                                    JsonValueKind.True => true,
                                    JsonValueKind.False => false,
                                    JsonValueKind.Number when f.Value.TryGetInt64(out long l) => l,
                                    JsonValueKind.Number when f.Value.TryGetDouble(out double d) => d,
                                    JsonValueKind.String => f.Value.GetString() ?? string.Empty,
                                    _ => f.Value.GetRawText(),
                                };
                            }

                            dict[prop.Name] = inner;
                        }
                    }
                }
            }

            long nowSec = unlocked ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0;
            dict[apiName] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["earned"] = unlocked,
                ["earned_time"] = nowSec,
            };

            string? parentDir = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrWhiteSpace(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }

            string tempPath = jsonFilePath + ".spawn_tmp";
            string serialized = JsonSerializer.Serialize(dict, IndentedJsonOptions);
            await File.WriteAllTextAsync(tempPath, serialized, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, jsonFilePath, overwrite: true);
        }
        catch
        {
            // Best-effort local emulator file sync.
        }
    }

    public static IReadOnlyList<ParsedAchievementEntry> ReadGreenLumaRegistry(string subKeyPath)
    {
        var results = new List<ParsedAchievementEntry>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return [];
            }

            string[] names = key.GetValueNames();
            var timeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            foreach (string valueName in names)
            {
                if (valueName.EndsWith("_Time", StringComparison.OrdinalIgnoreCase) && valueName.Length > 5)
                {
                    string baseName = valueName[..^5];
                    if (TryConvertRegistryInt64(key.GetValue(valueName), out long ts))
                    {
                        timeMap[baseName] = ts;
                    }
                }
            }

            foreach (string valueName in names)
            {
                if (string.IsNullOrWhiteSpace(valueName)
                    || valueName.EndsWith("_Time", StringComparison.OrdinalIgnoreCase)
                    || valueName.Equals("SkipStatsAndAchievements", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryConvertRegistryInt64(key.GetValue(valueName), out long flag))
                {
                    bool unlocked = flag != 0;
                    timeMap.TryGetValue(valueName, out long ts);
                    results.Add(new ParsedAchievementEntry
                    {
                        ApiName = valueName,
                        IsUnlocked = unlocked,
                        UnlockedUtc = unlocked ? SteamEmulatorFormatParsers.UnixTimestampToUtc(ts) : null,
                    });
                }
            }
        }
        catch
        {
            // Registry unavailable or restricted.
        }

        return results;
    }

    private static IEnumerable<string> GetGreenLumaRegistryPaths(AchievementSourceContext context, string appIdStr)
    {
        if (context.EnvironmentOverrides is not null)
        {
            if (!string.IsNullOrWhiteSpace(context.EnvironmentOverrides.RegistryRootKeyOverride))
            {
                yield return $@"{context.EnvironmentOverrides.RegistryRootKeyOverride}\AppID\{appIdStr}\Achievements";
            }

            yield break;
        }

        yield return $@"SOFTWARE\GLR\AppID\{appIdStr}\Achievements";
        yield return $@"SOFTWARE\GL2020\AppID\{appIdStr}\Achievements";
    }

    private static bool RegistryKeyExists(string subKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryConvertRegistryInt64(object? val, out long result)
    {
        switch (val)
        {
            case int i:
                result = i;
                return true;
            case uint ui:
                result = ui;
                return true;
            case long l:
                result = l;
                return true;
            case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool IsJsonFile(string? path, ReadOnlySpan<byte> raw)
    {
        if (!string.IsNullOrWhiteSpace(path) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (byte b in raw)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0xEF or 0xBB or 0xBF)
            {
                continue;
            }

            return b is (byte)'{' or (byte)'[';
        }

        return false;
    }
}
