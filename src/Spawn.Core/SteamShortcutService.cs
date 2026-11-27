using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;

namespace Spawn.Core;

/// <summary>
/// Injectable process controller for graceful Steam client shutdown (<c>steam.exe -shutdown</c>) and relaunch,
/// keeping all unit tests 100% offline and silent (zero real Steam spawns or termination).
/// </summary>
public interface ISteamProcessController
{
    /// <summary>
    /// Requests a graceful exit of the running Steam client via <c>steam.exe -shutdown</c> (NEVER <c>taskkill /f</c>).
    /// </summary>
    Task<bool> RequestGracefulShutdownAsync(string steamExePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the Steam client executable cleanly.
    /// </summary>
    Task<bool> StartSteamAsync(string steamExePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="ISteamProcessController"/> invoking <c>steam.exe -shutdown</c> silently
/// (<c>UseShellExecute = false, CreateNoWindow = true</c>) and launching <c>steam.exe</c> cleanly.
/// </summary>
public sealed class DefaultSteamProcessController : ISteamProcessController
{
    public static DefaultSteamProcessController Instance { get; } = new();

    private DefaultSteamProcessController()
    {
    }

    public Task<bool> RequestGracefulShutdownAsync(string steamExePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamExePath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? workDir = Path.GetDirectoryName(steamExePath);
            var psi = new ProcessStartInfo
            {
                FileName = steamExePath,
                Arguments = "-shutdown",
                WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)
                    ? workDir
                    : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? proc = Process.Start(psi);
            return Task.FromResult(proc is not null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> StartSteamAsync(string steamExePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamExePath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            string? workDir = Path.GetDirectoryName(steamExePath);
            var psi = new ProcessStartInfo
            {
                FileName = steamExePath,
                Arguments = "-silent",
                WorkingDirectory = !string.IsNullOrWhiteSpace(workDir) && Directory.Exists(workDir)
                    ? workDir
                    : Environment.CurrentDirectory,
                UseShellExecute = true,
            };

            using Process? proc = Process.Start(psi);
            return Task.FromResult(proc is not null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Configurable timing parameters for <see cref="SteamShortcutService"/> graceful restart synchronization.
/// Defaults match production budgets (25s shutdown wait, 45s startup login wait, 250ms poll) while remaining
/// fast and deterministic under unit tests.
/// </summary>
public sealed class SteamShortcutServiceOptions
{
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(25);
    public TimeSpan StartupLoginTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Implements <see cref="ISteamShortcutService"/>: distinguishes official <c>SteamLocal</c> launches from
/// <c>Tier 0/1</c> non-Steam <c>shortcuts.vdf</c> launches, detects emulator/UIPI warnings, and synchronizes
/// <c>shortcuts.vdf</c> atomically with explicit user-confirmed graceful Steam restarts.
/// </summary>
public sealed class SteamShortcutService : ISteamShortcutService
{
    private static readonly string[] GenericOfflineEmulatorFiles =
    [
        "steam_emu.ini",
        "codex.ini",
        "rune.ini",
        "tenoke.ini",
        "flt.ini",
        "ALI213.ini",
        "Valve.ini",
        "3dmgame.ini",
        "hlm.ini",
        "skidrow.ini",
        "SmartSteamEmu.ini",
        "ColdClientLoader.ini",
    ];

    private static readonly HashSet<string> SkippedInspectionFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "_CommonRedist",
        "CommonRedist",
        "Redist",
        "DirectX",
        "SPAWN_Vault",
        "Paks",
        "Movies",
        "Videos",
        "ShaderCache",
        "DerivedDataCache",
    };

    private readonly ISteamClientLocator _steamLocator;
    private readonly ISteamProcessController _processController;
    private readonly TimeProvider _timeProvider;
    private readonly SteamShortcutServiceOptions _options;
    private readonly ILogger<SteamShortcutService> _logger;

    public SteamShortcutService(
        ISteamClientLocator steamLocator,
        ISteamProcessController? processController = null,
        TimeProvider? timeProvider = null,
        SteamShortcutServiceOptions? options = null,
        ILogger<SteamShortcutService>? logger = null)
    {
        _steamLocator = steamLocator ?? throw new ArgumentNullException(nameof(steamLocator));
        _processController = processController ?? DefaultSteamProcessController.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new SteamShortcutServiceOptions();
        _logger = logger ?? NullLogger<SteamShortcutService>.Instance;
    }

    public Task<SteamLaunchPreparationResult> PrepareLaunchAsync(
        GameRecord game,
        GameSteamLaunchSettings settings,
        string? steamInstallDirOverride = null,
        uint? preferredAccountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        SteamClientDiscoveryResult discovery = _steamLocator.Discover(steamInstallDirOverride, preferredAccountId);
        if (!discovery.IsDetected || string.IsNullOrWhiteSpace(discovery.SteamExecutablePath))
        {
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.BlockedSteamNotFound,
                ErrorMessage = discovery.WarningMessage
                               ?? "未检测到本机 Steam 客户端，请在「设置 → 常规与游戏库」中手动指定 Steam 路径。",
            });
        }

        // Mode 1: Official Steam library game -> direct steam://rungameid/<AppId>, never touches shortcuts.vdf.
        if (game.PlatformSource == PlatformSource.SteamLocal && game.SteamAppId is > 0)
        {
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.ReadyOfficialSteam,
                TargetKind = SteamLaunchTargetKind.OfficialSteamApp,
                LaunchUri = SteamShortcutsVdfSerializer.BuildOfficialRunGameIdUri(game.SteamAppId.Value),
                TargetAccountId = discovery.ActiveAccountId,
                Warnings = [],
            });
        }

        // Mode 2: Non-Steam shortcut -> hard block Tier 2/3 anti-cheat games.
        if (game.AntiCheatTier >= AntiCheatTier.Tier2)
        {
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.BlockedTierAntiCheat,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                TargetAccountId = discovery.ActiveAccountId,
                ErrorMessage = $"当前游戏为非 Steam 来源且静态反作弊等级为 {game.AntiCheatTier}，为防止 Steam 覆盖层与内核反作弊冲突，已禁止通过非 Steam 快捷方式启动（仅正版 Steam 库游戏支持通过 Steam 启动）。",
            });
        }

        // Validate executable exists.
        if (string.IsNullOrWhiteSpace(game.InstallDir)
            || string.IsNullOrWhiteSpace(game.ExecutableRelPath)
            || !Directory.Exists(game.InstallDir)
            || !File.Exists(game.ExecutableFullPath))
        {
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.BlockedInvalidExecutable,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                TargetAccountId = discovery.ActiveAccountId,
                ErrorMessage = $"找不到有效的游戏主程序可执行文件：{game.ExecutableFullPath}",
            });
        }

        // Resolve target Steam account & shortcuts.vdf path.
        SteamAccountInfo? targetAccount = ResolveTargetAccount(discovery, preferredAccountId);
        if (targetAccount is null || string.IsNullOrWhiteSpace(targetAccount.ShortcutsVdfPath))
        {
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.BlockedNoSteamAccount,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                ErrorMessage = "已检测到 Steam 客户端，但未发现可用的 Steam 账号数据目录（userdata），请先在 Steam 客户端登录一次后重试。",
            });
        }

        List<SteamLaunchWarning> warnings = InspectCompatibilityWarnings(game);
        SteamShortcutDesiredSpec desired = BuildDesiredSpec(game, settings);

        BinaryVdfMap document;
        try
        {
            document = SteamShortcutsVdfSerializer.ReadFile(targetAccount.ShortcutsVdfPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed reading Steam shortcuts.vdf at {Path}.", targetAccount.ShortcutsVdfPath);
            }

            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.BlockedNoSteamAccount,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                TargetAccountId = targetAccount.AccountId,
                ShortcutsVdfPath = targetAccount.ShortcutsVdfPath,
                Warnings = warnings,
                ErrorMessage = $"读取 Steam shortcuts.vdf 失败（已保护原文件未做修改）：{ex.Message}",
            });
        }

        BinaryVdfMap? existing = SteamShortcutsVdfSerializer.FindShortcutEntry(
            document,
            settings.RegisteredShortcutAppId,
            desired.NormalizedExeFullPath);

        if (existing is not null && SteamShortcutsVdfSerializer.IsEntryUpToDate(existing, desired))
        {
            int existingAppId = existing.GetInt32("appid")!.Value;
            return Task.FromResult(new SteamLaunchPreparationResult
            {
                Status = SteamLaunchPreparationStatus.ReadyNonSteamShortcut,
                TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
                LaunchUri = SteamShortcutsVdfSerializer.BuildRunGameIdUri(existingAppId),
                TargetAccountId = targetAccount.AccountId,
                ShortcutsVdfPath = targetAccount.ShortcutsVdfPath,
                ShortcutAppId = existingAppId,
                ShortcutAppName = desired.AppName,
                Warnings = warnings,
            });
        }

        int targetAppId = existing?.GetInt32("appid") is { } existingId && existingId != 0
            ? existingId
            : SteamShortcutsVdfSerializer.ComputeShortcutAppId(desired.QuotedExe, desired.AppName);

        SteamLaunchPreparationStatus nextStatus = discovery.IsSteamRunning
            ? SteamLaunchPreparationStatus.NeedsSteamRestartToSync
            : SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed;

        return Task.FromResult(new SteamLaunchPreparationResult
        {
            Status = nextStatus,
            TargetKind = SteamLaunchTargetKind.NonSteamShortcut,
            LaunchUri = SteamShortcutsVdfSerializer.BuildRunGameIdUri(targetAppId),
            TargetAccountId = targetAccount.AccountId,
            ShortcutsVdfPath = targetAccount.ShortcutsVdfPath,
            ShortcutAppId = targetAppId,
            ShortcutAppName = desired.AppName,
            Warnings = warnings,
        });
    }

    public async Task<SteamShortcutSyncResult> ApplyShortcutAndRestartIfNeededAsync(
        GameRecord game,
        GameSteamLaunchSettings settings,
        bool allowGracefulSteamRestart,
        string? steamInstallDirOverride = null,
        uint? preferredAccountId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(settings);

        SteamLaunchPreparationResult prep = await PrepareLaunchAsync(
            game,
            settings,
            steamInstallDirOverride,
            preferredAccountId,
            cancellationToken).ConfigureAwait(false);

        if (prep.IsBlocked)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = prep.ErrorMessage ?? "当前游戏无法通过 Steam 启动。",
            };
        }

        if (prep.Status is SteamLaunchPreparationStatus.ReadyOfficialSteam or SteamLaunchPreparationStatus.ReadyNonSteamShortcut)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = true,
                LaunchUri = prep.LaunchUri,
                ShortcutAppId = prep.ShortcutAppId,
                RestartedSteam = false,
            };
        }

        if (string.IsNullOrWhiteSpace(prep.ShortcutsVdfPath))
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = "未能解析目标 Steam shortcuts.vdf 路径。",
            };
        }

        SteamShortcutDesiredSpec desired = BuildDesiredSpec(game, settings);

        if (prep.Status == SteamLaunchPreparationStatus.NeedsShortcutWriteSteamClosed)
        {
            return TryWriteShortcutAtomically(
                prep.ShortcutsVdfPath,
                desired,
                settings.RegisteredShortcutAppId,
                restartedSteam: false);
        }

        // NeedsSteamRestartToSync: Steam is currently running.
        if (!allowGracefulSteamRestart)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                RestartedSteam = false,
                ErrorMessage = "Steam 当前正在运行，写入或更新非 Steam 快捷方式需要优雅重启 Steam 后才能生效；已取消同步且未改动 Steam 或 shortcuts.vdf。",
            };
        }

        SteamClientDiscoveryResult discovery = _steamLocator.Discover(steamInstallDirOverride, preferredAccountId);
        if (!discovery.IsDetected || string.IsNullOrWhiteSpace(discovery.SteamExecutablePath))
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = "未能定位 steam.exe，无法执行优雅重启。",
            };
        }

        string steamExePath = discovery.SteamExecutablePath;

        // 1. Request graceful shutdown (steam.exe -shutdown, NEVER taskkill /f).
        bool shutdownDispatched = await _processController
            .RequestGracefulShutdownAsync(steamExePath, cancellationToken)
            .ConfigureAwait(false);
        if (!shutdownDispatched)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = "向 Steam 发送优雅退出指令（steam.exe -shutdown）失败，已中止写入以防 shortcuts.vdf 被覆盖。",
            };
        }

        // 2. Wait for Steam to exit completely before touching shortcuts.vdf.
        bool exitedCleanly = await WaitUntilAsync(
            () => !_steamLocator.Discover(steamInstallDirOverride, preferredAccountId).IsSteamRunning,
            _options.ShutdownTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!exitedCleanly)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                ErrorMessage = "等待 Steam 优雅退出超时（可能正有游戏运行、云存档同步或下载任务进行中），已中止写入以避免覆盖 shortcuts.vdf。",
            };
        }

        // 3. Steam has flushed and exited -> atomically patch shortcuts.vdf (with .spawn.bak backup).
        SteamShortcutSyncResult writeResult = TryWriteShortcutAtomically(
            prep.ShortcutsVdfPath,
            desired,
            settings.RegisteredShortcutAppId,
            restartedSteam: true);

        if (!writeResult.Succeeded)
        {
            // Still try to bring Steam back up? No, leave clean error so user sees exact write error.
            return writeResult;
        }

        // 4. Relaunch Steam client cleanly.
        bool started = await _processController
            .StartSteamAsync(steamExePath, cancellationToken)
            .ConfigureAwait(false);

        if (!started)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                LaunchUri = writeResult.LaunchUri,
                ShortcutAppId = writeResult.ShortcutAppId,
                RestartedSteam = false,
                ErrorMessage = "已成功写入 shortcuts.vdf，但重新启动 steam.exe 失败，请手动打开 Steam 后再试。",
            };
        }

        // 5. Wait for Steam client login / ActiveUser readiness (ActiveUser != 0).
        bool ready = await WaitUntilAsync(
            () =>
            {
                SteamClientDiscoveryResult probe = _steamLocator.Discover(steamInstallDirOverride, preferredAccountId);
                return probe.IsSteamRunning
                       && probe.Accounts.Any(static a => a.IsActiveProcessUser && a.AccountId > 0);
            },
            _options.StartupLoginTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!ready)
        {
            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                LaunchUri = writeResult.LaunchUri,
                ShortcutAppId = writeResult.ShortcutAppId,
                RestartedSteam = true,
                ErrorMessage = "已写入 shortcuts.vdf 并重新启动 Steam，但在等待期内未检测到 Steam 完成账号登录（可能正在更新或等待令牌验证）；请在 Steam 登录完成后再次点击启动。",
            };
        }

        return writeResult;
    }

    private SteamShortcutSyncResult TryWriteShortcutAtomically(
        string shortcutsVdfPath,
        SteamShortcutDesiredSpec desired,
        int? registeredShortcutAppId,
        bool restartedSteam)
    {
        try
        {
            BinaryVdfMap doc = SteamShortcutsVdfSerializer.ReadFile(shortcutsVdfPath);
            (int appId, bool _) = SteamShortcutsVdfSerializer.UpsertShortcutEntry(doc, desired, registeredShortcutAppId);
            SteamShortcutsVdfSerializer.WriteFileAtomically(shortcutsVdfPath, doc);

            return new SteamShortcutSyncResult
            {
                Succeeded = true,
                LaunchUri = SteamShortcutsVdfSerializer.BuildRunGameIdUri(appId),
                ShortcutAppId = appId,
                RestartedSteam = restartedSteam,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Failed writing Steam shortcuts.vdf at {Path}.", shortcutsVdfPath);
            }

            return new SteamShortcutSyncResult
            {
                Succeeded = false,
                RestartedSteam = restartedSteam,
                ErrorMessage = $"写入 Steam shortcuts.vdf 失败：{ex.Message}",
            };
        }
    }

    private static SteamAccountInfo? ResolveTargetAccount(SteamClientDiscoveryResult discovery, uint? preferredAccountId)
    {
        if (discovery.Accounts.Count == 0)
        {
            return null;
        }

        if (preferredAccountId is > 0)
        {
            SteamAccountInfo? preferred = discovery.Accounts.FirstOrDefault(a => a.AccountId == preferredAccountId.Value);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        if (discovery.ActiveAccountId is > 0)
        {
            SteamAccountInfo? active = discovery.Accounts.FirstOrDefault(a => a.AccountId == discovery.ActiveAccountId.Value);
            if (active is not null)
            {
                return active;
            }
        }

        return discovery.Accounts[0];
    }

    private static SteamShortcutDesiredSpec BuildDesiredSpec(GameRecord game, GameSteamLaunchSettings settings)
    {
        string normalizedExe = Path.GetFullPath(game.ExecutableFullPath);
        string normalizedStartDir = Path.TrimEndingDirectorySeparator(
            Path.GetDirectoryName(normalizedExe) ?? Path.GetFullPath(game.InstallDir));

        string appName = settings.UseSteamAppIdAsShortcutName && game.SteamAppId is > 0
            ? game.SteamAppId.Value.ToString(CultureInfo.InvariantCulture)
            : (string.IsNullOrWhiteSpace(game.Title) ? Path.GetFileNameWithoutExtension(normalizedExe) : game.Title.Trim());

        string launchOptions = (game.LaunchArguments ?? string.Empty).Trim();

        return new SteamShortcutDesiredSpec(
            AppName: appName,
            NormalizedExeFullPath: normalizedExe,
            QuotedExe: SteamShortcutsVdfSerializer.QuoteShortcutPath(normalizedExe),
            NormalizedStartDir: normalizedStartDir,
            QuotedStartDir: SteamShortcutsVdfSerializer.QuoteShortcutPath(normalizedStartDir),
            IconPath: normalizedExe,
            LaunchOptions: launchOptions);
    }

    private static List<SteamLaunchWarning> InspectCompatibilityWarnings(GameRecord game)
    {
        var warnings = new List<SteamLaunchWarning>();

        bool hasGbeFork = game.AchievementCaptureMode == AchievementCaptureMode.GbeForkActivated;
        bool hasOnlineFix = false;
        bool hasGenericOfflineEmu = false;

        ScanEmulatorIndicatorsBounded(game.InstallDir, maxDepth: 4, ref hasGbeFork, ref hasOnlineFix, ref hasGenericOfflineEmu);

        if (hasGbeFork)
        {
            warnings.Add(new SteamLaunchWarning(
                SteamLaunchWarningKind.OfflineEmulatorGbeFork,
                "检测到游戏包含 Goldberg / gbe_fork 离线兼容层：经由 Steam 启动时可能出现手柄双输入或环境变量覆盖 AppID 的情况，建议确认 steam_settings 内的 AppID 配置并在 Steam 控制器设置中测试按键响应。"));
        }

        if (hasOnlineFix)
        {
            warnings.Add(new SteamLaunchWarning(
                SteamLaunchWarningKind.OfflineEmulatorOnlineFix,
                "检测到 OnlineFix.ini（通常借用 AppID 480 Spacewar）：经由 Steam 非 Steam 快捷方式启动可能与 Steam 客户端或 Steam Input 配置产生冲突。"));
        }

        if (hasGenericOfflineEmu && !hasGbeFork && !hasOnlineFix)
        {
            warnings.Add(new SteamLaunchWarning(
                SteamLaunchWarningKind.OfflineEmulatorGeneric,
                "检测到游戏目录包含离线 Steam 模拟器配置（如 RUNE / CODEX / TENOKE / FLT 等）：多数情况下可正常使用 Steam Input，但少数依赖独立环境的游戏可能出现覆盖层或存档路径冲突。"));
        }

        if (PeElevationInspector.RequiresElevation(game.ExecutableFullPath))
        {
            warnings.Add(new SteamLaunchWarning(
                SteamLaunchWarningKind.ElevatedExecutableUipi,
                "检测到该游戏主程序声明了管理员权限（requireAdministrator / highestAvailable）：普通权限的 Steam 客户端受 Windows UIPI 隔离限制，可能无法向高权限游戏注入覆盖层或手柄映射（SPAWN 不会自动提权 Steam）。"));
        }

        return warnings;
    }

    private static void ScanEmulatorIndicatorsBounded(
        string installDir,
        int maxDepth,
        ref bool hasGbeFork,
        ref bool hasOnlineFix,
        ref bool hasGenericOfflineEmu)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return;
        }

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((installDir, 0));

        var enumOpts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        while (queue.Count > 0)
        {
            (string currentDir, int depth) = queue.Dequeue();
            string dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(currentDir));
            if (string.Equals(dirName, "steam_settings", StringComparison.OrdinalIgnoreCase))
            {
                hasGbeFork = true;
            }

            try
            {
                foreach (string filePath in Directory.EnumerateFiles(currentDir, "*", enumOpts))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (string.Equals(fileName, "OnlineFix.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        hasOnlineFix = true;
                    }
                    else if (string.Equals(fileName, "configs.user.ini", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(fileName, "configs.app.ini", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(fileName, "local_save.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGbeFork = true;
                    }
                    else if (fileName.EndsWith(".cdx", StringComparison.OrdinalIgnoreCase)
                             || fileName.EndsWith(".rne", StringComparison.OrdinalIgnoreCase)
                             || GenericOfflineEmulatorFiles.Any(f => string.Equals(f, fileName, StringComparison.OrdinalIgnoreCase)))
                    {
                        hasGenericOfflineEmu = true;
                    }
                }

                if (depth < maxDepth)
                {
                    foreach (string subDir in Directory.EnumerateDirectories(currentDir, "*", enumOpts))
                    {
                        string subName = Path.GetFileName(Path.TrimEndingDirectorySeparator(subDir));
                        if (!SkippedInspectionFolders.Contains(subName))
                        {
                            queue.Enqueue((subDir, depth + 1));
                        }
                    }
                }
            }
            catch
            {
                // Ignore inaccessible subdirectories during best-effort warning scan.
            }
        }
    }

    private async Task<bool> WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (predicate())
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        DateTimeOffset deadline = _timeProvider.GetUtcNow() + timeout;
        TimeSpan step = _options.PollInterval > TimeSpan.Zero
            ? _options.PollInterval
            : TimeSpan.FromMilliseconds(50);

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return predicate();
            }

            TimeSpan sleep = remaining < step ? remaining : step;
            await DelayWithTimeProviderAsync(sleep, cancellationToken).ConfigureAwait(false);

            if (predicate())
            {
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private async Task DelayWithTimeProviderAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (ReferenceEquals(_timeProvider, TimeProvider.System))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ITimer timer = _timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            tcs,
            delay,
            Timeout.InfiniteTimeSpan);
        await using CancellationTokenRegistration reg = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            tcs);
        await tcs.Task.ConfigureAwait(false);
    }
}
