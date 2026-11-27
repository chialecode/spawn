using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Host.ViewModels.Saves;
using Spawn.Modules.Progress.Catalog;
using Spawn.Modules.Progress.Security;

namespace Spawn.Host.ViewModels;

public sealed record NamedOption<T>(T Value, string Label);

public sealed record ScanDirectoryEntry(string Path, string SourceNote, bool IsRemovable);

public enum UiThemePreferenceMode
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public sealed record WindowPlacementDto(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized);

public sealed record ManualImportOutcome(
    bool Succeeded,
    string? GameId = null,
    string? GameTitle = null,
    int ImportedCount = 0);

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    public const string SettingKeyCloseToTray = "ui.close_to_tray";
    public const string SettingKeyOverlayEnabled = "overlay.enabled";
    public const string SettingKeyStartWithWindowsTray = "ui.start_with_windows_tray";
    public const string SettingKeyFirstMinimizeToTrayNotified = "ui.tray_first_minimize_notified";
    public const string SettingKeyWindowPlacement = "ui.window_placement";
    public const string SteamApiKeyRegistrationUrl = "https://steamcommunity.com/dev/apikey";

    private const string WindowsRunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsPersonalizeSubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SteamApiKeyHexRegex();

    private readonly SpawnPaths _paths;
    private readonly SpawnBuildMetadataSnapshot _buildSnapshot;
    private readonly SpawnDatabase _db;
    private readonly ISteamApiKeyProvider _steamApiKeyProvider;
    private readonly ISteamClientLocator _steamClientLocator;
    private readonly IOverlayIpcClient _overlayIpcClient;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly ThemeManagerService _themeManager;
    private readonly IDefenderExclusionAssistant? _defenderAssistant;
    private readonly ITrainerAudioController? _trainerAudioController;
    private readonly ITrainerRuntimeService? _trainerRuntime;
    private readonly ISaveTimeMachineService? _saveService;
    private readonly ICloudSaveSyncService? _cloudSyncService;
    private readonly ILudusaviRuleResolver? _ruleResolver;
    private readonly Action<string> _openExternalUrl;
    private readonly Func<SpawnUiThemeKind> _detectSystemTheme;
    private readonly Action<bool> _applyAutoStart;
    private readonly int _volumeDebounceMs;
    private bool _suppressAutoSave;
    private bool _isInitialized;
    private CancellationTokenSource? _overlayVolumeDebounceCts;
    private CancellationTokenSource? _trainerCueVolumeDebounceCts;
    private int _disposed;

    public event Action<string>? StatusFeedbackRequested;
    public event Action<string, bool>? StatusFeedbackWithSeverityRequested;
    public event Func<IReadOnlyList<string>, Task>? ScanDirectoriesRequested;
    public event Func<string, Task>? ManualGameImportRequested;
    public event Func<string, Task<ManualImportOutcome>>? ManualGameImportWithResultRequested;
    public event Action<string>? OpenLastImportedGameRequested;
    public event Action<bool>? GlobalSpoilerPreferenceChanged;
    public event Action? TrainerSafetySettingsChanged;
    public event Action? SteamSettingsChanged;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.GeneralAndLibrary;

    [ObservableProperty]
    private string _manualGameImportPathText = string.Empty;

    [ObservableProperty]
    private string? _lastImportedGameId;

    [ObservableProperty]
    private string? _lastImportedGameTitle;

    [ObservableProperty]
    private int _selectedAfkTimeoutSec = 300;

    // Global Steam Client & Steam Input Launch Settings (SettingsCategory.GeneralAndLibrary)
    [ObservableProperty]
    private string? _appliedSteamInstallDirectoryOverride;

    [ObservableProperty]
    private bool _isSteamClientDetected;

    [ObservableProperty]
    private bool _isSteamClientManualOverride;

    [ObservableProperty]
    private bool _isSteamClientRunning;

    [ObservableProperty]
    private string _steamDetectionStatusBadgeText = "未检测到 Steam";

    [ObservableProperty]
    private string _steamRunningStatusText = "Steam 未运行";

    [ObservableProperty]
    private string _steamRunningStatusToolTip = "当前未检测到正在运行的 steam.exe 进程";

    [ObservableProperty]
    private string _steamResolvedPathText = "未检测到 steam.exe 路径";

    [ObservableProperty]
    private string _steamDetectionDetailText = string.Empty;

    [ObservableProperty]
    private string _steamPathValidationErrorText = string.Empty;

    [ObservableProperty]
    private SteamAccountInfo? _selectedSteamAccount;

    [ObservableProperty]
    private uint? _selectedSteamTargetAccountId;

    [ObservableProperty]
    private bool _steamSuppressDuplicateAchievementToast = true;

    public SteamClientDiscoveryResult? LastSteamDiscovery { get; private set; }

    public ObservableCollection<SteamAccountInfo> SteamAccounts { get; } = [];

    [ObservableProperty]
    private bool _closeWindowToTray = true;

    [ObservableProperty]
    private bool _startWithWindowsMinimizedToTray;

    [ObservableProperty]
    private bool _hasShownFirstMinimizeToTrayNotice;

    public WindowPlacementDto? SavedWindowPlacement { get; private set; }

    [ObservableProperty]
    private bool _isSteamApiKeyConfigured;

    [ObservableProperty]
    private string _steamApiKeyStatusText = "正在检测密钥状态…";

    [ObservableProperty]
    private string _customSteamApiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _showCustomSteamApiKey;

    [ObservableProperty]
    private string _selectedAchievementLanguage = "schinese";

    [ObservableProperty]
    private bool _globalRevealSpoilers;

    [ObservableProperty]
    private bool _overlayEnabled = true;

    [ObservableProperty]
    private OverlayThemeKind _selectedOverlayTheme = OverlayThemeKind.Steam;

    [ObservableProperty]
    private OverlayPositionKind _selectedOverlayPosition = OverlayPositionKind.BottomRight;

    [ObservableProperty]
    private OverlaySoundPreset _selectedOverlaySoundPreset = OverlaySoundPreset.SteamAuthentic;

    [ObservableProperty]
    private string? _customOverlaySoundPath;

    [ObservableProperty]
    private bool _isSteamSoundDetected;

    [ObservableProperty]
    private string _steamSoundStatusText = "正在探测本机 Steam 客户端音效…";

    [ObservableProperty]
    private string _selectedAudioOutputDeviceId = OverlayAudioOutputModes.SystemDefault;

    [ObservableProperty]
    private string _audioRoutingStatusText = "默认跟随 Windows 系统当前输出设备（含显示器外接音响、扬声器或耳机）";

    [ObservableProperty]
    private double _overlayVolumePercent = 75.0;

    [ObservableProperty]
    private bool _overlayMuteSound;

    [ObservableProperty]
    private UiThemePreferenceMode _selectedUiThemeMode = UiThemePreferenceMode.Light;

    [ObservableProperty]
    private SpawnUiThemeKind _selectedUiTheme = SpawnUiThemeKind.Light;

    [ObservableProperty]
    private AntiCheatGuardPhase _antiCheatGuardPhase = AntiCheatGuardPhase.IdleSafe;

    [ObservableProperty]
    private string _antiCheatStatusSummary = "安全就绪 · 主进程与高敏组件物理隔离";

    // Trainer & Security Sandbox Settings (Category 6: SettingsCategory.TrainerAndSandbox)
    [ObservableProperty]
    private string _trainerCustomSandboxRootInput = string.Empty;

    [ObservableProperty]
    private string? _appliedCustomTrainerSandboxRoot;

    [ObservableProperty]
    private TrainerAudioMode _selectedTrainerAudioMode = TrainerAudioMode.FullMute;

    [ObservableProperty]
    private double _trainerCueVolumePercent = 35.0;

    [ObservableProperty]
    private bool _trainerEnableA0PreMute = true;

    [ObservableProperty]
    private int _trainerHotkeyHoldMs = TrainerSettingsDto.DefaultHotkeyHoldMs;

    [ObservableProperty]
    private bool _trainerDefaultScanCodeMode;

    [ObservableProperty]
    private bool _trainerStopWithGameExit = true;

    [ObservableProperty]
    private bool _trainerHideOriginalWindowOnLaunch;

    [ObservableProperty]
    private bool _trainerCreatePreSnapshot = true;

    [ObservableProperty]
    private int _selectedTrainerSnapshotRetentionCount = TrainerSettingsDto.DefaultPreSnapshotRetentionCount;

    [ObservableProperty]
    private int _selectedTrainerSnapshotMaxSizeMb = TrainerSettingsDto.DefaultPreSnapshotMaxSizeMb;

    [ObservableProperty]
    private bool _trainerIncludeFlingArchive;

    [ObservableProperty]
    private bool _trainerStrictVanguardMode;

    [ObservableProperty]
    private DefenderExclusionStatusReport? _defenderStatusReport;

    [ObservableProperty]
    private bool _isDefenderWorking;

    // Save Time Machine & Cloud Sync Settings (Category 8: SettingsCategory.SavesAndCloudSync)
    [ObservableProperty]
    private bool _saveAutoWatchByDefault = true;

    [ObservableProperty]
    private bool _savePostExitSnapshotByDefault = true;

    [ObservableProperty]
    private bool _saveIncludeConfigByDefault;

    [ObservableProperty]
    private int _saveQuiescentDebounceMs = SaveSettingsDto.DefaultQuiescentDebounceMs;

    [ObservableProperty]
    private int _saveDefaultMaxRollingSnapshots = SaveSettingsDto.DefaultRollingRetentionCount;

    [ObservableProperty]
    private int _saveDefaultMaxSnapshotSizeMb = SaveSettingsDto.DefaultSnapshotMaxSizeMb;

    [ObservableProperty]
    private int _ludusaviRuleCount;

    [ObservableProperty]
    private CasVaultStatsDto? _saveVaultStats;

    [ObservableProperty]
    private string _newCloudEndpointName = string.Empty;

    [ObservableProperty]
    private CloudSyncProviderType _newCloudProviderType = CloudSyncProviderType.LocalFolder;

    [ObservableProperty]
    private bool _newCloudEndpointIsDefault = true;

    [ObservableProperty]
    private string _newCloudEndpointUrl = string.Empty;

    [ObservableProperty]
    private string _newCloudBucketOrRoot = string.Empty;

    [ObservableProperty]
    private string _newCloudRegion = "auto";

    [ObservableProperty]
    private string _newCloudUsernameOrAccessKey = string.Empty;

    public ObservableCollection<ScanDirectoryEntry> ScanDirectories { get; } = [];
    public ObservableCollection<CloudEndpointItemViewModel> CloudSyncEndpoints { get; } = [];
    public UpdateViewModel Update { get; }

    public IReadOnlyList<NamedOption<int>> SaveRollingRetentionOptions { get; } =
    [
        new(10, "保留最近 10 份"),
        new(30, "保留最近 30 份（默认）"),
        new(50, "保留最近 50 份"),
        new(100, "保留最近 100 份"),
    ];

    public IReadOnlyList<NamedOption<int>> SaveMaxSnapshotSizeMbOptions { get; } =
    [
        new(256, "256 MB"),
        new(512, "512 MB（默认）"),
        new(1024, "1024 MB"),
        new(2048, "2048 MB"),
    ];

    public IReadOnlyList<NamedOption<CloudSyncProviderType>> CloudProviderTypeOptions { get; } =
    [
        new(CloudSyncProviderType.LocalFolder, "本地同步目录 / 网盘"),
        new(CloudSyncProviderType.WebDav, "WebDAV 服务器"),
        new(CloudSyncProviderType.S3, "S3 / R2 对象存储"),
    ];

    public IReadOnlyList<NamedOption<TrainerAudioMode>> TrainerAudioModeOptions { get; } =
    [
        new(TrainerAudioMode.FullMute, "完全静音（默认推荐）"),
        new(TrainerAudioMode.KeepCuesExperimental, "实验性：尽量保留提示音（仅新版 FLiNG 生效）"),
        new(TrainerAudioMode.Passthrough, "不干预修改器音频"),
    ];

    public IReadOnlyList<NamedOption<string>> AchievementLanguageOptions { get; } =
    [
        new("schinese", "简体中文（默认）"),
        new("tchinese", "繁體中文"),
        new("english", "English"),
        new("japanese", "日本語"),
    ];

    public IReadOnlyList<NamedOption<int>> TrainerSnapshotRetentionOptions { get; } =
    [
        new(3, "保留最近 3 份"),
        new(5, "保留最近 5 份（默认）"),
        new(10, "保留最近 10 份"),
    ];

    public IReadOnlyList<NamedOption<int>> TrainerSnapshotMaxSizeMbOptions { get; } =
    [
        new(128, "128 MB"),
        new(256, "256 MB（默认）"),
        new(512, "512 MB"),
        new(1024, "1024 MB"),
    ];

    public ObservableCollection<NamedOption<string>> AudioOutputDeviceOptions { get; } =
    [
        new(OverlayAudioOutputModes.SystemDefault, "跟随系统默认输出设备"),
    ];

    public IReadOnlyList<NamedOption<int>> AfkTimeoutOptions { get; } =
    [
        new(180, "3 分钟"),
        new(300, "5 分钟（推荐）"),
        new(600, "10 分钟"),
        new(900, "15 分钟"),
    ];

    public IReadOnlyList<NamedOption<OverlayThemeKind>> OverlayThemeOptions { get; } =
    [
        new(OverlayThemeKind.Steam, "Steam 经典"),
        new(OverlayThemeKind.Ps5Trophy, "PS5 奖杯（100% 自动白金连发）"),
        new(OverlayThemeKind.XboxRare, "Xbox 稀有紫钻"),
    ];

    public IReadOnlyList<NamedOption<OverlayPositionKind>> OverlayPositionOptions { get; } =
    [
        new(OverlayPositionKind.BottomRight, "右下角（推荐）"),
        new(OverlayPositionKind.TopRight, "右上角"),
        new(OverlayPositionKind.BottomLeft, "左下角"),
        new(OverlayPositionKind.TopLeft, "左上角"),
        new(OverlayPositionKind.TopCenter, "顶部居中"),
        new(OverlayPositionKind.BottomCenter, "底部居中"),
    ];

    public IReadOnlyList<NamedOption<OverlaySoundPreset>> OverlaySoundPresetOptions { get; } =
    [
        new(OverlaySoundPreset.SteamAuthentic, "Steam 原版同款音效（推荐）"),
        new(OverlaySoundPreset.ThemeSynthesized, "主题合成和弦音效"),
        new(OverlaySoundPreset.CustomWav, "自定义 WAV 音频文件"),
    ];

    public bool ShowCustomWavPicker => SelectedOverlaySoundPreset == OverlaySoundPreset.CustomWav;

    public bool ShowSteamSoundStatus => SelectedOverlaySoundPreset == OverlaySoundPreset.SteamAuthentic;

    public double OverlayVolumeSliderOpacity => OverlayMuteSound ? 0.45 : 1.0;

    public bool HasLastImportedGame => !string.IsNullOrWhiteSpace(LastImportedGameId);

    public string LastImportedGameHintText => HasLastImportedGame
        ? $"已导入《{LastImportedGameTitle}》"
        : string.Empty;

    public bool IsAntiCheatGuardIdle => AntiCheatGuardPhase == AntiCheatGuardPhase.IdleSafe;
    public bool IsAntiCheatGuardWarning => AntiCheatGuardPhase is AntiCheatGuardPhase.ResidualGuardWatch or AntiCheatGuardPhase.CooldownLocked;
    public bool IsAntiCheatGuardLocked => AntiCheatGuardPhase == AntiCheatGuardPhase.ProtectedRunning;

    public bool HasSteamInstallDirectoryOverride => !string.IsNullOrWhiteSpace(AppliedSteamInstallDirectoryOverride);
    public bool HasSteamPathValidationError => !string.IsNullOrWhiteSpace(SteamPathValidationErrorText);
    public bool HasSteamAccounts => SteamAccounts.Count > 0;
    public string SteamDetectionStatusKind => IsSteamClientDetected
        ? (IsSteamClientManualOverride ? "Manual" : "Auto")
        : "NotFound";
    public string SteamAccountsEmptyHintText => IsSteamClientDetected
        ? "未发现本地 Steam 账号目录（请先在 Steam 客户端登录一次后点击「重新检测」）"
        : "检测到有效 Steam 客户端后将自动列出本地账号";

    public string ScanDirectoriesSubtitleText => IsDevOrSourceBuild
        ? "管理持久化多目录扫描源（含开发工作区自动发现目录），自动识别 GOG 离线版与本地单机游戏"
        : "管理持久化多目录扫描源，自动识别 GOG 离线版、Steam 本地与免安装单机游戏";

    public bool ShowDevWorkspaceBridge => IsDevOrSourceBuild;

    public bool HasNonAsciiTrainerSandboxPath => !SpawnPaths.IsPureAsciiPath(EffectiveTrainerSandboxRoot);

    public string NonAsciiTrainerSandboxNoticeText =>
        $"当前沙箱目录（{EffectiveTrainerSandboxRoot}）包含中文或非英文字符，部分旧版修改器可能无法正常加载；建议在下方改设为纯英文目录（如 D:\\SpawnSandbox\\Trainers）。";

    public string CustomWavDisplayStatus =>
        !string.IsNullOrWhiteSpace(CustomOverlaySoundPath) && File.Exists(CustomOverlaySoundPath)
            ? $"已载入自定义音效：{Path.GetFileName(CustomOverlaySoundPath)}（已存入配置目录）"
            : "尚未选择自定义 .wav 音频（未配置时将自动回退主题合成和弦音）";

    public string CurrentCategoryTitle => SelectedCategory switch
    {
        SettingsCategory.AchievementAndSteamApi => "成就与 Steam API",
        SettingsCategory.OverlayAndSound => "弹窗外观与音效",
        SettingsCategory.SavesAndCloudSync => "存档与云同步",
        SettingsCategory.TrainerAndSandbox => "修改器与安全沙箱",
        SettingsCategory.AppearanceTheme => "外观主题",
        SettingsCategory.Updates => "更新",
        SettingsCategory.AboutAndVersion => "关于与版本",
        _ => "常规与游戏库",
    };

    public string SaveQuiescentDebounceDisplayText =>
        string.Create(CultureInfo.InvariantCulture, $"{SaveQuiescentDebounceMs} ms");

    public bool IsNewCloudEndpointRemote => NewCloudProviderType != CloudSyncProviderType.LocalFolder;

    public string SaveLudusaviAndVaultSummaryText => SaveVaultStats is null
        ? string.Create(CultureInfo.InvariantCulture, $"Ludusavi 规则库：{LudusaviRuleCount} 条 · 本地块库：{_paths.VaultChunksDirectory}")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"Ludusavi 规则：{LudusaviRuleCount} 条 · 块数 {SaveVaultStats.TotalChunkCount}（未引用 {SaveVaultStats.UnreferencedChunkCount}）· 实占 {UiFormatters.FormatByteSize(SaveVaultStats.TotalCompressedBytes)}（{UiFormatters.FormatDeduplicationPercent(SaveVaultStats.DeduplicationRatio)}）");

    public string EffectiveTrainerSandboxRoot => !string.IsNullOrWhiteSpace(AppliedCustomTrainerSandboxRoot)
        ? AppliedCustomTrainerSandboxRoot!
        : _paths.TrainerSandboxDirectory;

    public string TrainerSourceProvenanceTitle { get; } = TrainerSourceProvenance.SettingsProvenanceTitle;
    public string TrainerSourceProvenanceDescription { get; } = TrainerSourceProvenance.SettingsProvenanceDescription;
    public string TrainerOverviewProvenanceSummary { get; } = TrainerSourceProvenance.OverviewProvenanceSummaryText;
    public string TrainerOverviewEmptyStateDescription { get; } = TrainerSourceProvenance.OverviewEmptyStateDescriptionText;
    public string TrainerExternalMainDomain { get; } = FlingUrlPolicy.MainHost;
    public string TrainerExternalArchiveDomain { get; } = FlingUrlPolicy.ArchiveHost;

    public bool HasCustomTrainerSandboxRoot => !string.IsNullOrWhiteSpace(AppliedCustomTrainerSandboxRoot);

    public bool ShowTrainerCueVolumeSlider => SelectedTrainerAudioMode == TrainerAudioMode.KeepCuesExperimental;

    public string TrainerCueVolumeDisplayText =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(TrainerCueVolumePercent):0}%");

    public string TrainerHotkeyHoldMsDisplayText =>
        string.Create(CultureInfo.InvariantCulture, $"{TrainerHotkeyHoldMs} ms");

    public string DefenderStatusBadgeText => DefenderStatusReport is null
        ? "未检测"
        : UiFormatters.FormatDefenderStatusBadge(DefenderStatusReport.Status);

    public string DefenderStatusMessage => DefenderStatusReport?.Message
        ?? "仅在您主动点击下方按钮时才会请求单次管理员确认，且只排除单一修改器沙箱目录（绝不排除游戏目录、系统目录或下载目录）。";

    public bool IsDefenderExcluded => DefenderStatusReport?.IsExcluded == true;
    public bool IsDefenderNotExcluded => !IsDefenderExcluded;

    public bool ShowSmartAppControlCard =>
        DefenderStatusReport?.IsSmartAppControlEnforced == true
        || DefenderStatusReport?.Status == DefenderExclusionStatus.SmartAppControlNotice;

    public bool ShowDefenderManualGuidance =>
        DefenderStatusReport is not null
        && !DefenderStatusReport.IsExcluded
        && DefenderStatusReport.Status is DefenderExclusionStatus.TamperProtectionBlocked
            or DefenderExclusionStatus.ThirdPartyAntivirusPassiveMode
            or DefenderExclusionStatus.VerificationReadbackFailed
            or DefenderExclusionStatus.UacCancelledByUser
            or DefenderExclusionStatus.Failed;

    public string BuildProductName => _buildSnapshot.DisplayProductName;
    public bool IsDevOrSourceBuild => _buildSnapshot.IsDevOrSourceChannel;
    public string BuildVersionText => _buildSnapshot.Version;
    public string BuildCommitShaText => _buildSnapshot.CommitSha;
    public string BuildTimeUtcText => _buildSnapshot.BuildTimeUtc;
    public string BuildChannelBadgeText => _buildSnapshot.Channel.ToUpperInvariant();

    public string ChannelDisplayName => _paths.IsDevelopment
        ? $"开发版通道 ({_paths.Identity} · {_buildSnapshot.Channel})"
        : $"正式版通道 ({_paths.Identity})";

    public string InstallDirectoryPath => _buildSnapshot.InstallDirectory;
    public string DataRootPath => _paths.DataRoot;
    public string LogsDirectoryPath => _paths.LogsDirectory;
    public string TrashDirectoryPath => _paths.TrashDir;

    public string WorkspaceBridgeSummaryText => !string.IsNullOrWhiteSpace(_paths.LocalWorkspaceRoot)
        ? _paths.LocalWorkspaceRoot!
        : "未绑定开发工作区（标准独立运行模式）";

    public string OverlayVolumeDisplayText => OverlayMuteSound
        ? "静音 (0%)"
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(OverlayVolumePercent):0}%");

    public SettingsViewModel(
        SpawnPaths paths,
        SpawnDatabase db,
        ISteamApiKeyProvider steamApiKeyProvider,
        IOverlayIpcClient overlayIpcClient,
        IAntiCheatGuardService antiCheatGuard,
        ThemeManagerService themeManager,
        IDefenderExclusionAssistant? defenderAssistant = null,
        ITrainerAudioController? trainerAudioController = null,
        Action<string>? openExternalUrl = null,
        ITrainerRuntimeService? trainerRuntime = null,
        UpdateViewModel? updateViewModel = null,
        ISaveTimeMachineService? saveService = null,
        ICloudSaveSyncService? cloudSyncService = null,
        ILudusaviRuleResolver? ruleResolver = null,
        Func<SpawnUiThemeKind>? detectSystemTheme = null,
        Action<bool>? applyAutoStart = null,
        int volumeDebounceMs = 250,
        ISteamClientLocator? steamClientLocator = null)
    {
        _paths = paths;
        _buildSnapshot = SpawnBuildInfo.GetSnapshot(paths.Identity);
        _db = db;
        _steamApiKeyProvider = steamApiKeyProvider;
        _steamClientLocator = steamClientLocator ?? SteamClientLocator.Default;
        _overlayIpcClient = overlayIpcClient;
        _antiCheatGuard = antiCheatGuard;
        _themeManager = themeManager;
        _defenderAssistant = defenderAssistant;
        _trainerAudioController = trainerAudioController;
        _trainerRuntime = trainerRuntime;
        _saveService = saveService;
        _cloudSyncService = cloudSyncService;
        _ruleResolver = ruleResolver;
        _openExternalUrl = openExternalUrl ?? OpenExternalUriDefault;
        _detectSystemTheme = detectSystemTheme ?? DetectWindowsSystemTheme;
        _applyAutoStart = applyAutoStart ?? ApplyWindowsAutoStartRegistry;
        _volumeDebounceMs = Math.Clamp(volumeDebounceMs, 0, 2000);
        Update = updateViewModel ?? new UpdateViewModel(updateService: null, openExternalUrl: _openExternalUrl);
        Update.StatusFeedbackWithSeverityRequested += EmitStatusFeedback;

        RefreshAntiCheatSummary(_antiCheatGuard.CurrentSnapshot);
        _antiCheatGuard.StateChanged += OnAntiCheatGuardStateChanged;
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
    }

    private void EmitStatusFeedback(string message, bool isError = false)
    {
        StatusFeedbackWithSeverityRequested?.Invoke(message, isError);
        StatusFeedbackRequested?.Invoke(message);
    }

    private void OnAntiCheatGuardStateChanged(object? sender, AntiCheatGuardSnapshot snap)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    RefreshAntiCheatSummary(snap);
                }
            });
            return;
        }

        RefreshAntiCheatSummary(snap);
    }

    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0
            || e.Category != UserPreferenceCategory.General
            || SelectedUiThemeMode != UiThemePreferenceMode.System)
        {
            return;
        }

        void ApplyFollowSystem()
        {
            if (Volatile.Read(ref _disposed) != 0 || SelectedUiThemeMode != UiThemePreferenceMode.System)
            {
                return;
            }

            SpawnUiThemeKind resolved = _detectSystemTheme();
            if (SelectedUiTheme != resolved)
            {
                bool prev = _suppressAutoSave;
                _suppressAutoSave = true;
                try
                {
                    SelectedUiTheme = resolved;
                }
                finally
                {
                    _suppressAutoSave = prev;
                }
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(ApplyFollowSystem);
        }
        else
        {
            ApplyFollowSystem();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _suppressAutoSave = true;
        try
        {
            string? rawUiTheme = await _db.GetSettingAsync(AppSettingKeys.UiTheme, cancellationToken).ConfigureAwait(true);
            if (string.Equals(rawUiTheme, nameof(UiThemePreferenceMode.System), StringComparison.OrdinalIgnoreCase))
            {
                SelectedUiThemeMode = UiThemePreferenceMode.System;
                SelectedUiTheme = _detectSystemTheme();
            }
            else if (Enum.TryParse<SpawnUiThemeKind>(rawUiTheme, ignoreCase: true, out var uiTheme))
            {
                SelectedUiThemeMode = uiTheme == SpawnUiThemeKind.Dark
                    ? UiThemePreferenceMode.Dark
                    : UiThemePreferenceMode.Light;
                SelectedUiTheme = uiTheme;
            }
            else
            {
                SelectedUiThemeMode = UiThemePreferenceMode.Light;
                SelectedUiTheme = SpawnUiThemeKind.Light;
            }

            _themeManager.ApplyTheme(SelectedUiTheme);

            string? rawCloseTray = await _db.GetSettingAsync(SettingKeyCloseToTray, cancellationToken).ConfigureAwait(true);
            CloseWindowToTray = string.IsNullOrWhiteSpace(rawCloseTray) || (!bool.TryParse(rawCloseTray, out bool closeTray) || closeTray);

            string? rawAutoStart = await _db.GetSettingAsync(SettingKeyStartWithWindowsTray, cancellationToken).ConfigureAwait(true);
            StartWithWindowsMinimizedToTray = bool.TryParse(rawAutoStart, out bool autoStart) && autoStart;

            string? rawFirstTrayNotice = await _db.GetSettingAsync(SettingKeyFirstMinimizeToTrayNotified, cancellationToken).ConfigureAwait(true);
            HasShownFirstMinimizeToTrayNotice = bool.TryParse(rawFirstTrayNotice, out bool shownTrayNotice) && shownTrayNotice;

            SavedWindowPlacement = await _db.GetSettingJsonAsync<WindowPlacementDto?>(SettingKeyWindowPlacement, null, cancellationToken).ConfigureAwait(true);

            string? rawAfk = await _db.GetSettingAsync(AppSettingKeys.LauncherAfkTimeoutSec, cancellationToken).ConfigureAwait(true);
            if (int.TryParse(rawAfk, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedAfk) && parsedAfk >= 60)
            {
                SelectedAfkTimeoutSec = parsedAfk;
            }

            AppliedSteamInstallDirectoryOverride = await _db.GetSteamInstallDirectoryOverrideAsync(cancellationToken).ConfigureAwait(true);
            SelectedSteamTargetAccountId = await _db.GetSteamTargetAccountIdAsync(cancellationToken).ConfigureAwait(true);
            SteamSuppressDuplicateAchievementToast = await _db.GetSteamSuppressDuplicateAchievementToastAsync(cancellationToken).ConfigureAwait(true);
            RefreshSteamClientDiscovery(emitFeedback: false);

            string? rawSpoilers = await _db.GetSettingAsync(AppSettingKeys.AchievementRevealSpoilers, cancellationToken).ConfigureAwait(true);
            GlobalRevealSpoilers = bool.TryParse(rawSpoilers, out bool spoilers) && spoilers;

            string? rawLang = await _db.GetSettingAsync(AppSettingKeys.AchievementLanguage, cancellationToken).ConfigureAwait(true);
            SelectedAchievementLanguage = !string.IsNullOrWhiteSpace(rawLang)
                && AchievementLanguageOptions.Any(o => string.Equals(o.Value, rawLang.Trim(), StringComparison.OrdinalIgnoreCase))
                ? rawLang.Trim().ToLowerInvariant()
                : "schinese";

            string? rawOverlayEnabled = await _db.GetSettingAsync(SettingKeyOverlayEnabled, cancellationToken).ConfigureAwait(true);
            OverlayEnabled = string.IsNullOrWhiteSpace(rawOverlayEnabled)
                || !bool.TryParse(rawOverlayEnabled, out bool ovEnabled)
                || ovEnabled;

            OverlaySettingsDto overlaySettings = await _db.GetOverlaySettingsAsync(cancellationToken).ConfigureAwait(true);
            SelectedOverlayTheme = overlaySettings.Theme;
            SelectedOverlayPosition = overlaySettings.Position;
            SelectedOverlaySoundPreset = overlaySettings.SoundPreset;
            CustomOverlaySoundPath = overlaySettings.CustomSoundPath;
            SelectedAudioOutputDeviceId = string.IsNullOrWhiteSpace(overlaySettings.AudioOutputDeviceId)
                || string.Equals(overlaySettings.AudioOutputDeviceId, OverlayAudioOutputModes.AutoSmart, StringComparison.OrdinalIgnoreCase)
                ? OverlayAudioOutputModes.SystemDefault
                : overlaySettings.AudioOutputDeviceId;
            OverlayVolumePercent = Math.Round(overlaySettings.SoundVolume * 100.0);
            OverlayMuteSound = overlaySettings.MuteSound;
            RefreshSteamClientSoundStatus();
            RefreshAudioOutputDevicesCore(notifyFeedback: false);

            TrainerSettingsDto trainerSettings = await _db.GetTrainerSettingsAsync(cancellationToken).ConfigureAwait(true);
            AppliedCustomTrainerSandboxRoot = trainerSettings.CustomSandboxRoot;
            TrainerCustomSandboxRootInput = trainerSettings.CustomSandboxRoot ?? string.Empty;
            SelectedTrainerAudioMode = trainerSettings.AudioMode;
            TrainerCueVolumePercent = Math.Round(trainerSettings.CueVolume * 100.0);
            TrainerEnableA0PreMute = trainerSettings.EnableA0PreMute;
            TrainerHotkeyHoldMs = trainerSettings.HotkeyHoldMs;
            TrainerDefaultScanCodeMode = trainerSettings.DefaultScanCodeMode;
            TrainerStopWithGameExit = trainerSettings.StopWithGameExit;
            TrainerHideOriginalWindowOnLaunch = trainerSettings.HideOriginalWindowOnLaunch;
            TrainerCreatePreSnapshot = trainerSettings.CreatePreTrainerSnapshot;
            SelectedTrainerSnapshotRetentionCount = trainerSettings.PreTrainerSnapshotRetentionCount;
            SelectedTrainerSnapshotMaxSizeMb = trainerSettings.PreTrainerSnapshotMaxSizeMb;
            TrainerIncludeFlingArchive = trainerSettings.IncludeFlingArchiveInSearch;
            TrainerStrictVanguardMode = trainerSettings.StrictVanguardMode;
            OnPropertyChanged(nameof(EffectiveTrainerSandboxRoot));
            OnPropertyChanged(nameof(HasCustomTrainerSandboxRoot));
            OnPropertyChanged(nameof(HasNonAsciiTrainerSandboxPath));
            OnPropertyChanged(nameof(ShowTrainerCueVolumeSlider));
            OnPropertyChanged(nameof(TrainerCueVolumeDisplayText));
            OnPropertyChanged(nameof(TrainerHotkeyHoldMsDisplayText));
            OnPropertyChanged(nameof(ShowSteamSoundStatus));
            OnPropertyChanged(nameof(OverlayVolumeSliderOpacity));

            await ReloadScanDirectoriesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshSteamApiKeyStatusAsync(cancellationToken).ConfigureAwait(true);
            await RefreshSaveAndCloudSettingsAsync(cancellationToken).ConfigureAwait(true);
            await Update.InitializeAsync(cancellationToken).ConfigureAwait(true);
            _isInitialized = true;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public Task SaveWindowPlacementAsync(WindowPlacementDto placement, CancellationToken cancellationToken = default)
    {
        SavedWindowPlacement = placement;
        return _db.SetSettingJsonAsync(SettingKeyWindowPlacement, placement, cancellationToken);
    }

    public Task MarkFirstMinimizeToTrayNotifiedAsync(CancellationToken cancellationToken = default)
    {
        HasShownFirstMinimizeToTrayNotice = true;
        return _db.SetSettingAsync(SettingKeyFirstMinimizeToTrayNotified, "true", cancellationToken);
    }

    public static bool IsValidSteamWebApiKeyFormat(string? key) =>
        !string.IsNullOrWhiteSpace(key) && SteamApiKeyHexRegex().IsMatch(key.Trim());

    public static SpawnUiThemeKind DetectWindowsSystemTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(WindowsPersonalizeSubKey, writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int appsUseLight && appsUseLight == 0)
            {
                return SpawnUiThemeKind.Dark;
            }
        }
        catch
        {
            // Fall back to default Light theme on registry access failure.
        }

        return SpawnUiThemeKind.Light;
    }

    private void ApplyWindowsAutoStartRegistry(bool enabled)
    {
        if (_paths.IsIsolatedOverride || StartupWindowPolicy.IsHeadlessTestEnvironment(SpawnBuildInfo.IsReleaseChannel))
        {
            return;
        }

        string valueName = _paths.IsDevelopment ? "SPAWN-Dev" : "SPAWN";
        try
        {
            using RegistryKey? runKey = Registry.CurrentUser.CreateSubKey(WindowsRunRegistrySubKey, writable: true);
            if (runKey is null)
            {
                return;
            }

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    runKey.SetValue(valueName, $"\"{Path.GetFullPath(exePath)}\" --background", RegistryValueKind.String);
                }
            }
            else
            {
                runKey.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"更新 Windows 开机自启注册表失败：{ex.Message}", isError: true);
        }
    }

    public IReadOnlyList<string> GetAllEffectiveScanDirectories() =>
        ScanDirectories.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(CurrentCategoryTitle));
        if (value == SettingsCategory.GeneralAndLibrary && _isInitialized)
        {
            RefreshSteamClientDiscovery(emitFeedback: false);
        }

        if (value == SettingsCategory.TrainerAndSandbox
            && _defenderAssistant is not null
            && DefenderStatusReport is null
            && !IsDefenderWorking)
        {
            _ = InspectDefenderExclusionCoreAsync(emitFeedback: false);
        }
    }

    partial void OnLastImportedGameIdChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLastImportedGame));
        OnPropertyChanged(nameof(LastImportedGameHintText));
    }

    partial void OnLastImportedGameTitleChanged(string? value) =>
        OnPropertyChanged(nameof(LastImportedGameHintText));

    partial void OnAntiCheatGuardPhaseChanged(AntiCheatGuardPhase value)
    {
        OnPropertyChanged(nameof(IsAntiCheatGuardIdle));
        OnPropertyChanged(nameof(IsAntiCheatGuardWarning));
        OnPropertyChanged(nameof(IsAntiCheatGuardLocked));
    }

    partial void OnCloseWindowToTrayChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(SettingKeyCloseToTray, value ? "true" : "false");
    }

    partial void OnStartWithWindowsMinimizedToTrayChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(SettingKeyStartWithWindowsTray, value ? "true" : "false");
        _applyAutoStart(value);
        EmitStatusFeedback(
            value ? "已开启开机静默启动到系统托盘" : "已关闭开机静默启动",
            isError: false);
    }

    partial void OnSelectedAfkTimeoutSecChanged(int value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(AppSettingKeys.LauncherAfkTimeoutSec, value.ToString(CultureInfo.InvariantCulture));
    }

    partial void OnAppliedSteamInstallDirectoryOverrideChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSteamInstallDirectoryOverride));
    }

    partial void OnIsSteamClientDetectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SteamDetectionStatusKind));
        OnPropertyChanged(nameof(SteamAccountsEmptyHintText));
    }

    partial void OnIsSteamClientManualOverrideChanged(bool value)
    {
        OnPropertyChanged(nameof(SteamDetectionStatusKind));
    }

    partial void OnSteamPathValidationErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSteamPathValidationError));
    }

    partial void OnSelectedSteamAccountChanged(SteamAccountInfo? value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        uint? nextId = value?.AccountId;
        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            SelectedSteamTargetAccountId = nextId;
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }

        _ = _db.SetSteamTargetAccountIdAsync(nextId);
        SteamSettingsChanged?.Invoke();
        if (value is not null)
        {
            EmitStatusFeedback($"已切换目标 Steam 账号：{value.DisplayLabel}", isError: false);
        }
    }

    partial void OnSelectedSteamTargetAccountIdChanged(uint? value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        SteamAccountInfo? matched = value is > 0
            ? SteamAccounts.FirstOrDefault(a => a.AccountId == value.Value)
            : null;
        if (!Equals(SelectedSteamAccount, matched))
        {
            bool prevSuppress = _suppressAutoSave;
            _suppressAutoSave = true;
            try
            {
                SelectedSteamAccount = matched;
            }
            finally
            {
                _suppressAutoSave = prevSuppress;
            }
        }

        _ = _db.SetSteamTargetAccountIdAsync(value);
        SteamSettingsChanged?.Invoke();
    }

    partial void OnSteamSuppressDuplicateAchievementToastChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSteamSuppressDuplicateAchievementToastAsync(value);
        SteamSettingsChanged?.Invoke();
        EmitStatusFeedback(
            value
                ? "已开启：通过 Steam 启动正版 Steam 游戏时静默 SPAWN 成就弹窗"
                : "已关闭：通过 Steam 启动正版 Steam 游戏时保留 SPAWN 成就弹窗",
            isError: false);
    }

    partial void OnSelectedAchievementLanguageChanged(string value)
    {
        if (_suppressAutoSave || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _ = _db.SetSettingAsync(AppSettingKeys.AchievementLanguage, value.Trim().ToLowerInvariant());
        EmitStatusFeedback("已保存成就语言偏好，刷新游戏成就元数据后生效", isError: false);
    }

    partial void OnGlobalRevealSpoilersChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(AppSettingKeys.AchievementRevealSpoilers, value ? "true" : "false");
        GlobalSpoilerPreferenceChanged?.Invoke(value);
    }

    partial void OnOverlayEnabledChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(SettingKeyOverlayEnabled, value ? "true" : "false");
        EmitStatusFeedback(
            value ? "已启用游戏内成就跳杯弹窗" : "已开启游戏内弹窗免打扰（后台仍正常记录解锁进度）",
            isError: false);
    }

    partial void OnSelectedOverlayThemeChanged(OverlayThemeKind value) => PersistOverlaySettingsIfReady();

    partial void OnSelectedOverlayPositionChanged(OverlayPositionKind value) => PersistOverlaySettingsIfReady();

    partial void OnSelectedOverlaySoundPresetChanged(OverlaySoundPreset value)
    {
        OnPropertyChanged(nameof(ShowCustomWavPicker));
        OnPropertyChanged(nameof(ShowSteamSoundStatus));
        RefreshSteamClientSoundStatus();
        PersistOverlaySettingsIfReady();
    }

    partial void OnCustomOverlaySoundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CustomWavDisplayStatus));
        PersistOverlaySettingsIfReady();
    }

    partial void OnSelectedAudioOutputDeviceIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        UpdateAudioRoutingSummary(AudioDeviceDiscoveryHelper.QueryActiveRenderDevices());
        PersistOverlaySettingsIfReady();
    }

    partial void OnOverlayVolumePercentChanged(double value)
    {
        if (!_suppressAutoSave && value > 0.0 && OverlayMuteSound)
        {
            OverlayMuteSound = false;
        }

        OnPropertyChanged(nameof(OverlayVolumeDisplayText));
        ScheduleDebouncedOverlaySettingsPersist();
    }

    partial void OnOverlayMuteSoundChanged(bool value)
    {
        OnPropertyChanged(nameof(OverlayVolumeDisplayText));
        OnPropertyChanged(nameof(OverlayVolumeSliderOpacity));
        PersistOverlaySettingsIfReady();
    }

    partial void OnSelectedUiThemeModeChanged(UiThemePreferenceMode value)
    {
        SpawnUiThemeKind resolved = value switch
        {
            UiThemePreferenceMode.Dark => SpawnUiThemeKind.Dark,
            UiThemePreferenceMode.Light => SpawnUiThemeKind.Light,
            _ => _detectSystemTheme(),
        };

        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            if (SelectedUiTheme != resolved)
            {
                SelectedUiTheme = resolved;
            }
            else
            {
                _themeManager.ApplyTheme(resolved);
            }
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }

        if (!_suppressAutoSave)
        {
            string persisted = value == UiThemePreferenceMode.System
                ? nameof(UiThemePreferenceMode.System)
                : resolved.ToString();
            _ = _db.SetSettingAsync(AppSettingKeys.UiTheme, persisted);
            string msg = value switch
            {
                UiThemePreferenceMode.System => $"已切换为跟随 Windows 系统（当前：{(resolved == SpawnUiThemeKind.Dark ? "深色" : "浅色")}）",
                UiThemePreferenceMode.Dark => "已切换为极客深色主题",
                _ => "已切换为清透浅色主题",
            };
            EmitStatusFeedback(msg, isError: false);
        }
    }

    partial void OnSelectedUiThemeChanged(SpawnUiThemeKind value)
    {
        _themeManager.ApplyTheme(value);
        if (!_suppressAutoSave)
        {
            UiThemePreferenceMode targetMode = value == SpawnUiThemeKind.Dark
                ? UiThemePreferenceMode.Dark
                : UiThemePreferenceMode.Light;
            if (SelectedUiThemeMode != targetMode)
            {
                _suppressAutoSave = true;
                try
                {
                    SelectedUiThemeMode = targetMode;
                }
                finally
                {
                    _suppressAutoSave = false;
                }
            }

            _ = _db.SetSettingAsync(AppSettingKeys.UiTheme, value.ToString());
            EmitStatusFeedback(value == SpawnUiThemeKind.Dark ? "已切换为极客深色主题" : "已切换为清透浅色主题", isError: false);
        }
    }

    partial void OnSelectedTrainerAudioModeChanged(TrainerAudioMode value)
    {
        OnPropertyChanged(nameof(ShowTrainerCueVolumeSlider));
        PersistTrainerSettingsIfReady(applyAudioPolicy: true);
    }

    partial void OnTrainerCueVolumePercentChanged(double value)
    {
        OnPropertyChanged(nameof(TrainerCueVolumeDisplayText));
        ScheduleDebouncedTrainerCueVolumePersist();
    }

    partial void OnTrainerEnableA0PreMuteChanged(bool value) => PersistTrainerSettingsIfReady(applyAudioPolicy: true);

    partial void OnTrainerHotkeyHoldMsChanged(int value)
    {
        int clamped = Math.Clamp(value, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs);
        if (clamped != value)
        {
            TrainerHotkeyHoldMs = clamped;
            return;
        }

        OnPropertyChanged(nameof(TrainerHotkeyHoldMsDisplayText));
        PersistTrainerSettingsIfReady();
    }

    partial void OnTrainerDefaultScanCodeModeChanged(bool value) => PersistTrainerSettingsIfReady();
    partial void OnTrainerStopWithGameExitChanged(bool value) => PersistTrainerSettingsIfReady();
    partial void OnTrainerHideOriginalWindowOnLaunchChanged(bool value) => PersistTrainerSettingsIfReady();
    partial void OnTrainerCreatePreSnapshotChanged(bool value) => PersistTrainerSettingsIfReady();
    partial void OnSelectedTrainerSnapshotRetentionCountChanged(int value) => PersistTrainerSettingsIfReady();
    partial void OnSelectedTrainerSnapshotMaxSizeMbChanged(int value) => PersistTrainerSettingsIfReady();
    partial void OnTrainerIncludeFlingArchiveChanged(bool value) => PersistTrainerSettingsIfReady();
    partial void OnTrainerStrictVanguardModeChanged(bool value)
    {
        PersistTrainerSettingsIfReady();
        if (!_suppressAutoSave)
        {
            TrainerSafetySettingsChanged?.Invoke();
        }
    }

    partial void OnAppliedCustomTrainerSandboxRootChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTrainerSandboxRoot));
        OnPropertyChanged(nameof(HasCustomTrainerSandboxRoot));
        OnPropertyChanged(nameof(HasNonAsciiTrainerSandboxPath));
    }

    partial void OnDefenderStatusReportChanged(DefenderExclusionStatusReport? value)
    {
        OnPropertyChanged(nameof(DefenderStatusBadgeText));
        OnPropertyChanged(nameof(DefenderStatusMessage));
        OnPropertyChanged(nameof(IsDefenderExcluded));
        OnPropertyChanged(nameof(IsDefenderNotExcluded));
        OnPropertyChanged(nameof(ShowSmartAppControlCard));
        OnPropertyChanged(nameof(ShowDefenderManualGuidance));
    }

    partial void OnSaveAutoWatchByDefaultChanged(bool value) => PersistSaveSettingsIfReady();
    partial void OnSavePostExitSnapshotByDefaultChanged(bool value) => PersistSaveSettingsIfReady();
    partial void OnSaveIncludeConfigByDefaultChanged(bool value) => PersistSaveSettingsIfReady();

    partial void OnSaveQuiescentDebounceMsChanged(int value)
    {
        int clamped = Math.Clamp(value, SaveSettingsDto.MinQuiescentDebounceMs, SaveSettingsDto.MaxQuiescentDebounceMs);
        if (clamped != value)
        {
            SaveQuiescentDebounceMs = clamped;
            return;
        }

        OnPropertyChanged(nameof(SaveQuiescentDebounceDisplayText));
        PersistSaveSettingsIfReady();
    }

    partial void OnSaveDefaultMaxRollingSnapshotsChanged(int value) => PersistSaveSettingsIfReady();
    partial void OnSaveDefaultMaxSnapshotSizeMbChanged(int value) => PersistSaveSettingsIfReady();
    partial void OnLudusaviRuleCountChanged(int value) => OnPropertyChanged(nameof(SaveLudusaviAndVaultSummaryText));
    partial void OnSaveVaultStatsChanged(CasVaultStatsDto? value) => OnPropertyChanged(nameof(SaveLudusaviAndVaultSummaryText));
    partial void OnNewCloudProviderTypeChanged(CloudSyncProviderType value) => OnPropertyChanged(nameof(IsNewCloudEndpointRemote));

    [RelayCommand]
    private void SelectCategory(SettingsCategory category) => SelectedCategory = category;

    [RelayCommand]
    private void SetUiTheme(SpawnUiThemeKind theme) => SelectedUiTheme = theme;

    [RelayCommand]
    private void SetOverlayTheme(OverlayThemeKind theme) => SelectedOverlayTheme = theme;

    [RelayCommand]
    private async Task AddScanDirectoryAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要纳入 SPAWN 游戏库扫描的根目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        await AddScanDirectoryPathAsync(dialog.FolderName, triggerImmediateScan: true).ConfigureAwait(true);
    }

    public async Task AddScanDirectoryPathAsync(string folderPath, bool triggerImmediateScan = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return;
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim().Trim('"')));
        var customDirs = await _db.GetSettingJsonAsync<List<string>>(AppSettingKeys.LibraryScanDirectories, []).ConfigureAwait(true);
        if (!customDirs.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            customDirs.Add(normalized);
            await _db.SetSettingJsonAsync(AppSettingKeys.LibraryScanDirectories, customDirs).ConfigureAwait(true);
            await ReloadScanDirectoriesAsync().ConfigureAwait(true);
        }

        if (triggerImmediateScan && ScanDirectoriesRequested is not null)
        {
            await ScanDirectoriesRequested([normalized]).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BrowseAndImportSingleGameFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要导入的单机游戏文件夹",
            Multiselect = false,
        };

        string? initialDir = _paths.LocalGamesDirectory;
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
        {
            dialog.InitialDirectory = initialDir;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ManualGameImportPathText = dialog.FolderName;
            await ImportManualGamePathAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BrowseAndImportSingleGameExeAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要导入的单机游戏主程序 (.exe)",
            Filter = "可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            ManualGameImportPathText = dialog.FileName;
            await ImportManualGamePathAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    public async Task ImportManualGamePathAsync()
    {
        string raw = ManualGameImportPathText.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw))
        {
            EmitStatusFeedback("请输入或浏览选择游戏文件夹 / 主程序 (.exe) 路径", isError: true);
            return;
        }

        if (ManualGameImportWithResultRequested is not null)
        {
            ManualImportOutcome outcome = await ManualGameImportWithResultRequested(raw).ConfigureAwait(true);
            if (outcome.Succeeded)
            {
                ManualGameImportPathText = string.Empty;
                if (!string.IsNullOrWhiteSpace(outcome.GameId))
                {
                    LastImportedGameId = outcome.GameId;
                    LastImportedGameTitle = outcome.GameTitle ?? "新导入游戏";
                }
            }

            return;
        }

        if (ManualGameImportRequested is not null)
        {
            await ManualGameImportRequested(raw).ConfigureAwait(true);
            ManualGameImportPathText = string.Empty;
        }
    }

    [RelayCommand]
    private void OpenLastImportedGame()
    {
        if (!string.IsNullOrWhiteSpace(LastImportedGameId))
        {
            OpenLastImportedGameRequested?.Invoke(LastImportedGameId!);
        }
    }

    [RelayCommand]
    private void ToggleShowCustomSteamApiKey() => ShowCustomSteamApiKey = !ShowCustomSteamApiKey;

    [RelayCommand]
    private void OpenSteamApiKeyRegistration()
    {
        try
        {
            _openExternalUrl(SteamApiKeyRegistrationUrl);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"无法打开 Steam Web API Key 申请页面：{ex.Message}", isError: true);
        }
    }

    // -------------------------------------------------------------------------
    // Trainer & Security Sandbox Commands (Category 6)
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseCustomSandboxRoot()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择纯 ASCII 自定义修改器沙箱根目录",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            TrainerCustomSandboxRootInput = dialog.FolderName;
        }
    }

    [RelayCommand]
    public async Task ApplyCustomSandboxRootAsync()
    {
        if (_trainerRuntime?.ActiveSession is not null)
        {
            EmitStatusFeedback("当前有修改器正在运行，请先停止修改器后再切换或迁移沙箱根目录。", isError: true);
            return;
        }

        string raw = TrainerCustomSandboxRootInput.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(raw))
        {
            await ResetDefaultSandboxRootAsync().ConfigureAwait(true);
            return;
        }

        var games = await _db.GetAllGamesAsync().ConfigureAwait(true);
        var gameDirs = games.Select(static g => g.InstallDir).ToArray();

        if (!DefenderExclusionAssistant.TryValidateCustomAsciiSandboxRoot(raw, _paths, gameDirs, out string normalized, out string? rejectReason))
        {
            EmitStatusFeedback(rejectReason ?? "自定义沙箱目录校验未通过", isError: true);
            return;
        }

        string previousRoot = EffectiveTrainerSandboxRoot;
        try
        {
            var (succeeded, _, message) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(previousRoot, normalized).ConfigureAwait(true);
            if (!succeeded)
            {
                EmitStatusFeedback(message, isError: true);
                return;
            }

            AppliedCustomTrainerSandboxRoot = normalized;
            TrainerCustomSandboxRootInput = normalized;
            DefenderStatusReport = null;
            PersistTrainerSettingsIfReady();
            EmitStatusFeedback(message, isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"迁移修改器沙箱目录失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public async Task ResetDefaultSandboxRootAsync()
    {
        if (_trainerRuntime?.ActiveSession is not null)
        {
            EmitStatusFeedback("当前有修改器正在运行，请先停止修改器后再恢复默认沙箱根目录。", isError: true);
            return;
        }

        string previousRoot = EffectiveTrainerSandboxRoot;
        string defaultRoot = _paths.TrainerSandboxDirectory;

        try
        {
            if (!string.Equals(previousRoot, defaultRoot, StringComparison.OrdinalIgnoreCase))
            {
                var (succeeded, _, message) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(previousRoot, defaultRoot, destinationIsSpawnOwnedDefaultRoot: true).ConfigureAwait(true);
                if (!succeeded)
                {
                    EmitStatusFeedback(message, isError: true);
                    return;
                }
            }

            AppliedCustomTrainerSandboxRoot = null;
            TrainerCustomSandboxRootInput = string.Empty;
            DefenderStatusReport = null;
            PersistTrainerSettingsIfReady();
            EmitStatusFeedback($"已恢复默认修改器沙箱根目录：{defaultRoot}", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"恢复默认修改器沙箱目录失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public Task InspectDefenderExclusionAsync() => InspectDefenderExclusionCoreAsync(emitFeedback: true);

    private async Task InspectDefenderExclusionCoreAsync(bool emitFeedback)
    {
        if (_defenderAssistant is null || IsDefenderWorking)
        {
            return;
        }

        IsDefenderWorking = true;
        try
        {
            DefenderStatusReport = await _defenderAssistant.InspectStatusAsync(EffectiveTrainerSandboxRoot).ConfigureAwait(true);
            if (emitFeedback)
            {
                bool isErr = DefenderStatusReport.Status is DefenderExclusionStatus.Failed or DefenderExclusionStatus.VerificationReadbackFailed;
                EmitStatusFeedback(DefenderStatusReport.Message, isError: isErr);
            }
        }
        catch (Exception ex)
        {
            if (emitFeedback)
            {
                EmitStatusFeedback($"检测 Defender 状态失败：{ex.Message}", isError: true);
            }
        }
        finally
        {
            IsDefenderWorking = false;
        }
    }

    [RelayCommand]
    public async Task RequestDefenderExclusionAsync()
    {
        if (_defenderAssistant is null || IsDefenderWorking)
        {
            return;
        }

        IsDefenderWorking = true;
        try
        {
            DefenderStatusReport = await _defenderAssistant.RequestExclusionAsync(EffectiveTrainerSandboxRoot).ConfigureAwait(true);
            bool isErr = !DefenderStatusReport.IsExcluded
                && DefenderStatusReport.Status != DefenderExclusionStatus.UacCancelledByUser;
            EmitStatusFeedback(DefenderStatusReport.Message, isError: isErr);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"配置 Defender 沙箱排除失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsDefenderWorking = false;
        }
    }

    [RelayCommand]
    public void CopySandboxPath()
    {
        try
        {
            Clipboard.SetText(EffectiveTrainerSandboxRoot);
            EmitStatusFeedback($"已复制单一沙箱目录路径：{EffectiveTrainerSandboxRoot}", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"复制沙箱路径失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public void OpenDefenderSettings()
    {
        try
        {
            _openExternalUrl(DefenderExclusionAssistant.DefenderExclusionsSettingsUri);
            EmitStatusFeedback("已尝试打开 Windows 安全中心排除项页面，请粘贴刚刚复制的单一沙箱路径", isError: false);
        }
        catch
        {
            try
            {
                _openExternalUrl(DefenderExclusionAssistant.DefenderSettingsFallbackUri);
                EmitStatusFeedback("已打开 Windows 安全中心设置，请进入病毒和威胁防护 → 管理设置 → 排除项", isError: false);
            }
            catch (Exception ex)
            {
                EmitStatusFeedback($"无法打开 Windows 安全中心：{ex.Message}", isError: true);
            }
        }
    }

    [RelayCommand]
    public void OpenSmartAppControlLearnMore()
    {
        try
        {
            _openExternalUrl(DefenderExclusionAssistant.SmartAppControlLearnMoreUrl);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"无法打开微软说明页面：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public async Task RestoreTrainerA0BackupsAsync()
    {
        if (_trainerAudioController is null)
        {
            EmitStatusFeedback("当前运行环境未挂载修改器音频控制器", isError: true);
            return;
        }

        try
        {
            await _trainerAudioController.RestoreA0BackupsAsync().ConfigureAwait(true);
            EmitStatusFeedback("已还原修改器静音配置备份并清理 SPAWN 新建配置", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"还原静音配置备份失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task RemoveScanDirectoryAsync(ScanDirectoryEntry? entry)
    {
        if (entry is null || !entry.IsRemovable)
        {
            return;
        }

        var customDirs = await _db.GetSettingJsonAsync<List<string>>(AppSettingKeys.LibraryScanDirectories, []).ConfigureAwait(true);
        customDirs.RemoveAll(p => string.Equals(
            Path.TrimEndingDirectorySeparator(p),
            Path.TrimEndingDirectorySeparator(entry.Path),
            StringComparison.OrdinalIgnoreCase));
        await _db.SetSettingJsonAsync(AppSettingKeys.LibraryScanDirectories, customDirs).ConfigureAwait(true);
        await ReloadScanDirectoriesAsync().ConfigureAwait(true);
        EmitStatusFeedback($"已移除扫描目录：{entry.Path}", isError: false);
    }

    [RelayCommand]
    private async Task ScanAllConfiguredDirectoriesAsync()
    {
        var dirs = GetAllEffectiveScanDirectories();
        if (dirs.Count == 0)
        {
            EmitStatusFeedback("尚未配置默认游戏库目录，请先点击「添加目录」", isError: true);
            return;
        }

        if (ScanDirectoriesRequested is not null)
        {
            await ScanDirectoriesRequested(dirs).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenDataDirectory() => OpenFolderInExplorer(_paths.DataRoot, "数据目录", ensureCreated: true);

    [RelayCommand]
    private void OpenLogsDirectory() => OpenFolderInExplorer(_paths.LogsDirectory, "日志目录", ensureCreated: true);

    [RelayCommand]
    private void OpenTrashDirectory() => OpenFolderInExplorer(_paths.TrashDir, "回收站目录", ensureCreated: true);

    [RelayCommand]
    private void OpenInstallDirectory() => OpenFolderInExplorer(SpawnBuildInfo.InstallDirectory, "安装目录", ensureCreated: false);

    private void OpenFolderInExplorer(string dirPath, string label, bool ensureCreated)
    {
        try
        {
            if (ensureCreated)
            {
                _paths.EnsureDirectoriesExist();
                Directory.CreateDirectory(dirPath);
            }

            if (Directory.Exists(dirPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dirPath}\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                EmitStatusFeedback($"{label}尚不存在：{dirPath}", isError: true);
            }
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"打开{label}失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void CopyVersionDiagnostics()
    {
        try
        {
            string info = $"{BuildProductName} | Version={BuildVersionText} | Channel={SpawnBuildInfo.Channel} | Identity={_paths.Identity} | Commit={BuildCommitShaText} | Built={BuildTimeUtcText}";
            Clipboard.SetText(info);
            EmitStatusFeedback("已复制版本与通道诊断信息到剪贴板", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"复制失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task SaveCustomSteamApiKeyAsync()
    {
        string trimmed = CustomSteamApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            EmitStatusFeedback("请输入有效的 32 位 Steam Web API Key 后再点击保存", isError: true);
            return;
        }

        if (!IsValidSteamWebApiKeyFormat(trimmed))
        {
            EmitStatusFeedback("Steam Web API Key 格式无效：必须为 32 位十六进制字符（0-9、A-F），请检查后重试", isError: true);
            return;
        }

        await _steamApiKeyProvider.SaveCustomApiKeyAsync(trimmed).ConfigureAwait(true);
        CustomSteamApiKeyInput = string.Empty;
        await RefreshSteamApiKeyStatusAsync().ConfigureAwait(true);
        EmitStatusFeedback("已通过当前 Windows 用户安全加密保存 Steam Web API Key", isError: false);
    }

    [RelayCommand]
    private async Task ClearCustomSteamApiKeyAsync()
    {
        await _steamApiKeyProvider.SaveCustomApiKeyAsync(null).ConfigureAwait(true);
        CustomSteamApiKeyInput = string.Empty;
        await RefreshSteamApiKeyStatusAsync().ConfigureAwait(true);
        EmitStatusFeedback("已清除本机自定义密钥，已恢复默认密钥解析链", isError: false);
    }

    [RelayCommand]
    private void BrowseCustomOverlayWav()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择自定义成就跳杯 WAV 音频文件",
            Filter = "WAV 音频文件 (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            string sourcePath = Path.GetFullPath(dialog.FileName);
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists || sourceInfo.Length is <= 44 or > 20 * 1024 * 1024)
            {
                EmitStatusFeedback("请选择有效且体积不超过 20 MB 的标准 WAV 音频文件", isError: true);
                return;
            }

            Span<byte> header = stackalloc byte[12];
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Read(header) < 12
                    || !header[..4].SequenceEqual("RIFF"u8)
                    || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
                {
                    EmitStatusFeedback("所选文件不是标准 RIFF WAV 音频格式，请选择未压缩 PCM/Float .wav 文件", isError: true);
                    return;
                }
            }

            Directory.CreateDirectory(_paths.ConfigDirectory);
            string targetPath = Path.Combine(_paths.ConfigDirectory, "custom_toast.wav");
            if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            _suppressAutoSave = true;
            try
            {
                CustomOverlaySoundPath = targetPath;
                SelectedOverlaySoundPreset = OverlaySoundPreset.CustomWav;
            }
            finally
            {
                _suppressAutoSave = false;
            }

            OnPropertyChanged(nameof(CustomWavDisplayStatus));
            OnPropertyChanged(nameof(ShowCustomWavPicker));
            OnPropertyChanged(nameof(ShowSteamSoundStatus));
            PersistOverlaySettingsIfReady();
            EmitStatusFeedback($"已导入自定义跳杯音效（{Path.GetFileName(sourcePath)}）", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"导入自定义 WAV 音效失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task TriggerTestToastAsync()
    {
        var req = new OverlayToastRequest
        {
            GameTitle = "SPAWN 弹窗预览",
            AchievementApiName = "SPAWN_TEST_TOAST",
            DisplayName = SelectedOverlayTheme switch
            {
                OverlayThemeKind.Ps5Trophy => "破晓行者 · 金杯预览",
                OverlayThemeKind.XboxRare => "深渊凝视者 · 稀有紫钻",
                _ => "初入暗林 · 成就跳杯测试",
            },
            Description = $"当前样式：{UiFormatters.FormatOverlayThemeLabel(SelectedOverlayTheme)} · {UiFormatters.FormatOverlaySoundPresetLabel(SelectedOverlaySoundPreset)}",
            IsRare = SelectedOverlayTheme == OverlayThemeKind.XboxRare,
            GlobalPercent = SelectedOverlayTheme == OverlayThemeKind.XboxRare ? 3.8 : 8.6,
            IsPlatinumCompletion = false,
            Theme = SelectedOverlayTheme,
            Position = SelectedOverlayPosition,
            SoundPreset = SelectedOverlaySoundPreset,
            CustomSoundPath = CustomOverlaySoundPath,
            AudioOutputDeviceId = SelectedAudioOutputDeviceId,
            SoundVolume = Math.Clamp(OverlayVolumePercent / 100.0, 0.0, 1.0),
            MuteSound = OverlayMuteSound,
            DurationMs = 4200,
        };

        bool ok = await _overlayIpcClient.SendToastAsync(req).ConfigureAwait(true);
        string disabledSuffix = OverlayEnabled ? string.Empty : "（当前处于免打扰模式，实际游戏中已静默）";
        EmitStatusFeedback(
            ok
                ? $"已发送实时测试成就弹窗（不抢夺当前焦点）{disabledSuffix}"
                : "未能连接游戏内弹窗服务，请稍后重试或重启 SPAWN",
            isError: !ok);
    }

    [RelayCommand]
    private async Task TriggerPlatinumChainTestAsync()
    {
        var req = new OverlayToastRequest
        {
            GameTitle = "LIMBO",
            AchievementApiName = "SPAWN_TEST_FINAL_TROPHY",
            DisplayName = "叮！最后一枚彩蛋",
            Description = "在漆黑边境集齐全部隐藏彩蛋（即将自动触发白金连发！）",
            IsRare = true,
            GlobalPercent = 2.4,
            IsPlatinumCompletion = true,
            Theme = OverlayThemeKind.Ps5Trophy,
            Position = SelectedOverlayPosition,
            SoundPreset = SelectedOverlaySoundPreset,
            CustomSoundPath = CustomOverlaySoundPath,
            AudioOutputDeviceId = SelectedAudioOutputDeviceId,
            SoundVolume = Math.Clamp(OverlayVolumePercent / 100.0, 0.0, 1.0),
            MuteSound = OverlayMuteSound,
            DurationMs = 3800,
        };

        bool ok = await _overlayIpcClient.SendToastAsync(req).ConfigureAwait(true);
        EmitStatusFeedback(
            ok
                ? "已触发 PS5 金杯 + 100% 白金奖杯双连发预览！"
                : "未能连接游戏内弹窗服务，请稍后重试或重启 SPAWN",
            isError: !ok);
    }

    public async Task RefreshSteamApiKeyStatusAsync(CancellationToken cancellationToken = default)
    {
        SteamApiKeyStatusDto status = await _steamApiKeyProvider.GetStatusAsync(cancellationToken).ConfigureAwait(true);
        IsSteamApiKeyConfigured = status.IsConfigured;
        SteamApiKeyStatusText = status.IsConfigured && !string.IsNullOrWhiteSpace(status.MaskedPreview)
            ? status.MaskedPreview!
            : status.SourceLabel;
    }

    private async Task ReloadScanDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        ScanDirectories.Clear();

        string builtInNote = IsDevOrSourceBuild ? "开发工作区默认扫描目录（自动发现）" : "系统默认游戏库目录";
        foreach (string builtIn in _paths.GetDefaultScanDirectories())
        {
            ScanDirectories.Add(new ScanDirectoryEntry(builtIn, builtInNote, IsRemovable: false));
        }

        var customDirs = await _db.GetSettingJsonAsync<List<string>>(AppSettingKeys.LibraryScanDirectories, [], cancellationToken).ConfigureAwait(true);
        foreach (string custom in customDirs)
        {
            if (string.IsNullOrWhiteSpace(custom))
            {
                continue;
            }

            if (ScanDirectories.Any(existing => string.Equals(existing.Path, custom, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            ScanDirectories.Add(new ScanDirectoryEntry(custom, "用户自定义游戏库目录", IsRemovable: true));
        }
    }

    private void ScheduleDebouncedOverlaySettingsPersist()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _overlayVolumeDebounceCts?.Cancel();
        _overlayVolumeDebounceCts?.Dispose();
        _overlayVolumeDebounceCts = null;

        if (_volumeDebounceMs <= 0)
        {
            PersistOverlaySettingsIfReady();
            return;
        }

        var cts = new CancellationTokenSource();
        _overlayVolumeDebounceCts = cts;
        _ = RunDebouncedAsync(_volumeDebounceMs, PersistOverlaySettingsIfReady, cts.Token);
    }

    private void ScheduleDebouncedTrainerCueVolumePersist()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _trainerCueVolumeDebounceCts?.Cancel();
        _trainerCueVolumeDebounceCts?.Dispose();
        _trainerCueVolumeDebounceCts = null;

        if (_volumeDebounceMs <= 0)
        {
            PersistTrainerSettingsIfReady(applyAudioPolicy: true);
            return;
        }

        var cts = new CancellationTokenSource();
        _trainerCueVolumeDebounceCts = cts;
        _ = RunDebouncedAsync(_volumeDebounceMs, () => PersistTrainerSettingsIfReady(applyAudioPolicy: true), cts.Token);
    }

    private async Task RunDebouncedAsync(int delayMs, Action action, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                action();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer slider change.
        }
    }

    private void PersistOverlaySettingsIfReady()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        var dto = new OverlaySettingsDto
        {
            Theme = SelectedOverlayTheme,
            Position = SelectedOverlayPosition,
            SoundPreset = SelectedOverlaySoundPreset,
            CustomSoundPath = CustomOverlaySoundPath,
            AudioOutputDeviceId = SelectedAudioOutputDeviceId,
            SoundVolume = Math.Clamp(OverlayVolumePercent / 100.0, 0.0, 1.0),
            MuteSound = OverlayMuteSound,
        };

        _ = _db.SaveOverlaySettingsAsync(dto);
    }

    private void PersistTrainerSettingsIfReady(bool applyAudioPolicy = false)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        var dto = new TrainerSettingsDto
        {
            CustomSandboxRoot = string.IsNullOrWhiteSpace(AppliedCustomTrainerSandboxRoot)
                ? null
                : AppliedCustomTrainerSandboxRoot.Trim(),
            AudioMode = SelectedTrainerAudioMode,
            CueVolume = Math.Clamp(TrainerCueVolumePercent / 100.0, 0.0, 1.0),
            EnableA0PreMute = TrainerEnableA0PreMute,
            HotkeyHoldMs = Math.Clamp(TrainerHotkeyHoldMs, TrainerSettingsDto.MinHotkeyHoldMs, TrainerSettingsDto.MaxHotkeyHoldMs),
            CreatePreTrainerSnapshot = TrainerCreatePreSnapshot,
            PreTrainerSnapshotRetentionCount = SelectedTrainerSnapshotRetentionCount,
            PreTrainerSnapshotMaxSizeMb = SelectedTrainerSnapshotMaxSizeMb,
            StopWithGameExit = TrainerStopWithGameExit,
            HideOriginalWindowOnLaunch = TrainerHideOriginalWindowOnLaunch,
            StrictVanguardMode = TrainerStrictVanguardMode,
            IncludeFlingArchiveInSearch = TrainerIncludeFlingArchive,
            DefaultScanCodeMode = TrainerDefaultScanCodeMode,
        };

        _ = _db.SaveTrainerSettingsAsync(dto);
        if (applyAudioPolicy && _trainerAudioController is not null)
        {
            _ = _trainerAudioController.ApplyPolicyAsync(dto.ToAudioPolicy());
        }
    }

    // -------------------------------------------------------------------------
    // Save Time Machine & Decentralized Cloud Sync Settings (Category 8)
    // -------------------------------------------------------------------------

    public async Task RefreshSaveAndCloudSettingsAsync(CancellationToken cancellationToken = default)
    {
        SaveSettingsDto saveSettings = _saveService is not null
            ? await _saveService.GetSettingsAsync(cancellationToken).ConfigureAwait(true)
            : await _db.GetSaveSettingsAsync(cancellationToken).ConfigureAwait(true);

        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            SaveAutoWatchByDefault = saveSettings.AutoWatchByDefault;
            SavePostExitSnapshotByDefault = saveSettings.PostExitSnapshotByDefault;
            SaveIncludeConfigByDefault = saveSettings.IncludeConfigFilesByDefault;
            SaveQuiescentDebounceMs = saveSettings.QuiescentDebounceMs;
            SaveDefaultMaxRollingSnapshots = saveSettings.DefaultMaxRollingSnapshots;
            SaveDefaultMaxSnapshotSizeMb = saveSettings.DefaultMaxSnapshotSizeMb;
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }

        OnPropertyChanged(nameof(SaveQuiescentDebounceDisplayText));

        LudusaviRuleCount = _ruleResolver is not null
            ? await _ruleResolver.GetRuleCountAsync(cancellationToken).ConfigureAwait(true)
            : await _db.GetLudusaviRuleCountAsync(cancellationToken).ConfigureAwait(true);

        SaveVaultStats = _saveService is not null
            ? await _saveService.GetVaultStatsAsync(cancellationToken).ConfigureAwait(true)
            : await _db.GetCasVaultStatsAsync(cancellationToken).ConfigureAwait(true);

        IReadOnlyList<CloudSyncEndpointRecord> endpoints = _cloudSyncService is not null
            ? await _cloudSyncService.ListEndpointsAsync(cancellationToken).ConfigureAwait(true)
            : await _db.GetAllCloudSyncEndpointsAsync(cancellationToken).ConfigureAwait(true);

        CloudSyncEndpoints.Clear();
        foreach (CloudSyncEndpointRecord ep in endpoints)
        {
            CloudSyncEndpoints.Add(new CloudEndpointItemViewModel(ep));
        }
    }

    private void PersistSaveSettingsIfReady()
    {
        if (_suppressAutoSave)
        {
            return;
        }

        var dto = new SaveSettingsDto
        {
            AutoWatchByDefault = SaveAutoWatchByDefault,
            PostExitSnapshotByDefault = SavePostExitSnapshotByDefault,
            IncludeConfigFilesByDefault = SaveIncludeConfigByDefault,
            QuiescentDebounceMs = Math.Clamp(SaveQuiescentDebounceMs, SaveSettingsDto.MinQuiescentDebounceMs, SaveSettingsDto.MaxQuiescentDebounceMs),
            DefaultMaxRollingSnapshots = SaveDefaultMaxRollingSnapshots,
            DefaultMaxSnapshotSizeMb = SaveDefaultMaxSnapshotSizeMb,
        };

        _ = _saveService is not null
            ? _saveService.SaveSettingsAsync(dto)
            : _db.SaveSaveSettingsAsync(dto);
    }

    [RelayCommand]
    private void BrowseLocalCloudEndpointFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择本地同步目录或网盘同步文件夹（仅存放 32MB SPPK 与 JSON，绝不含 spawn.db）",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            NewCloudEndpointUrl = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(NewCloudEndpointName))
            {
                NewCloudEndpointName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.FolderName));
            }
        }
    }

    public async Task<bool> SaveNewCloudEndpointAsync(string? passwordOrSecretKey)
    {
        string name = NewCloudEndpointName.Trim();
        string url = NewCloudEndpointUrl.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
        {
            EmitStatusFeedback("请填写云同步端点名称以及本地目录或远程 URL", isError: true);
            return false;
        }

        var record = new CloudSyncEndpointRecord
        {
            EndpointId = Guid.CreateVersion7().ToString("N"),
            DisplayName = name,
            ProviderType = NewCloudProviderType,
            EndpointUrl = url,
            BucketOrRoot = NewCloudBucketOrRoot.Trim(),
            Region = NewCloudProviderType == CloudSyncProviderType.S3 && !string.IsNullOrWhiteSpace(NewCloudRegion)
                ? NewCloudRegion.Trim()
                : null,
            PathStyleAddressing = true,
            IsDefault = NewCloudEndpointIsDefault,
            HasSecretConfigured = !string.IsNullOrWhiteSpace(NewCloudUsernameOrAccessKey) || !string.IsNullOrWhiteSpace(passwordOrSecretKey),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        CloudSyncEndpointSecrets? secrets = NewCloudProviderType == CloudSyncProviderType.LocalFolder
            ? null
            : new CloudSyncEndpointSecrets(
                string.IsNullOrWhiteSpace(NewCloudUsernameOrAccessKey) ? null : NewCloudUsernameOrAccessKey.Trim(),
                string.IsNullOrWhiteSpace(passwordOrSecretKey) ? null : passwordOrSecretKey);

        try
        {
            if (_cloudSyncService is not null)
            {
                _ = await _cloudSyncService.SaveEndpointAsync(record, secrets).ConfigureAwait(true);
            }
            else
            {
                await _db.UpsertCloudSyncEndpointAsync(record).ConfigureAwait(true);
            }

            NewCloudEndpointName = string.Empty;
            NewCloudEndpointUrl = string.Empty;
            NewCloudBucketOrRoot = string.Empty;
            NewCloudUsernameOrAccessKey = string.Empty;
            await RefreshSaveAndCloudSettingsAsync().ConfigureAwait(true);
            EmitStatusFeedback($"已保存云同步端点「{name}」", isError: false);
            return true;
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"保存云同步端点失败：{ex.Message}", isError: true);
            return false;
        }
    }

    [RelayCommand]
    public async Task TestCloudEndpointAsync(CloudEndpointItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (_cloudSyncService is null)
        {
            item.LastTestSucceeded = false;
            item.LastTestResultText = "云同步服务尚未挂载";
            EmitStatusFeedback(item.LastTestResultText, isError: true);
            return;
        }

        try
        {
            CloudSyncEndpointTestResult res = await _cloudSyncService.TestEndpointConnectionAsync(item.EndpointId).ConfigureAwait(true);
            item.LastTestSucceeded = res.Succeeded;
            item.LastTestResultText = res.Succeeded
                ? $"连接正常（{res.LatencyMs} ms）· {res.Message}"
                : $"连接失败 · {res.Message}";
            EmitStatusFeedback(item.LastTestResultText, isError: !res.Succeeded);
        }
        catch (Exception ex)
        {
            item.LastTestSucceeded = false;
            item.LastTestResultText = $"测试异常：{ex.Message}";
            EmitStatusFeedback(item.LastTestResultText, isError: true);
        }
    }

    [RelayCommand]
    public async Task DeleteCloudEndpointAsync(CloudEndpointItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            if (_cloudSyncService is not null)
            {
                _ = await _cloudSyncService.DeleteEndpointAsync(item.EndpointId).ConfigureAwait(true);
            }
            else
            {
                _ = await _db.DeleteCloudSyncEndpointAsync(item.EndpointId).ConfigureAwait(true);
            }

            await RefreshSaveAndCloudSettingsAsync().ConfigureAwait(true);
            EmitStatusFeedback($"已删除云同步端点「{item.DisplayName}」", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"删除云同步端点失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public async Task ImportLudusaviRulesJsonAsync()
    {
        if (_ruleResolver is null)
        {
            EmitStatusFeedback("Ludusavi 规则解析器尚未挂载", isError: true);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择预编译 Ludusavi 存档规则 JSON 文件",
            Filter = "JSON 文件 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            int count = await _ruleResolver.ImportRulesJsonAsync(json).ConfigureAwait(true);
            await RefreshSaveAndCloudSettingsAsync().ConfigureAwait(true);
            EmitStatusFeedback($"已导入 {count} 条 Ludusavi 存档路径规则", isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"导入 Ludusavi 规则失败：{ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    public async Task RunSaveCasGcAsync()
    {
        if (_saveService is null)
        {
            EmitStatusFeedback("存档时间机器服务尚未挂载", isError: true);
            return;
        }

        try
        {
            SaveGcReport report = await _saveService.RunCasGarbageCollectionAsync(TimeSpan.Zero).ConfigureAwait(true);
            await RefreshSaveAndCloudSettingsAsync().ConfigureAwait(true);
            EmitStatusFeedback(
                report.DeletedChunkCount > 0
                    ? $"已清理 {report.DeletedChunkCount} 个未引用去重块，回收空间 {UiFormatters.FormatByteSize(report.ReclaimedBytes)}"
                    : "本地存档块库已处于最紧凑状态，暂无可回收空间",
                isError: false);
        }
        catch (Exception ex)
        {
            EmitStatusFeedback($"清理存档块库失败：{ex.Message}", isError: true);
        }
    }

    private static void OpenExternalUriDefault(string uri)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void RefreshAudioOutputDevices() => RefreshAudioOutputDevicesCore(notifyFeedback: true);

    private void RefreshAudioOutputDevicesCore(bool notifyFeedback)
    {
        AudioRenderDeviceSnapshot snap = AudioDeviceDiscoveryHelper.QueryActiveRenderDevices();
        string preserveSelection = string.IsNullOrWhiteSpace(SelectedAudioOutputDeviceId)
            || string.Equals(SelectedAudioOutputDeviceId, OverlayAudioOutputModes.AutoSmart, StringComparison.OrdinalIgnoreCase)
            ? OverlayAudioOutputModes.SystemDefault
            : SelectedAudioOutputDeviceId;

        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            AudioOutputDeviceOptions.Clear();

            string sysDefaultLabel = string.IsNullOrWhiteSpace(snap.SystemDefaultFriendlyName)
                ? "跟随系统默认输出设备"
                : $"跟随系统默认输出设备（当前：{snap.SystemDefaultFriendlyName}）";
            AudioOutputDeviceOptions.Add(new NamedOption<string>(
                OverlayAudioOutputModes.SystemDefault,
                sysDefaultLabel));

            foreach (AudioRenderEndpointInfo ep in snap.ActiveEndpoints)
            {
                string label = ep.IsSystemDefault
                    ? $"{ep.FriendlyName} [系统默认]"
                    : ep.FriendlyName;
                AudioOutputDeviceOptions.Add(new NamedOption<string>(ep.DeviceId, label));
            }

            if (!AudioOutputDeviceOptions.Any(o => string.Equals(o.Value, preserveSelection, StringComparison.OrdinalIgnoreCase)))
            {
                AudioOutputDeviceOptions.Add(new NamedOption<string>(
                    preserveSelection,
                    "已保存指定设备（当前未连接，自动回退系统默认）"));
            }

            SelectedAudioOutputDeviceId = preserveSelection;
            UpdateAudioRoutingSummary(snap);
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }

        if (notifyFeedback)
        {
            EmitStatusFeedback($"已刷新音频播放设备列表（共发现 {snap.ActiveEndpoints.Count} 个活跃输出端点）", isError: false);
        }
    }

    private void UpdateAudioRoutingSummary(AudioRenderDeviceSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(SelectedAudioOutputDeviceId)
            || string.Equals(SelectedAudioOutputDeviceId, OverlayAudioOutputModes.SystemDefault, StringComparison.OrdinalIgnoreCase)
            || string.Equals(SelectedAudioOutputDeviceId, OverlayAudioOutputModes.AutoSmart, StringComparison.OrdinalIgnoreCase))
        {
            AudioRoutingStatusText = string.IsNullOrWhiteSpace(snap.SystemDefaultFriendlyName)
                ? "默认跟随 Windows 系统当前输出设备（含显示器外接音响、扬声器或耳机）"
                : $"当前跟随系统默认：{snap.SystemDefaultFriendlyName}";
            return;
        }

        var matched = snap.ActiveEndpoints.FirstOrDefault(
            e => string.Equals(e.DeviceId, SelectedAudioOutputDeviceId, StringComparison.OrdinalIgnoreCase));
        AudioRoutingStatusText = matched is not null
            ? $"已手动锁定输出设备：{matched.FriendlyName}"
            : "所选设备当前未连接，播放时将自动回退至系统默认输出设备";
    }

    public void RefreshSteamClientSoundStatus()
    {
        SteamClientSoundProbeResult probe = SteamClientSoundLocator.Probe(AppliedSteamInstallDirectoryOverride);
        IsSteamSoundDetected = !string.IsNullOrWhiteSpace(probe.EffectiveSoundPath);
        SteamSoundStatusText = probe.StatusMessage;
    }

    [RelayCommand]
    public void RedetectSteamClient()
    {
        SteamPathValidationErrorText = string.Empty;
        RefreshSteamClientDiscovery(emitFeedback: true);
        RefreshSteamClientSoundStatus();
        SteamSettingsChanged?.Invoke();
    }

    [RelayCommand]
    private async Task BrowseSteamInstallFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Steam 客户端安装目录（包含 steam.exe）",
            Multiselect = false,
        };

        string? initialDir = LastSteamDiscovery?.InstallDirectory ?? AppliedSteamInstallDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
        {
            dialog.InitialDirectory = initialDir;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            await ApplySteamInstallDirectoryOverrideAsync(dialog.FolderName).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task BrowseSteamExecutableAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Steam 客户端主程序（steam.exe）",
            Filter = "Steam 客户端 (steam.exe)|steam.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        string? initialDir = LastSteamDiscovery?.InstallDirectory ?? AppliedSteamInstallDirectoryOverride;
        if (!string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir))
        {
            dialog.InitialDirectory = initialDir;
        }

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FileName))
        {
            await ApplySteamInstallDirectoryOverrideAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    public async Task<bool> ApplySteamInstallDirectoryOverrideAsync(
        string? rawPathOrExe,
        CancellationToken cancellationToken = default)
    {
        if (!_steamClientLocator.TryNormalizeInstallDirectory(
                rawPathOrExe,
                out string? normalizedDir,
                out string? steamExePath,
                out string? errorMessage)
            || string.IsNullOrWhiteSpace(normalizedDir))
        {
            SteamPathValidationErrorText = errorMessage ?? "所选路径无效，请选择包含 steam.exe 的目录或 steam.exe 文件。";
            EmitStatusFeedback(SteamPathValidationErrorText, isError: true);
            return false;
        }

        SteamPathValidationErrorText = string.Empty;
        await _db.SetSteamInstallDirectoryOverrideAsync(normalizedDir, cancellationToken).ConfigureAwait(true);
        AppliedSteamInstallDirectoryOverride = normalizedDir;
        RefreshSteamClientDiscovery(emitFeedback: false);
        RefreshSteamClientSoundStatus();
        SteamSettingsChanged?.Invoke();

        string displayPath = !string.IsNullOrWhiteSpace(steamExePath) ? steamExePath! : normalizedDir;
        EmitStatusFeedback($"已手动指定 Steam 客户端：{displayPath}", isError: false);
        return true;
    }

    [RelayCommand]
    public async Task ResetSteamInstallDirectoryOverrideAsync()
    {
        SteamPathValidationErrorText = string.Empty;
        await _db.SetSteamInstallDirectoryOverrideAsync(null).ConfigureAwait(true);
        AppliedSteamInstallDirectoryOverride = null;
        RefreshSteamClientDiscovery(emitFeedback: false);
        RefreshSteamClientSoundStatus();
        SteamSettingsChanged?.Invoke();
        EmitStatusFeedback("已恢复自动检测本机 Steam 客户端位置", isError: false);
    }

    public void RefreshSteamClientDiscovery(bool emitFeedback)
    {
        SteamClientDiscoveryResult result;
        try
        {
            result = _steamClientLocator.Discover(
                explicitInstallDirOverride: AppliedSteamInstallDirectoryOverride,
                preferredAccountId: SelectedSteamTargetAccountId);
        }
        catch (Exception ex)
        {
            result = new SteamClientDiscoveryResult
            {
                IsDetected = false,
                IsUserOverride = HasSteamInstallDirectoryOverride,
                Source = HasSteamInstallDirectoryOverride ? SteamPathDiscoverySource.UserOverride : SteamPathDiscoverySource.NotFound,
                WarningMessage = $"检测 Steam 客户端时发生异常：{ex.Message}",
            };
        }

        LastSteamDiscovery = result;
        IsSteamClientDetected = result.IsDetected;
        IsSteamClientManualOverride = result.IsUserOverride;
        IsSteamClientRunning = result.IsSteamRunning;

        SteamDetectionStatusBadgeText = result.IsDetected
            ? (result.IsUserOverride ? "已手动指定" : "已自动检测")
            : "未检测到 Steam";

        SteamRunningStatusText = result.IsSteamRunning ? "Steam 运行中" : "Steam 未运行";
        SteamRunningStatusToolTip = result.IsSteamRunning && result.SteamProcessId is > 0
            ? string.Create(CultureInfo.InvariantCulture, $"检测到正在运行的 steam.exe（PID {result.SteamProcessId.Value}）")
            : (result.IsSteamRunning
                ? "检测到正在运行的 steam.exe 进程"
                : "当前未检测到正在运行的 steam.exe 进程");

        SteamResolvedPathText = result.IsDetected && !string.IsNullOrWhiteSpace(result.SteamExecutablePath)
            ? result.SteamExecutablePath!
            : (!string.IsNullOrWhiteSpace(result.InstallDirectory)
                ? result.InstallDirectory!
                : "未检测到本机 Steam 客户端（便携版或自定义位置请点击「浏览指定…」）");

        string sourceLabel = result.Source switch
        {
            SteamPathDiscoverySource.UserOverride => "路径来源：用户手动指定",
            SteamPathDiscoverySource.RegistryCurrentUser => "路径来源：Windows 当前用户注册表 (HKCU)",
            SteamPathDiscoverySource.RegistryLocalMachine => "路径来源：Windows 系统注册表 (HKLM)",
            SteamPathDiscoverySource.DefaultDirectory => "路径来源：系统默认安装目录",
            _ => "路径来源：未定位到 Steam 安装目录",
        };

        SteamDetectionDetailText = !string.IsNullOrWhiteSpace(result.WarningMessage)
            ? result.WarningMessage!
            : (result.IsDetected && !string.IsNullOrWhiteSpace(result.InstallDirectory)
                ? $"{sourceLabel} · 安装目录：{result.InstallDirectory}"
                : sourceLabel);

        bool prevSuppress = _suppressAutoSave;
        _suppressAutoSave = true;
        try
        {
            SteamAccounts.Clear();
            foreach (SteamAccountInfo account in result.Accounts)
            {
                SteamAccounts.Add(account);
            }

            SteamAccountInfo? resolvedAccount = null;
            if (SelectedSteamTargetAccountId is > 0)
            {
                resolvedAccount = SteamAccounts.FirstOrDefault(a => a.AccountId == SelectedSteamTargetAccountId.Value);
            }

            if (resolvedAccount is null && result.ActiveAccountId is > 0)
            {
                resolvedAccount = SteamAccounts.FirstOrDefault(a => a.AccountId == result.ActiveAccountId.Value);
            }

            resolvedAccount ??= SteamAccounts.FirstOrDefault(static a => a.IsActiveProcessUser)
                ?? SteamAccounts.FirstOrDefault(static a => a.IsMostRecent)
                ?? SteamAccounts.FirstOrDefault();

            SelectedSteamAccount = resolvedAccount;
        }
        finally
        {
            _suppressAutoSave = prevSuppress;
        }

        OnPropertyChanged(nameof(HasSteamAccounts));
        OnPropertyChanged(nameof(SteamAccountsEmptyHintText));

        if (emitFeedback)
        {
            string msg = result.IsDetected
                ? $"已检测到 Steam 客户端（{SteamDetectionStatusBadgeText} · {SteamRunningStatusText}，发现 {SteamAccounts.Count} 个本地账号）"
                : (result.WarningMessage ?? "未检测到本机 Steam 客户端，可点击「浏览指定…」手动选择目录或 steam.exe");
            EmitStatusFeedback(msg, isError: !result.IsDetected);
        }
    }

    public void RefreshAntiCheatSummary(AntiCheatGuardSnapshot snap)
    {
        AntiCheatGuardPhase = snap.Phase;
        AntiCheatStatusSummary = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => "受保护网游运行中 · 高敏组件已自动锁定",
            AntiCheatGuardPhase.ResidualGuardWatch => "等待反作弊守护进程完全退出…",
            AntiCheatGuardPhase.CooldownLocked => string.Create(CultureInfo.InvariantCulture, $"反作弊 15 秒安全冷却中（剩余 {snap.RemainingCooldownSeconds:0.0} 秒）"),
            _ => "安全就绪 · 主进程与高敏组件物理隔离",
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _antiCheatGuard.StateChanged -= OnAntiCheatGuardStateChanged;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        Update.StatusFeedbackWithSeverityRequested -= EmitStatusFeedback;
        Update.Dispose();

        _overlayVolumeDebounceCts?.Cancel();
        _overlayVolumeDebounceCts?.Dispose();
        _overlayVolumeDebounceCts = null;

        _trainerCueVolumeDebounceCts?.Cancel();
        _trainerCueVolumeDebounceCts?.Dispose();
        _trainerCueVolumeDebounceCts = null;
    }
}
