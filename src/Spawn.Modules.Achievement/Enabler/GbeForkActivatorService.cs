using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spawn.Contracts;
using Spawn.Core;

namespace Spawn.Modules.Achievement;

internal sealed record GbeEnablerManifest
{
    public required string GameId { get; init; }
    public required string TargetDllFullPath { get; init; }
    public required string BackupDllFullPath { get; init; }
    public required string OriginalSha256 { get; init; }
    public required bool ReplacedDllBinary { get; init; }
    public IReadOnlyList<string> CreatedFiles { get; init; } = [];
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];
    public DateTimeOffset AppliedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Non-destructive one-click <c>Detanup01/gbe_fork</c> offline achievement activator and byte-exact restorer
/// for <c>Tier 0</c> single-player Steamrip / FLT / CODEX / RUNE games.
/// </summary>
public sealed class GbeForkActivatorService : IDisposable
{
    public const string BackupSuffix = ".spawn_vanilla_bak";
    public const string ManifestFileName = ".spawn_enabler_manifest.json";

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly IGbeForkBinarySource _binarySource;
    private readonly PinnedGbeForkReleaseSource? _ownedBinarySource;

    public GbeForkActivatorService(
        SpawnPaths paths,
        SpawnDatabase database,
        IAntiCheatGuardService antiCheatGuard,
        AchievementModuleOptions? options = null,
        HttpClient? httpClient = null,
        IGbeForkBinarySource? binarySource = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(antiCheatGuard);

        _paths = paths;
        _database = database;
        _antiCheatGuard = antiCheatGuard;

        if (binarySource is not null)
        {
            _binarySource = binarySource;
        }
        else
        {
            // Production default: pinned gbe_fork release + SHA-256 verified, tag-scoped cache. No test-mode branches.
            var pinned = new PinnedGbeForkReleaseSource(paths, options, httpClient);
            _binarySource = pinned;
            _ownedBinarySource = pinned;
        }
    }

    /// <summary>
    /// Inspects whether a game directory contains an unconfigured Steam API / FLT stub eligible for <c>gbe_fork</c> activation.
    /// </summary>
    public static bool IsCandidateForActivation(GameRecord game)
    {
        if (game.AntiCheatTier != AntiCheatTier.Tier0
            || string.IsNullOrWhiteSpace(game.InstallDir)
            || !Directory.Exists(game.InstallDir)
            || IsOfficialSteamAppsPath(game.InstallDir))
        {
            return false;
        }

        string? dllPath = FindPrimarySteamApiDll(game.InstallDir);
        if (dllPath is null)
        {
            return false;
        }

        string dllDir = Path.GetDirectoryName(dllPath) ?? game.InstallDir;
        string settingsAchJson = Path.Combine(dllDir, "steam_settings", "achievements.json");
        bool hasFltIni = SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, "flt.ini", maxDepth: 6).Any();
        if (hasFltIni)
        {
            return true;
        }

        // If the game already ships a working CODEX/RUNE/OnlineFix/TENOKE/ALI213/SSE emulator, it does not require gbe_fork patching.
        var (sceneFlavor, _, _, _) = SteamEmulatorFormatParsers.DetectSceneSteamEmulatorInfo(game.InstallDir);
        if (sceneFlavor != EmulatorFlavor.Unknown
            || SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, "OnlineFix.ini", maxDepth: 6).Any()
            || SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, "tenoke.ini", maxDepth: 6).Any()
            || SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, "ALI213.ini", maxDepth: 6).Any()
            || SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, "SmartSteamEmu.ini", maxDepth: 6).Any())
        {
            return false;
        }

        return !File.Exists(settingsAchJson);
    }

    public async Task<GbeForkActivationResult> ActivateAsync(
        GameRecord game,
        int? effectiveSteamAppId,
        IReadOnlyList<AchievementDefinitionDto> schemaDefinitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(schemaDefinitions);

        // 1. Strict Anti-Cheat Guard & Official Steam Directory Gate
        if (game.AntiCheatTier != AntiCheatTier.Tier0)
        {
            return GbeForkActivationResult.Fail(
                $"安全熔断拦截：仅允许对纯单机（Tier 0）游戏执行离线成就激活，当前游戏安全等级为 {game.AntiCheatTier}。");
        }

        try
        {
            _antiCheatGuard.EnsureHighRiskOperationPermitted(game.AntiCheatTier, "GbeForkOfflineAchievementEnabler");
        }
        catch (Exception ex)
        {
            return GbeForkActivationResult.Fail($"反作弊守卫熔断拦截：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(game.InstallDir) || !Directory.Exists(game.InstallDir))
        {
            return GbeForkActivationResult.Fail("目标游戏安装目录不存在。");
        }

        if (IsOfficialSteamAppsPath(game.InstallDir))
        {
            return GbeForkActivationResult.Fail("安全红线拦截：严禁修改正版 Steam 库目录（steamapps\\common）下的任何文件。");
        }

        string? dllPath = FindPrimarySteamApiDll(game.InstallDir);
        if (dllPath is null)
        {
            return GbeForkActivationResult.Fail("未在游戏目录中检测到 steam_api64.dll 或 steam_api.dll。");
        }

        int appId = effectiveSteamAppId
                    ?? game.SteamAppId
                    ?? SteamEmulatorFormatParsers.TryDetectSteamAppIdFromDirectory(game.InstallDir)
                    ?? 0;
        if (appId <= 0)
        {
            return GbeForkActivationResult.Fail("未能确定该游戏的 Steam AppID，请先在游戏设置中指定 AppID 后重试。");
        }

        bool is64Bit = Is64BitPe(dllPath);
        bool alreadyGbeDll = IsAlreadyGoldbergOrGbeDll(dllPath);
        string backupPath = dllPath + BackupSuffix;

        // Compute original SHA-256 (if backup already exists from an earlier idempotent run, read original SHA from backup!)
        string shaSourcePath = File.Exists(backupPath) ? backupPath : dllPath;
        byte[] origBytes = await File.ReadAllBytesAsync(shaSourcePath, cancellationToken).ConfigureAwait(false);
        string origSha256 = Convert.ToHexStringLower(SHA256.HashData(origBytes));

        byte[]? gbeDllBytes = null;
        if (!alreadyGbeDll)
        {
            gbeDllBytes = await _binarySource.GetSteamApiDllAsync(is64Bit, cancellationToken).ConfigureAwait(false);
            if (gbeDllBytes is null || gbeDllBytes.Length == 0)
            {
                return GbeForkActivationResult.Fail(
                    $"未能获取经 SHA-256 校验的固定版本 gbe_fork {PinnedGbeForkReleaseSource.PinnedReleaseTag} 组件 {(is64Bit ? "steam_api64.dll" : "steam_api.dll")}（本地缓存缺失或校验失败，且当前无法联网下载）。");
            }
        }

        var createdFiles = new List<string>();
        var createdDirs = new List<string>();
        string dllDir = Path.GetDirectoryName(dllPath) ?? game.InstallDir;
        string steamSettingsDir = Path.Combine(dllDir, "steam_settings");

        if (!Directory.Exists(steamSettingsDir))
        {
            Directory.CreateDirectory(steamSettingsDir);
            createdDirs.Add(steamSettingsDir);
        }

        // 2. Atomic DLL backup & replacement (when DLL wasn't already gbe_fork)
        bool replacedDll = false;
        if (!alreadyGbeDll && gbeDllBytes is not null)
        {
            if (!File.Exists(backupPath))
            {
                File.Copy(dllPath, backupPath, overwrite: false);
            }

            string tempDll = dllPath + ".spawn_tmp";
            await File.WriteAllBytesAsync(tempDll, gbeDllBytes, cancellationToken).ConfigureAwait(false);
            File.Move(tempDll, dllPath, overwrite: true);
            replacedDll = true;
        }

        // 3. Generate steam_settings/steam_appid.txt, configs.user.ini, achievements.json, and LGPL notice
        string appIdTxtPath = Path.Combine(steamSettingsDir, "steam_appid.txt");
        if (!File.Exists(appIdTxtPath))
        {
            createdFiles.Add(appIdTxtPath);
        }
        await File.WriteAllTextAsync(appIdTxtPath, appId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        string userIniPath = Path.Combine(steamSettingsDir, "configs.user.ini");
        if (!File.Exists(userIniPath))
        {
            createdFiles.Add(userIniPath);
            const string userIniContent = """
                [user::general]
                account_name=SPAWN_Player
                language=schinese

                [user::saves]
                saves_folder_name=GSE Saves
                """;
            await File.WriteAllTextAsync(userIniPath, userIniContent, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        string achJsonPath = Path.Combine(steamSettingsDir, "achievements.json");
        if (!File.Exists(achJsonPath))
        {
            createdFiles.Add(achJsonPath);
        }
        string gbeSchemaJson = SerializeGbeSchemaArray(schemaDefinitions);
        await File.WriteAllTextAsync(achJsonPath, gbeSchemaJson, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        if (replacedDll)
        {
            string noticePath = Path.Combine(steamSettingsDir, "THIRD-PARTY-NOTICES-gbe_fork.txt");
            if (!File.Exists(noticePath))
            {
                createdFiles.Add(noticePath);
            }
            const string lgplNotice = "This optional achievement compatibility layer uses Detanup01/gbe_fork licensed under GNU LGPL v3.0. Users may freely replace steam_api(64).dll with any ABI-compatible build or restore the original binary via SPAWN.";
            await File.WriteAllTextAsync(noticePath, lgplNotice, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        // 4. Write reversible manifest & update SQLite ledger
        string manifestPath = Path.Combine(game.InstallDir, ManifestFileName);
        var manifest = new GbeEnablerManifest
        {
            GameId = game.GameId,
            TargetDllFullPath = dllPath,
            BackupDllFullPath = backupPath,
            OriginalSha256 = origSha256,
            ReplacedDllBinary = replacedDll,
            CreatedFiles = createdFiles,
            CreatedDirectories = createdDirs,
            AppliedAtUtc = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, IndentedJsonOptions),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        await _database.SetEnablerStateAsync(
            game.GameId,
            hasEnablerDeployed: true,
            originalApiDllSha256: origSha256,
            backupDllPath: replacedDll ? backupPath : dllPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return GbeForkActivationResult.Ok(
            replacedDll
                ? "已成功备份原始 DLL 并无损激活 gbe_fork 离线成就引擎。"
                : "检测到已有 Goldberg/GSE 组件，已无损补全中文成就配置清单。",
            backupDllPath: replacedDll ? backupPath : null,
            originalSha256: origSha256);
    }

    public async Task<GbeForkActivationResult> RestoreOriginalDllAsync(
        GameRecord game,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        if (string.IsNullOrWhiteSpace(game.InstallDir) || !Directory.Exists(game.InstallDir))
        {
            return GbeForkActivationResult.Fail("游戏安装目录不存在。");
        }

        string manifestPath = Path.Combine(game.InstallDir, ManifestFileName);
        GbeEnablerManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<GbeEnablerManifest>(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                manifest = null;
            }
        }

        var dbBackup = await _database.GetEnablerBackupAsync(game.GameId, cancellationToken).ConfigureAwait(false);
        string? backupPath = manifest?.BackupDllFullPath ?? dbBackup?.BackupDllPath;
        string? expectedSha256 = manifest?.OriginalSha256 ?? dbBackup?.OriginalSha256;

        if (string.IsNullOrWhiteSpace(backupPath) && !string.IsNullOrWhiteSpace(game.InstallDir))
        {
            backupPath = SteamEmulatorFormatParsers.EnumerateFilesSafe(game.InstallDir, $"*{BackupSuffix}", maxDepth: 5).FirstOrDefault();
        }

        if (manifest is { ReplacedDllBinary: true } || (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath)))
        {
            if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            {
                return GbeForkActivationResult.Fail("未找到原始 DLL 备份文件（.spawn_vanilla_bak），无法执行自动覆盖。");
            }

            byte[] backupBytes = await File.ReadAllBytesAsync(backupPath, cancellationToken).ConfigureAwait(false);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(backupBytes));
            if (!string.IsNullOrWhiteSpace(expectedSha256)
                && !string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return GbeForkActivationResult.Fail("备份文件 SHA-256 校验不一致，已中止还原以保护文件安全。");
            }

            string targetDllPath = manifest?.TargetDllFullPath
                                   ?? (backupPath.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase)
                                       ? backupPath[..^BackupSuffix.Length]
                                       : backupPath);

            File.Move(backupPath, targetDllPath, overwrite: true);

            byte[] restoredBytes = await File.ReadAllBytesAsync(targetDllPath, cancellationToken).ConfigureAwait(false);
            string restoredSha256 = Convert.ToHexStringLower(SHA256.HashData(restoredBytes));
            if (!string.IsNullOrWhiteSpace(expectedSha256)
                && !string.Equals(restoredSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return GbeForkActivationResult.Fail("还原后 SHA-256 复核未通过。");
            }
        }

        // Remove SPAWN-created configuration files & empty created directories cleanly
        if (manifest is not null)
        {
            foreach (string createdFile in manifest.CreatedFiles)
            {
                try
                {
                    if (File.Exists(createdFile))
                    {
                        File.Delete(createdFile);
                    }
                }
                catch
                {
                    // Ignore best-effort cleanup.
                }
            }

            foreach (string createdDir in manifest.CreatedDirectories.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (Directory.Exists(createdDir) && !Directory.EnumerateFileSystemEntries(createdDir).Any())
                    {
                        Directory.Delete(createdDir, recursive: false);
                    }
                }
                catch
                {
                    // Never recursively force-delete user folders.
                }
            }
        }

        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        await _database.SetEnablerStateAsync(
            game.GameId,
            hasEnablerDeployed: false,
            originalApiDllSha256: null,
            backupDllPath: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return GbeForkActivationResult.Ok("已 100% 校验 SHA-256 并无损还原原始 Steam API DLL，清理全部附加配置。", originalSha256: expectedSha256);
    }

    public static bool IsOfficialSteamAppsPath(string path)
    {
        string normalized = path.Replace('/', '\\');
        return normalized.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith(@"\steamapps\common", StringComparison.OrdinalIgnoreCase);
    }

    private static string SerializeGbeSchemaArray(IReadOnlyList<AchievementDefinitionDto> definitions)
    {
        var items = definitions.Select(d => new Dictionary<string, object?>
        {
            ["name"] = d.ApiName,
            ["displayName"] = new Dictionary<string, string>
            {
                ["schinese"] = d.DisplayName,
                ["english"] = d.DisplayName,
            },
            ["description"] = new Dictionary<string, string>
            {
                ["schinese"] = !string.IsNullOrWhiteSpace(d.Description) ? d.Description : (d.HiddenSpoilerDescription ?? string.Empty),
                ["english"] = !string.IsNullOrWhiteSpace(d.Description) ? d.Description : (d.HiddenSpoilerDescription ?? string.Empty),
            },
            ["hidden"] = d.IsHidden ? "1" : "0",
            ["icon"] = d.IconPath ?? string.Empty,
            ["icon_gray"] = d.IconGrayPath ?? string.Empty,
        }).ToList();

        return JsonSerializer.Serialize(items, IndentedJsonOptions);
    }

    private static string? FindPrimarySteamApiDll(string installDir)
    {
        string? api64 = SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "steam_api64.dll", maxDepth: 6).FirstOrDefault();
        if (api64 is not null)
        {
            return api64;
        }

        return SteamEmulatorFormatParsers.EnumerateFilesSafe(installDir, "steam_api.dll", maxDepth: 6).FirstOrDefault();
    }

    private static bool Is64BitPe(string dllPath)
    {
        try
        {
            using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length >= 0x44)
            {
                Span<byte> header = stackalloc byte[64];
                fs.ReadExactly(header);
                if (header[0] == (byte)'M' && header[1] == (byte)'Z')
                {
                    int peOffset = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C, 4));
                    if (peOffset >= 0 && peOffset + 6 <= fs.Length)
                    {
                        fs.Position = peOffset + 4;
                        Span<byte> mach = stackalloc byte[2];
                        fs.ReadExactly(mach);
                        ushort machine = BinaryPrimitives.ReadUInt16LittleEndian(mach);
                        if (machine == 0x8664)
                        {
                            return true;
                        }

                        if (machine == 0x014C)
                        {
                            return false;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback to filename heuristic.
        }

        return dllPath.EndsWith("64.dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlreadyGoldbergOrGbeDll(string dllPath)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(dllPath);
            string ascii = Encoding.Latin1.GetString(bytes);
            return ascii.Contains("GSE Saves", StringComparison.Ordinal)
                   || ascii.Contains("Goldberg SteamEmu Saves", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _ownedBinarySource?.Dispose();
    }
}
