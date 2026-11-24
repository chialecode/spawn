using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Modules.Progress.Runtime;

namespace Spawn.Modules.Progress.Security;

/// <summary>
/// Opt-in Windows Defender single-folder sandbox exclusion assistant and Smart App Control diagnostic service
/// implementing <see cref="IDefenderExclusionAssistant"/> (<c>REQ-PRG-04</c>, <c>M3-A-15</c>).
/// <list type="bullet">
///   <item>Strictly restricts exclusions to SPAWN's trainer sandbox root (<c>vault\Sandbox\Trainers</c> or configured pure-ASCII root);</item>
///   <item>Pre-reads <c>TPExclusions</c>, Defender passive mode / third-party AV, Smart App Control, and current <c>ExclusionPath</c>;</item>
///   <item>Delegates single Level 2 UAC WMI <c>MSFT_MpPreference.Add</c> (zero <c>powershell.exe</c>) to <see cref="IElevatedDefenderExecutor"/> with mandatory readback verification;</item>
///   <item>Never disables Real-Time Protection, Tamper Protection, or Smart App Control.</item>
/// </list>
/// </summary>
public sealed class DefenderExclusionAssistant : IDefenderExclusionAssistant
{
    public const string DefenderExclusionsSettingsUri = "windowsdefender://exclusions";
    public const string DefenderSettingsFallbackUri = "ms-settings:windowsdefender";
    public const string SmartAppControlLearnMoreUrl =
        "https://support.microsoft.com/windows/what-is-smart-app-control-285ea03d-fa88-4d56-882e-6698afdb7003";

    private readonly SpawnPaths _paths;
    private readonly SpawnDatabase _database;
    private readonly IDefenderEnvironmentProbe _probe;
    private readonly IElevatedDefenderExecutor _elevatedExecutor;
    private readonly ILogger<DefenderExclusionAssistant> _logger;

    public DefenderExclusionAssistant(
        SpawnPaths paths,
        SpawnDatabase database,
        IDefenderEnvironmentProbe? probe = null,
        IElevatedDefenderExecutor? elevatedExecutor = null,
        ILogger<DefenderExclusionAssistant>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _probe = probe ?? WindowsDefenderEnvironmentProbe.Instance;
        _elevatedExecutor = elevatedExecutor ?? new WorkerElevatedDefenderExecutor(paths);
        _logger = logger ?? NullLogger<DefenderExclusionAssistant>.Instance;
    }

    public async Task<DefenderExclusionStatusReport> InspectStatusAsync(
        string sandboxPath,
        CancellationToken ct = default)
    {
        var validation = await ValidateAllowedSandboxTargetAsync(sandboxPath, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.Failed,
                SandboxPath = sandboxPath ?? string.Empty,
                IsExcluded = false,
                Message = validation.RejectReason!,
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        DefenderEnvironmentProbeSnapshot snap = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        return BuildInspectionReport(validation.NormalizedPath, snap);
    }

    public async Task<DefenderExclusionStatusReport> RequestExclusionAsync(
        string sandboxPath,
        CancellationToken ct = default)
    {
        var validation = await ValidateAllowedSandboxTargetAsync(sandboxPath, ct).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.Failed,
                SandboxPath = sandboxPath ?? string.Empty,
                IsExcluded = false,
                Message = validation.RejectReason!,
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        string normalizedSandbox = validation.NormalizedPath;

        // 1. Pre-read environment BEFORE prompting UAC (prevents unnecessary UAC prompts on blocked/already-excluded systems).
        DefenderEnvironmentProbeSnapshot preSnap = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        bool isSacEnforced = preSnap.SmartAppControlState == SmartAppControlMode.Enforced;
        bool isTamperOn = preSnap.IsTamperProtectionEnabled == true || preSnap.TamperProtectionExclusionsProtected == true;

        if (ContainsPath(preSnap.CurrentExclusionPaths, normalizedSandbox))
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.AlreadyExcluded,
                SandboxPath = normalizedSandbox,
                IsExcluded = true,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = preSnap.IsThirdPartyPassiveMode,
                IsSmartAppControlEnforced = isSacEnforced,
                Message = isSacEnforced
                    ? "沙箱目录已在 Microsoft Defender 排除列表中。但本机已启用 Windows 11 智能应用控制（Smart App Control），Defender 排除项无法绕过 SAC 对未签名修改器的拦截。"
                    : "修改器沙箱目录已在 Microsoft Defender 排除列表中，无需重复配置。",
                GuidanceUrl = isSacEnforced ? SmartAppControlLearnMoreUrl : null,
            };
        }

        if (preSnap.IsThirdPartyPassiveMode || !preSnap.IsDefenderServiceAvailable)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = true,
                IsSmartAppControlEnforced = isSacEnforced,
                Message = "检测到本机由第三方安全软件接管实时防护（Windows Defender 处于被动模式或未启用）。请复制下方沙箱路径，在您当前使用的杀毒软件中单独为该目录添加信任。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        if (isSacEnforced)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.SmartAppControlNotice,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = true,
                Message = "检测到 Windows 11 智能应用控制（Smart App Control）处于强制模式。根据微软安全机制，Defender 文件夹排除项无法绕过 Smart App Control，SPAWN 也绝不会尝试通过程序关闭该系统保护；请参考微软官方说明了解其行为边界。",
                GuidanceUrl = SmartAppControlLearnMoreUrl,
            };
        }

        if (preSnap.TamperProtectionExclusionsProtected == true)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.TamperProtectionBlocked,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = true,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = false,
                Message = "检测到 Windows Defender 篡改保护已锁定排除项修改（TPExclusions=1），程序化写入将被系统拦截。请点击「复制沙箱路径」并打开 Windows 安全中心排除项页面手动粘贴添加该单一目录。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        // Ensure the sandbox directory exists before asking Defender WMI to exclude it.
        try
        {
            Directory.CreateDirectory(normalizedSandbox);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create trainer sandbox directory {SandboxPath} before Defender exclusion.", normalizedSandbox);
        }

        // 2. Execute single Level 2 UAC WMI MSFT_MpPreference.Add via W05 IElevatedDefenderExecutor (zero powershell.exe).
        TrainerAddDefenderExclusionResponse execResult = await _elevatedExecutor
            .AddSandboxExclusionAsync(normalizedSandbox, ct)
            .ConfigureAwait(false);

        if (execResult.Status == DefenderExclusionStatus.UacCancelledByUser)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.UacCancelledByUser,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = false,
                Message = "您已取消管理员提权（UAC），未对 Windows Defender 进行任何修改。如需配置，可随时重新点击或手动添加沙箱路径。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        if (execResult.Status == DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = true,
                IsSmartAppControlEnforced = false,
                Message = "Windows Defender WMI 服务当前处于被动或不可用状态（可能由第三方杀软接管）。请复制沙箱路径并在第三方安全软件中手动添加单目录排除。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        if (execResult.Status == DefenderExclusionStatus.TamperProtectionBlocked)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.TamperProtectionBlocked,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = true,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = false,
                Message = "Windows Defender 篡改保护拦截了排除项写入（可能静默丢弃 / Event 5013）。请复制沙箱路径并点击打开 Windows 安全中心排除项页面手动添加。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        // 3. Mandatory readback verification (both Worker elevated readback and Host re-probe when readable).
        if (!execResult.Succeeded || !execResult.VerifiedByReadback)
        {
            bool looksLikeSilentTamperDrop = execResult.WmiReturnValue == 0 || isTamperOn;
            return new DefenderExclusionStatusReport
            {
                Status = looksLikeSilentTamperDrop
                    ? DefenderExclusionStatus.TamperProtectionBlocked
                    : DefenderExclusionStatus.VerificationReadbackFailed,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn || looksLikeSilentTamperDrop,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = false,
                Message = looksLikeSilentTamperDrop
                    ? "已调用 WMI 添加排除项，但强制回读未发现该沙箱目录（疑似被篡改保护静默拦截 / Event 5013）。请复制沙箱路径并在 Windows 安全中心手动添加。"
                    : $"Defender 排除项回读校验未通过：{execResult.ErrorMessage ?? "未知原因"}。请复制沙箱路径并手动添加。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        DefenderEnvironmentProbeSnapshot postSnap = await _probe.ProbeAsync(ct).ConfigureAwait(false);
        if (postSnap.CanReadExclusionPaths && !ContainsPath(postSnap.CurrentExclusionPaths, normalizedSandbox))
        {
            bool tamperActive = postSnap.IsTamperProtectionEnabled == true || postSnap.TamperProtectionExclusionsProtected == true;
            return new DefenderExclusionStatusReport
            {
                Status = tamperActive
                    ? DefenderExclusionStatus.TamperProtectionBlocked
                    : DefenderExclusionStatus.VerificationReadbackFailed,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = tamperActive,
                IsDefenderPassiveMode = postSnap.IsThirdPartyPassiveMode,
                IsSmartAppControlEnforced = postSnap.SmartAppControlState == SmartAppControlMode.Enforced,
                Message = tamperActive
                    ? "强制回读 Defender ExclusionPath 未找到沙箱路径，已被篡改保护拒绝。请复制沙箱路径并在 Windows 安全中心手动添加。"
                    : "强制回读 Defender ExclusionPath 未找到沙箱路径，未能确认排除生效。请复制沙箱路径并在 Windows 安全中心手动核对。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        return new DefenderExclusionStatusReport
        {
            Status = DefenderExclusionStatus.AddedAndVerified,
            SandboxPath = normalizedSandbox,
            IsExcluded = true,
            IsTamperProtectionEnabled = postSnap.IsTamperProtectionEnabled == true,
            IsDefenderPassiveMode = false,
            IsSmartAppControlEnforced = postSnap.SmartAppControlState == SmartAppControlMode.Enforced,
            Message = $"已通过 WMI 将单一沙箱目录添加到 Windows Defender 排除列表并完成回读验证：{normalizedSandbox}",
            GuidanceUrl = null,
        };
    }

    /// <summary>
    /// Validates a user-chosen custom pure-ASCII sandbox root path before saving or migrating trainers.
    /// Rejects non-ASCII characters, relative/traversal paths, drive roots, broad system/user roots,
    /// and any known game installation directory.
    /// </summary>
    public static bool TryValidateCustomAsciiSandboxRoot(
        string? rawPath,
        SpawnPaths paths,
        IEnumerable<string>? knownGameDirectories,
        out string normalizedPath,
        out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(paths);
        normalizedPath = string.Empty;
        rejectReason = null;

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            rejectReason = "自定义沙箱根目录不能为空。";
            return false;
        }

        string trimmed = rawPath.Trim().Trim('"');
        if (!IsPureAsciiPath(trimmed))
        {
            rejectReason = "自定义沙箱根目录必须仅包含纯 ASCII 字符（例如 D:\\SpawnSandbox\\Trainers），以兼容旧版 ANSI 修改器。";
            return false;
        }

        if (ContainsPathTraversalSegment(trimmed))
        {
            rejectReason = "沙箱路径禁止包含相对跳转段（..）。";
            return false;
        }

        if (!Path.IsPathFullyQualified(trimmed) || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            rejectReason = "沙箱路径必须为本机绝对目录路径（不支持网络共享路径）。";
            return false;
        }

        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejectReason = $"沙箱路径格式无效：{ex.Message}";
            return false;
        }

        if (IsForbiddenBroadRoot(normalizedPath, paths, out string? broadReason))
        {
            rejectReason = broadReason;
            return false;
        }

        if (knownGameDirectories is not null)
        {
            foreach (string gameDir in knownGameDirectories)
            {
                if (string.IsNullOrWhiteSpace(gameDir))
                {
                    continue;
                }

                if (PathsOverlap(normalizedPath, gameDir))
                {
                    rejectReason = $"严禁将游戏安装目录或其父/子目录设为修改器沙箱或排除目标：{normalizedPath}";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Safely moves existing trainer subdirectories (<c>&lt;GameId&gt;\&lt;TrainerId&gt;</c>) when the user changes
    /// the custom sandbox root, keeping <c>sandbox_relative_path</c> stable across migration.
    /// </summary>
    /// <remarks>
    /// Data-safety contract: a user-chosen destination must be empty (ignoring <c>.staging</c>) so that SPAWN only ever
    /// manages content it created there; every source subtree is copied without overwriting and its old copy is removed
    /// only after the copy has been verified (same regular file count and total bytes, zero reparse points). Anything that
    /// fails verification stays in place untouched.
    /// </remarks>
    public static Task<(bool Succeeded, int MovedTrainerCount, string Message)> MigrateSandboxContentsAsync(
        string previousSandboxRoot,
        string newSandboxRoot,
        bool destinationIsSpawnOwnedDefaultRoot = false,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            string srcRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(previousSandboxRoot));
            string dstRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newSandboxRoot));

            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
            {
                return (true, 0, "沙箱目录未发生变化。");
            }

            if (PathsOverlap(srcRoot, dstRoot))
            {
                return (false, 0, "新旧沙箱目录存在父子包含关系，请先选择一个独立的纯 ASCII 目录。");
            }

            if (!destinationIsSpawnOwnedDefaultRoot
                && Directory.Exists(dstRoot)
                && Directory.EnumerateFileSystemEntries(dstRoot).Any(static e => !IsStagingEntry(e)))
            {
                return (false, 0, $"新沙箱目录必须为空目录（SPAWN 将独占管理该目录），请新建或选择一个空的纯 ASCII 目录：{dstRoot}");
            }

            Directory.CreateDirectory(dstRoot);
            if (!Directory.Exists(srcRoot))
            {
                return (true, 0, $"已切换修改器沙箱根目录至：{dstRoot}");
            }

            int movedCount = 0;
            var keptInPlace = new List<string>();
            foreach (string subDir in Directory.EnumerateDirectories(srcRoot).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (IsStagingEntry(subDir))
                {
                    continue;
                }

                string dirName = Path.GetFileName(subDir);
                string targetSubDir = Path.Combine(dstRoot, dirName);
                try
                {
                    if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(targetSubDir))
                    {
                        keptInPlace.Add(dirName);
                        continue;
                    }

                    CopyDirectoryNonReparse(subDir, targetSubDir, ct);
                    var (srcFiles, srcBytes, srcHasReparse) = MeasureTree(subDir);
                    var (dstFiles, dstBytes, _) = MeasureTree(targetSubDir);
                    if (srcHasReparse || srcFiles != dstFiles || srcBytes != dstBytes)
                    {
                        keptInPlace.Add(dirName);
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    keptInPlace.Add(dirName);
                    continue;
                }

                movedCount++;
                try
                {
                    // Verified copy: the source subtree contains no reparse points, so recursive delete stays inside it.
                    Directory.Delete(subDir, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup; the verified copy already lives in the new root.
                }
            }

            string message = movedCount > 0
                ? $"已切换修改器沙箱根目录并迁移 {movedCount} 个游戏修改器目录至：{dstRoot}"
                : $"已切换修改器沙箱根目录至：{dstRoot}";
            if (keptInPlace.Count > 0)
            {
                message += $"；{keptInPlace.Count} 个目录未能完整校验迁移，已原样保留在旧位置：{srcRoot}（{string.Join("、", keptInPlace)}）";
            }

            return (true, movedCount, message);
        }, ct);
    }

    private static bool IsStagingEntry(string path) =>
        string.Equals(Path.GetFileName(path), ".staging", StringComparison.OrdinalIgnoreCase);

    private static (long FileCount, long TotalBytes, bool HasReparsePoint) MeasureTree(string root)
    {
        long files = 0;
        long bytes = 0;
        bool hasReparse = false;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            DirectoryInfo dir = pending.Pop();
            foreach (FileSystemInfo entry in dir.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    hasReparse = true;
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    pending.Push(sub);
                }
                else if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                }
            }
        }

        return (files, bytes, hasReparse);
    }

    public static bool IsPureAsciiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (char ch in path)
        {
            if (ch < 0x20 || ch > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<(bool IsValid, string NormalizedPath, string? RejectReason)> ValidateAllowedSandboxTargetAsync(
        string? rawPath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return (false, string.Empty, "拒绝排除：沙箱路径不能为空。");
        }

        string trimmed = rawPath.Trim().Trim('"');
        if (ContainsPathTraversalSegment(trimmed))
        {
            return (false, string.Empty, "拒绝排除：路径包含非法的相对跳转段（..）。");
        }

        if (!Path.IsPathFullyQualified(trimmed) || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (false, string.Empty, "拒绝排除：仅允许本机绝对沙箱目录路径。");
        }

        string normalizedCandidate;
        try
        {
            normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, string.Empty, $"拒绝排除：路径格式无效（{ex.Message}）。");
        }

        if (IsForbiddenBroadRoot(normalizedCandidate, _paths, out string? broadReason))
        {
            return (false, string.Empty, $"拒绝排除：{broadReason}");
        }

        var games = await _database.GetAllGamesAsync(ct).ConfigureAwait(false);
        var gameDirs = games
            .Select(static g => g.InstallDir)
            .Where(static d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        foreach (string gameDir in gameDirs)
        {
            if (PathsOverlap(normalizedCandidate, gameDir))
            {
                return (false, string.Empty, "安全硬约束：严禁将游戏安装目录加入杀软排除列表，仅允许排除 SPAWN 修改器沙箱根目录。");
            }
        }

        string defaultSandbox = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.TrainerSandboxDirectory));
        if (string.Equals(normalizedCandidate, defaultSandbox, StringComparison.OrdinalIgnoreCase))
        {
            return (true, normalizedCandidate, null);
        }

        TrainerSettingsDto settings = await _database.GetTrainerSettingsAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(settings.CustomSandboxRoot)
            && TryValidateCustomAsciiSandboxRoot(settings.CustomSandboxRoot, _paths, gameDirs, out string validCustomRoot, out _)
            && string.Equals(normalizedCandidate, validCustomRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (true, normalizedCandidate, null);
        }

        return (false, string.Empty,
            $"安全硬约束：Defender 排除助手仅允许操作当前修改器沙箱根目录（{defaultSandbox} 或已配置的纯 ASCII 自定义沙箱根），已拒绝范围外路径：{normalizedCandidate}");
    }

    private static DefenderExclusionStatusReport BuildInspectionReport(
        string normalizedSandbox,
        DefenderEnvironmentProbeSnapshot snap)
    {
        bool isSacEnforced = snap.SmartAppControlState == SmartAppControlMode.Enforced;
        bool isTamperOn = snap.IsTamperProtectionEnabled == true || snap.TamperProtectionExclusionsProtected == true;

        if (ContainsPath(snap.CurrentExclusionPaths, normalizedSandbox))
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.AlreadyExcluded,
                SandboxPath = normalizedSandbox,
                IsExcluded = true,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = snap.IsThirdPartyPassiveMode,
                IsSmartAppControlEnforced = isSacEnforced,
                Message = isSacEnforced
                    ? "修改器沙箱目录已在 Defender 排除列表中；但本机 Windows 11 Smart App Control 处于强制模式，Defender 排除无法绕过 SAC 拦截。"
                    : "修改器沙箱目录已在 Microsoft Defender 排除列表中。",
                GuidanceUrl = isSacEnforced ? SmartAppControlLearnMoreUrl : null,
            };
        }

        if (snap.IsThirdPartyPassiveMode || !snap.IsDefenderServiceAvailable)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = true,
                IsSmartAppControlEnforced = isSacEnforced,
                Message = "检测到本机由第三方安全软件接管防护（Windows Defender 处于被动模式或未启用）。如需避免误报，请复制沙箱路径并在第三方杀软中单独放行该目录。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        if (isSacEnforced)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.SmartAppControlNotice,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = isTamperOn,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = true,
                Message = "检测到 Windows 11 Smart App Control（智能应用控制）处于强制模式。Defender 排除项无法绕过 SAC 对无签名外部修改器的拦截，SPAWN 绝不会尝试编程关闭 SAC。",
                GuidanceUrl = SmartAppControlLearnMoreUrl,
            };
        }

        if (snap.TamperProtectionExclusionsProtected == true)
        {
            return new DefenderExclusionStatusReport
            {
                Status = DefenderExclusionStatus.TamperProtectionBlocked,
                SandboxPath = normalizedSandbox,
                IsExcluded = false,
                IsTamperProtectionEnabled = true,
                IsDefenderPassiveMode = false,
                IsSmartAppControlEnforced = false,
                Message = "检测到 Windows Defender 篡改保护锁定了排除项（TPExclusions=1），无法通过自动提权写入。请复制沙箱路径后打开 Windows 安全中心手动添加。",
                GuidanceUrl = DefenderExclusionsSettingsUri,
            };
        }

        string statusHint = snap.CanReadExclusionPaths
            ? "当前沙箱目录尚未加入 Microsoft Defender 排除列表。若下载或运行单机修改器时遇到误报隔离，可按需一键仅排除此单一沙箱目录（需 1 次 UAC）。"
            : "标准用户权限下 Windows 会隐藏现有 Defender 排除项列表；如遇修改器被隔离，可按需点击下方按钮触发单次 UAC 添加并回读验证单一沙箱目录。";

        return new DefenderExclusionStatusReport
        {
            Status = DefenderExclusionStatus.Unknown,
            SandboxPath = normalizedSandbox,
            IsExcluded = false,
            IsTamperProtectionEnabled = isTamperOn,
            IsDefenderPassiveMode = false,
            IsSmartAppControlEnforced = false,
            Message = statusHint,
            GuidanceUrl = DefenderExclusionsSettingsUri,
        };
    }

    private static bool IsForbiddenBroadRoot(string normalizedPath, SpawnPaths paths, out string? reason)
    {
        reason = null;

        string? root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(Path.TrimEndingDirectorySeparator(root), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = "严禁将整个磁盘盘符根目录设为沙箱或加入杀软排除。";
            return true;
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] forbiddenExact =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            userProfile,
            string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.Combine(userProfile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),
            paths.DataRoot,
            paths.VaultDirectory,
        ];

        foreach (string item in forbiddenExact)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string normForbidden = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item));
            if (string.Equals(normalizedPath, normForbidden, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"严禁将系统目录、下载目录或宽泛用户目录设为沙箱或排除目标：{normalizedPath}";
                return true;
            }
        }

        // Also disallow placing a custom sandbox directly inside %USERPROFILE%\Downloads or %SystemRoot%.
        string[] forbiddenAncestors =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            string.IsNullOrWhiteSpace(userProfile) ? string.Empty : Path.Combine(userProfile, "Downloads"),
        ];

        foreach (string ancestor in forbiddenAncestors)
        {
            if (string.IsNullOrWhiteSpace(ancestor))
            {
                continue;
            }

            string normAncestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor)) + Path.DirectorySeparatorChar;
            if (normalizedPath.StartsWith(normAncestor, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"严禁将 Windows 系统目录或 Downloads 目录及其子目录加入杀软排除：{normalizedPath}";
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPathTraversalSegment(string rawPath)
    {
        string[] segments = rawPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static s => s == "..");
    }

    private static bool PathsOverlap(string pathA, string pathB)
    {
        try
        {
            string normA = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathA));
            string normB = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathB));
            if (string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normA.StartsWith(normB + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normB.StartsWith(normA + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsPath(IReadOnlyList<string> exclusionPaths, string expectedNormalizedPath)
    {
        foreach (string entry in exclusionPaths)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            try
            {
                string norm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Trim()));
                if (string.Equals(norm, expectedNormalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Skip malformed system entry.
            }
        }

        return false;
    }

    private static void CopyDirectoryNonReparse(string sourceDir, string destDir, CancellationToken ct)
    {
        var dirInfo = new DirectoryInfo(sourceDir);
        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return;
        }

        Directory.CreateDirectory(destDir);
        foreach (FileInfo file in dirInfo.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            file.CopyTo(Path.Combine(destDir, file.Name), overwrite: false);
        }

        foreach (DirectoryInfo sub in dirInfo.EnumerateDirectories())
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryNonReparse(sub.FullName, Path.Combine(destDir, sub.Name), ct);
        }
    }
}
