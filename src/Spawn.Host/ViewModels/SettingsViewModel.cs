using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Core;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Modules.Progress.Security;

namespace Spawn.Host.ViewModels;

public sealed record NamedOption<T>(T Value, string Label);

public sealed record ScanDirectoryEntry(string Path, string SourceNote, bool IsRemovable);

public sealed partial class SettingsViewModel : ObservableObject
{
    public const string SettingKeyCloseToTray = "ui.close_to_tray";

    private readonly SpawnPaths _paths;
    private readonly SpawnBuildMetadataSnapshot _buildSnapshot;
    private readonly SpawnDatabase _db;
    private readonly ISteamApiKeyProvider _steamApiKeyProvider;
    private readonly IOverlayIpcClient _overlayIpcClient;
    private readonly IAntiCheatGuardService _antiCheatGuard;
    private readonly ThemeManagerService _themeManager;
    private readonly IDefenderExclusionAssistant? _defenderAssistant;
    private readonly ITrainerAudioController? _trainerAudioController;
    private readonly ITrainerRuntimeService? _trainerRuntime;
    private readonly Action<string> _openExternalUrl;
    private bool _suppressAutoSave;

    public event Action<string>? StatusFeedbackRequested;
    public event Func<IReadOnlyList<string>, Task>? ScanDirectoriesRequested;
    public event Func<string, Task>? ManualGameImportRequested;
    public event Action<bool>? GlobalSpoilerPreferenceChanged;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.GeneralAndLibrary;

    [ObservableProperty]
    private string _manualGameImportPathText = string.Empty;

    [ObservableProperty]
    private int _selectedAfkTimeoutSec = 300;

    [ObservableProperty]
    private bool _closeWindowToTray = true;

    [ObservableProperty]
    private bool _isSteamApiKeyConfigured;

    [ObservableProperty]
    private string _steamApiKeyStatusText = "正在检测密钥状态…";

    [ObservableProperty]
    private string _customSteamApiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _globalRevealSpoilers;

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
    private SpawnUiThemeKind _selectedUiTheme = SpawnUiThemeKind.Light;

    [ObservableProperty]
    private string _antiCheatStatusSummary = "Tier 0 安全就绪 · 零敏感导入表";

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

    public ObservableCollection<ScanDirectoryEntry> ScanDirectories { get; } = [];
    public UpdateViewModel Update { get; }

    public IReadOnlyList<NamedOption<TrainerAudioMode>> TrainerAudioModeOptions { get; } =
    [
        new(TrainerAudioMode.FullMute, "完全静音（默认推荐：A0 + WASAPI 静音）"),
        new(TrainerAudioMode.KeepCuesExperimental, "实验性：保留提示音（仅新版 FLiNG A0 生效）"),
        new(TrainerAudioMode.Passthrough, "不干预修改器音频"),
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

    public string CustomWavDisplayStatus =>
        !string.IsNullOrWhiteSpace(CustomOverlaySoundPath) && File.Exists(CustomOverlaySoundPath)
            ? $"已载入自定义音效：{Path.GetFileName(CustomOverlaySoundPath)}（已存入 config 目录）"
            : "尚未选择自定义 .wav 音频（未配置时将自动回退主题合成和弦音）";

    public string CurrentCategoryTitle => SelectedCategory switch
    {
        SettingsCategory.AchievementAndSteamApi => "成就与 Steam API",
        SettingsCategory.OverlayAndSound => "弹窗外观与音效",
        SettingsCategory.TrainerAndSandbox => "修改器与安全沙箱",
        SettingsCategory.AppearanceTheme => "外观主题",
        SettingsCategory.Updates => "更新",
        SettingsCategory.AboutAndVersion => "关于与版本",
        _ => "常规与游戏库",
    };

    public string EffectiveTrainerSandboxRoot => !string.IsNullOrWhiteSpace(AppliedCustomTrainerSandboxRoot)
        ? AppliedCustomTrainerSandboxRoot!
        : _paths.TrainerSandboxDirectory;

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
        ?? "仅在您点击下方按钮时才会触发单次 UAC，且仅排除单一修改器沙箱目录（绝不排除游戏目录、%LOCALAPPDATA% 或 Downloads）。";

    public bool IsDefenderExcluded => DefenderStatusReport?.IsExcluded == true;

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
        UpdateViewModel? updateViewModel = null)
    {
        _paths = paths;
        _buildSnapshot = SpawnBuildInfo.GetSnapshot(paths.Identity);
        _db = db;
        _steamApiKeyProvider = steamApiKeyProvider;
        _overlayIpcClient = overlayIpcClient;
        _antiCheatGuard = antiCheatGuard;
        _themeManager = themeManager;
        _defenderAssistant = defenderAssistant;
        _trainerAudioController = trainerAudioController;
        _trainerRuntime = trainerRuntime;
        _openExternalUrl = openExternalUrl ?? OpenExternalUriDefault;
        Update = updateViewModel ?? new UpdateViewModel(updateService: null, openExternalUrl: _openExternalUrl);
        Update.StatusFeedbackRequested += msg => StatusFeedbackRequested?.Invoke(msg);

        RefreshAntiCheatSummary(_antiCheatGuard.CurrentSnapshot);
        _antiCheatGuard.StateChanged += (_, snap) => RefreshAntiCheatSummary(snap);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _suppressAutoSave = true;
        try
        {
            string? rawUiTheme = await _db.GetSettingAsync(AppSettingKeys.UiTheme, cancellationToken).ConfigureAwait(true);
            SelectedUiTheme = Enum.TryParse<SpawnUiThemeKind>(rawUiTheme, ignoreCase: true, out var uiTheme)
                ? uiTheme
                : SpawnUiThemeKind.Light;
            _themeManager.ApplyTheme(SelectedUiTheme);

            string? rawCloseTray = await _db.GetSettingAsync(SettingKeyCloseToTray, cancellationToken).ConfigureAwait(true);
            CloseWindowToTray = string.IsNullOrWhiteSpace(rawCloseTray) || (!bool.TryParse(rawCloseTray, out bool closeTray) || closeTray);

            string? rawAfk = await _db.GetSettingAsync(AppSettingKeys.LauncherAfkTimeoutSec, cancellationToken).ConfigureAwait(true);
            if (int.TryParse(rawAfk, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedAfk) && parsedAfk >= 60)
            {
                SelectedAfkTimeoutSec = parsedAfk;
            }

            string? rawSpoilers = await _db.GetSettingAsync(AppSettingKeys.AchievementRevealSpoilers, cancellationToken).ConfigureAwait(true);
            GlobalRevealSpoilers = bool.TryParse(rawSpoilers, out bool spoilers) && spoilers;

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
            OnPropertyChanged(nameof(ShowTrainerCueVolumeSlider));
            OnPropertyChanged(nameof(TrainerCueVolumeDisplayText));
            OnPropertyChanged(nameof(TrainerHotkeyHoldMsDisplayText));

            await ReloadScanDirectoriesAsync(cancellationToken).ConfigureAwait(true);
            await RefreshSteamApiKeyStatusAsync(cancellationToken).ConfigureAwait(true);
            await Update.InitializeAsync(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public IReadOnlyList<string> GetAllEffectiveScanDirectories() =>
        ScanDirectories.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    partial void OnSelectedCategoryChanged(SettingsCategory value) =>
        OnPropertyChanged(nameof(CurrentCategoryTitle));

    partial void OnCloseWindowToTrayChanged(bool value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(SettingKeyCloseToTray, value ? "true" : "false");
    }

    partial void OnSelectedAfkTimeoutSecChanged(int value)
    {
        if (_suppressAutoSave)
        {
            return;
        }

        _ = _db.SetSettingAsync(AppSettingKeys.LauncherAfkTimeoutSec, value.ToString(CultureInfo.InvariantCulture));
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

    partial void OnSelectedOverlayThemeChanged(OverlayThemeKind value) => PersistOverlaySettingsIfReady();

    partial void OnSelectedOverlayPositionChanged(OverlayPositionKind value) => PersistOverlaySettingsIfReady();

    partial void OnSelectedOverlaySoundPresetChanged(OverlaySoundPreset value)
    {
        OnPropertyChanged(nameof(ShowCustomWavPicker));
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
        OnPropertyChanged(nameof(OverlayVolumeDisplayText));
        PersistOverlaySettingsIfReady();
    }

    partial void OnOverlayMuteSoundChanged(bool value)
    {
        OnPropertyChanged(nameof(OverlayVolumeDisplayText));
        PersistOverlaySettingsIfReady();
    }

    partial void OnSelectedUiThemeChanged(SpawnUiThemeKind value)
    {
        _themeManager.ApplyTheme(value);
        if (!_suppressAutoSave)
        {
            _ = _db.SetSettingAsync(AppSettingKeys.UiTheme, value.ToString());
            StatusFeedbackRequested?.Invoke(value == SpawnUiThemeKind.Dark ? "已切换为极客深色主题" : "已切换为清透浅色主题");
        }
    }

    partial void OnSelectedTrainerAudioModeChanged(TrainerAudioMode value)
    {
        OnPropertyChanged(nameof(ShowTrainerCueVolumeSlider));
        PersistTrainerSettingsIfReady();
    }

    partial void OnTrainerCueVolumePercentChanged(double value)
    {
        OnPropertyChanged(nameof(TrainerCueVolumeDisplayText));
        PersistTrainerSettingsIfReady();
    }

    partial void OnTrainerEnableA0PreMuteChanged(bool value) => PersistTrainerSettingsIfReady();

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
    partial void OnTrainerStrictVanguardModeChanged(bool value) => PersistTrainerSettingsIfReady();

    partial void OnAppliedCustomTrainerSandboxRootChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTrainerSandboxRoot));
        OnPropertyChanged(nameof(HasCustomTrainerSandboxRoot));
    }

    partial void OnDefenderStatusReportChanged(DefenderExclusionStatusReport? value)
    {
        OnPropertyChanged(nameof(DefenderStatusBadgeText));
        OnPropertyChanged(nameof(DefenderStatusMessage));
        OnPropertyChanged(nameof(IsDefenderExcluded));
        OnPropertyChanged(nameof(ShowSmartAppControlCard));
        OnPropertyChanged(nameof(ShowDefenderManualGuidance));
    }

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
            StatusFeedbackRequested?.Invoke("请输入或浏览选择游戏文件夹 / 主程序 (.exe) 路径");
            return;
        }

        if (ManualGameImportRequested is not null)
        {
            await ManualGameImportRequested(raw).ConfigureAwait(true);
            ManualGameImportPathText = string.Empty;
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
            StatusFeedbackRequested?.Invoke("当前有修改器正在运行，请先停止修改器后再切换或迁移沙箱根目录。");
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
            StatusFeedbackRequested?.Invoke(rejectReason ?? "自定义沙箱目录校验未通过");
            return;
        }

        string previousRoot = EffectiveTrainerSandboxRoot;
        try
        {
            var (succeeded, _, message) = await DefenderExclusionAssistant.MigrateSandboxContentsAsync(previousRoot, normalized).ConfigureAwait(true);
            if (!succeeded)
            {
                StatusFeedbackRequested?.Invoke(message);
                return;
            }

            AppliedCustomTrainerSandboxRoot = normalized;
            TrainerCustomSandboxRootInput = normalized;
            DefenderStatusReport = null;
            PersistTrainerSettingsIfReady();
            StatusFeedbackRequested?.Invoke(message);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"迁移修改器沙箱目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ResetDefaultSandboxRootAsync()
    {
        if (_trainerRuntime?.ActiveSession is not null)
        {
            StatusFeedbackRequested?.Invoke("当前有修改器正在运行，请先停止修改器后再恢复默认沙箱根目录。");
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
                    StatusFeedbackRequested?.Invoke(message);
                    return;
                }
            }

            AppliedCustomTrainerSandboxRoot = null;
            TrainerCustomSandboxRootInput = string.Empty;
            DefenderStatusReport = null;
            PersistTrainerSettingsIfReady();
            StatusFeedbackRequested?.Invoke($"已恢复默认修改器沙箱根目录：{defaultRoot}");
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"恢复默认修改器沙箱目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    public async Task InspectDefenderExclusionAsync()
    {
        if (_defenderAssistant is null || IsDefenderWorking)
        {
            return;
        }

        IsDefenderWorking = true;
        try
        {
            DefenderStatusReport = await _defenderAssistant.InspectStatusAsync(EffectiveTrainerSandboxRoot).ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke(DefenderStatusReport.Message);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"检测 Defender 状态失败：{ex.Message}");
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
            StatusFeedbackRequested?.Invoke(DefenderStatusReport.Message);
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"配置 Defender 沙箱排除失败：{ex.Message}");
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
            StatusFeedbackRequested?.Invoke($"已复制单一沙箱目录路径：{EffectiveTrainerSandboxRoot}");
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"复制沙箱路径失败：{ex.Message}");
        }
    }

    [RelayCommand]
    public void OpenDefenderSettings()
    {
        try
        {
            _openExternalUrl(DefenderExclusionAssistant.DefenderExclusionsSettingsUri);
            StatusFeedbackRequested?.Invoke("已尝试打开 Windows 安全中心排除项页面，请粘贴刚刚复制的单一沙箱路径");
        }
        catch
        {
            try
            {
                _openExternalUrl(DefenderExclusionAssistant.DefenderSettingsFallbackUri);
                StatusFeedbackRequested?.Invoke("已打开 Windows 安全中心设置（ms-settings:windowsdefender），请进入病毒和威胁防护 → 管理设置 → 排除项");
            }
            catch (Exception ex)
            {
                StatusFeedbackRequested?.Invoke($"无法打开 Windows 安全中心：{ex.Message}");
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
            StatusFeedbackRequested?.Invoke($"无法打开微软说明页面：{ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RestoreTrainerA0BackupsAsync()
    {
        if (_trainerAudioController is null)
        {
            StatusFeedbackRequested?.Invoke("当前运行环境未挂载修改器音频控制器");
            return;
        }

        try
        {
            await _trainerAudioController.RestoreA0BackupsAsync().ConfigureAwait(true);
            StatusFeedbackRequested?.Invoke("已还原所有 FLiNG 修改器 A0 音频预写备份（.spawn_bak）并清理 SPAWN 新建配置");
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"还原 A0 备份失败：{ex.Message}");
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
        StatusFeedbackRequested?.Invoke($"已移除扫描目录：{entry.Path}");
    }

    [RelayCommand]
    private async Task ScanAllConfiguredDirectoriesAsync()
    {
        var dirs = GetAllEffectiveScanDirectories();
        if (dirs.Count == 0)
        {
            StatusFeedbackRequested?.Invoke("尚未配置默认游戏库目录，请先点击「添加目录」");
            return;
        }

        if (ScanDirectoriesRequested is not null)
        {
            await ScanDirectoriesRequested(dirs).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenDataDirectory()
    {
        try
        {
            _paths.EnsureDirectoriesExist();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.DataRoot}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"打开数据目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenInstallDirectory()
    {
        try
        {
            string dir = SpawnBuildInfo.InstallDirectory;
            if (Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"打开安装目录失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void CopyVersionDiagnostics()
    {
        try
        {
            string info = $"{BuildProductName} | Version={BuildVersionText} | Channel={SpawnBuildInfo.Channel} | Identity={_paths.Identity} | Commit={BuildCommitShaText} | Built={BuildTimeUtcText}";
            Clipboard.SetText(info);
            StatusFeedbackRequested?.Invoke("已复制版本与通道诊断信息到剪贴板");
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"复制失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveCustomSteamApiKeyAsync()
    {
        string trimmed = CustomSteamApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            StatusFeedbackRequested?.Invoke("请输入有效的 Steam Web API Key 后再点击保存");
            return;
        }

        await _steamApiKeyProvider.SaveCustomApiKeyAsync(trimmed).ConfigureAwait(true);
        CustomSteamApiKeyInput = string.Empty;
        await RefreshSteamApiKeyStatusAsync().ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke("已通过当前 Windows 用户 DPAPI 加密保存 Steam Web API Key");
    }

    [RelayCommand]
    private async Task ClearCustomSteamApiKeyAsync()
    {
        await _steamApiKeyProvider.SaveCustomApiKeyAsync(null).ConfigureAwait(true);
        CustomSteamApiKeyInput = string.Empty;
        await RefreshSteamApiKeyStatusAsync().ConfigureAwait(true);
        StatusFeedbackRequested?.Invoke("已清除本机自定义密钥，已恢复默认密钥解析链");
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
                StatusFeedbackRequested?.Invoke("请选择有效且体积不超过 20 MB 的标准 WAV 音频文件");
                return;
            }

            Span<byte> header = stackalloc byte[12];
            using (var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Read(header) < 12
                    || !header[..4].SequenceEqual("RIFF"u8)
                    || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
                {
                    StatusFeedbackRequested?.Invoke("所选文件不是标准 RIFF WAV 音频格式，请选择未压缩 PCM/Float .wav 文件");
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
            PersistOverlaySettingsIfReady();
            StatusFeedbackRequested?.Invoke($"已导入自定义跳杯音效（{Path.GetFileName(sourcePath)} -> config\\custom_toast.wav）");
        }
        catch (Exception ex)
        {
            StatusFeedbackRequested?.Invoke($"导入自定义 WAV 音效失败：{ex.Message}");
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
        StatusFeedbackRequested?.Invoke(ok
            ? "已向 OverlayHost 发送实时测试成就弹窗（不抢夺当前焦点）"
            : "未能连接 OverlayHost 管道，请确认编译产物就绪");
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
        StatusFeedbackRequested?.Invoke(ok
            ? "已触发 PS5 金杯 + 100% 白金奖杯双连发预览！"
            : "未能连接 OverlayHost 管道");
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

        foreach (string builtIn in _paths.GetDefaultScanDirectories())
        {
            ScanDirectories.Add(new ScanDirectoryEntry(builtIn, "工作区 .local\\games（自动发现）", IsRemovable: false));
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

    private void PersistTrainerSettingsIfReady()
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
            StatusFeedbackRequested?.Invoke($"已刷新音频播放设备列表（共发现 {snap.ActiveEndpoints.Count} 个活跃输出端点）");
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
        SteamClientSoundProbeResult probe = SteamClientSoundLocator.Probe();
        IsSteamSoundDetected = !string.IsNullOrWhiteSpace(probe.EffectiveSoundPath);
        SteamSoundStatusText = probe.StatusMessage;
    }

    private void RefreshAntiCheatSummary(AntiCheatGuardSnapshot snap)
    {
        AntiCheatStatusSummary = snap.Phase switch
        {
            AntiCheatGuardPhase.ProtectedRunning => "受保护网游运行中 · 高敏子系统已物理熔断",
            AntiCheatGuardPhase.ResidualGuardWatch => "等待反作弊伴生守护进程完全退出…",
            AntiCheatGuardPhase.CooldownLocked => $"反作弊 15s 余温安全冷却中（剩余 {snap.RemainingCooldownSeconds:0.0}s）",
            _ => "Tier 0 安全就绪 · 纯净宿主零敏感导入表",
        };
    }
}
