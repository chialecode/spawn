using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Spawn.Contracts;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;

namespace Spawn.Host.Views;

public partial class MainWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;
    private const uint MonitorDefaultToNull = 0x00000000;

    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIconService _trayService;
    private readonly Action _requestApplicationShutdown;
    private readonly HashSet<Popup> _openPopups = [];
    private readonly DispatcherTimer _placementSaveDebounceTimer;
    private HwndSource? _hwndSource;
    private bool _isManualImportImeComposing;
    private bool _isLibrarySearchImeComposing;
    private bool _isAchievementSidebarSearchImeComposing;
    private bool _isAchievementListSearchImeComposing;
    private bool _isTrainerSearchImeComposing;
    private bool _isTrainerOverrideSearchImeComposing;
    private bool _isSaveSearchImeComposing;
    private bool _isSyncingSteamApiKeyPassword;
    private bool _isApplicationExiting;
    private bool _restoreMaximizedOnLoad;
    private bool _isMaxButtonNcPressed;
    private bool _isMaxButtonNcHovered;

    public event Action? HiddenToTray;

    public MainWindow(
        MainWindowViewModel viewModel,
        TrayIconService trayService,
        Action requestApplicationShutdown)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trayService);
        ArgumentNullException.ThrowIfNull(requestApplicationShutdown);

        _viewModel = viewModel;
        _trayService = trayService;
        _requestApplicationShutdown = requestApplicationShutdown;
        DataContext = _viewModel;

        InitializeComponent();

        Title = SpawnBuildInfo.DisplayProductName;
        var dynamicIcon = TrayIconService.CreateWindowIconImage(SpawnBuildInfo.IsDevOrSourceChannel);
        if (dynamicIcon is not null)
        {
            Icon = dynamicIcon;
        }

        _viewModel.ConfirmPromptHandler = ShowNativeConfirmDialog;
        _viewModel.EditGamePropertiesHandler = ShowGamePropertiesDialog;

        _placementSaveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _placementSaveDebounceTimer.Tick += (_, _) =>
        {
            _placementSaveDebounceTimer.Stop();
            SaveWindowPlacementNow();
        };

        RegisterImeHandlers(SettingsManualImportTextBox, v => _isManualImportImeComposing = v);
        RegisterImeHandlers(LibrarySidebarSearchTextBox, v => _isLibrarySearchImeComposing = v);
        RegisterImeHandlers(AchievementSidebarSearchTextBox, v => _isAchievementSidebarSearchImeComposing = v);
        RegisterImeHandlers(TrainerSidebarSearchTextBox, v => _isTrainerSearchImeComposing = v);
        RegisterImeHandlers(SaveSidebarSearchTextBox, v => _isSaveSearchImeComposing = v);

        ApplySavedWindowPlacement();

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        LocationChanged += OnWindowLocationChanged;
        Closed += OnWindowClosed;
        _viewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public void PrepareForApplicationExit()
    {
        _isApplicationExiting = true;
    }

    public void BringToFrontAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        SyncDwmWindowFrameTheme();
        UpdateMaximizedLayoutChrome();
        _ = Activate();
        Topmost = true;
        Topmost = false;
        _ = Focus();
    }

    public static WindowPlacementDto ClampWindowPlacementToWorkArea(
        WindowPlacementDto saved,
        Rect workArea,
        double minWidth = 760,
        double minHeight = 560)
    {
        double safeMinW = Math.Max(320, minWidth);
        double safeMinH = Math.Max(240, minHeight);

        double maxW = workArea.Width > 0 ? workArea.Width : 1280;
        double maxH = workArea.Height > 0 ? workArea.Height : 720;

        double width = double.IsFinite(saved.Width) ? Math.Clamp(saved.Width, Math.Min(safeMinW, maxW), maxW) : Math.Min(1220, maxW);
        double height = double.IsFinite(saved.Height) ? Math.Clamp(saved.Height, Math.Min(safeMinH, maxH), maxH) : Math.Min(780, maxH);

        double left = double.IsFinite(saved.Left) ? saved.Left : workArea.Left + (workArea.Width - width) / 2.0;
        double top = double.IsFinite(saved.Top) ? saved.Top : workArea.Top + (workArea.Height - height) / 2.0;

        // Keep at least the title bar and window frame inside the target monitor's working area.
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));

        return new WindowPlacementDto(left, top, width, height, saved.IsMaximized);
    }

    public static bool IsPointInsideMaximizeButton(Point localPoint, Size buttonSize, bool isVisible) =>
        isVisible
        && buttonSize.Width > 0
        && buttonSize.Height > 0
        && localPoint.X >= 0
        && localPoint.Y >= 0
        && localPoint.X <= buttonSize.Width
        && localPoint.Y <= buttonSize.Height;

    private static void RegisterImeHandlers(TextBox textBox, Action<bool> setComposing)
    {
        TextCompositionManager.AddPreviewTextInputStartHandler(textBox, (_, _) => setComposing(true));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(textBox, (_, _) => setComposing(true));
        TextCompositionManager.AddPreviewTextInputHandler(textBox, (_, _) => setComposing(false));
    }

    private void ApplySavedWindowPlacement()
    {
        WindowPlacementDto? saved = _viewModel.Settings.SavedWindowPlacement;
        if (saved is null || !double.IsFinite(saved.Width) || !double.IsFinite(saved.Height))
        {
            return;
        }

        Rect targetWorkArea = ResolveTargetWorkAreaDip(saved);
        WindowPlacementDto clamped = ClampWindowPlacementToWorkArea(saved, targetWorkArea, MinWidth, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = clamped.Left;
        Top = clamped.Top;
        Width = clamped.Width;
        Height = clamped.Height;
        _restoreMaximizedOnLoad = clamped.IsMaximized;
    }

    private Rect ResolveTargetWorkAreaDip(WindowPlacementDto saved)
    {
        Rect fallback = SystemParameters.WorkArea;
        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            var rectPx = new NativeRect
            {
                Left = (int)Math.Round(saved.Left * scaleX),
                Top = (int)Math.Round(saved.Top * scaleY),
                Right = (int)Math.Round((saved.Left + Math.Max(saved.Width, MinWidth)) * scaleX),
                Bottom = (int)Math.Round((saved.Top + Math.Max(saved.Height, MinHeight)) * scaleY),
            };

            nint hMonitor = MonitorFromRect(ref rectPx, MonitorDefaultToNull);
            if (hMonitor == nint.Zero)
            {
                return fallback;
            }

            var info = new NativeMonitorInfo
            {
                CbSize = Marshal.SizeOf<NativeMonitorInfo>(),
            };
            if (!GetMonitorInfoW(hMonitor, ref info))
            {
                return fallback;
            }

            double left = info.RcWork.Left / scaleX;
            double top = info.RcWork.Top / scaleY;
            double width = (info.RcWork.Right - info.RcWork.Left) / scaleX;
            double height = (info.RcWork.Bottom - info.RcWork.Top) / scaleY;
            if (width >= 320 && height >= 240)
            {
                return new Rect(left, top, width, height);
            }
        }
        catch
        {
            // Fall back to primary monitor work area.
        }

        return fallback;
    }

    private void ScheduleWindowPlacementSave()
    {
        if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        _placementSaveDebounceTimer.Stop();
        _placementSaveDebounceTimer.Start();
    }

    private void SaveWindowPlacementNow()
    {
        if (!IsLoaded)
        {
            return;
        }

        bool isMaximized = WindowState == WindowState.Maximized;
        Rect bounds = isMaximized && RestoreBounds != Rect.Empty && RestoreBounds.Width > 0 && RestoreBounds.Height > 0
            ? RestoreBounds
            : new Rect(Left, Top, Width, Height);

        if (!double.IsFinite(bounds.Left)
            || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width)
            || !double.IsFinite(bounds.Height)
            || bounds.Width < 200
            || bounds.Height < 200)
        {
            return;
        }

        var dto = new WindowPlacementDto(bounds.Left, bounds.Top, bounds.Width, bounds.Height, isMaximized);
        _ = _viewModel.Settings.SaveWindowPlacementAsync(dto);
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        SyncDwmWindowFrameTheme();

        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowWndProc);
        }
    }

    private nint WindowWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
            {
                if (IsScreenPointOnMaximizeButton(lParam))
                {
                    handled = true;
                    return (nint)HtMaxButton;
                }

                break;
            }

            case WmNcMouseMove:
            {
                bool onMax = wParam == (nint)HtMaxButton;
                SetMaximizeButtonNcHover(onMax);
                if (onMax)
                {
                    handled = true;
                }

                break;
            }

            case WmNcMouseLeave:
            {
                _isMaxButtonNcPressed = false;
                SetMaximizeButtonNcHover(false);
                break;
            }

            case WmNcLButtonDown:
            {
                if (wParam == (nint)HtMaxButton)
                {
                    _isMaxButtonNcPressed = true;
                    handled = true;
                    return nint.Zero;
                }

                break;
            }

            case WmNcLButtonUp:
            {
                if (wParam == (nint)HtMaxButton && _isMaxButtonNcPressed)
                {
                    _isMaxButtonNcPressed = false;
                    SetMaximizeButtonNcHover(false);
                    ToggleMaximizeRestore();
                    handled = true;
                    return nint.Zero;
                }

                _isMaxButtonNcPressed = false;
                break;
            }
        }

        return nint.Zero;
    }

    private bool IsScreenPointOnMaximizeButton(nint lParam)
    {
        if (MaximizeRestoreButton is null || !MaximizeRestoreButton.IsVisible)
        {
            return false;
        }

        try
        {
            long lp = lParam.ToInt64();
            int screenX = unchecked((short)(lp & 0xFFFF));
            int screenY = unchecked((short)((lp >> 16) & 0xFFFF));
            Point local = MaximizeRestoreButton.PointFromScreen(new Point(screenX, screenY));
            return IsPointInsideMaximizeButton(
                local,
                new Size(MaximizeRestoreButton.ActualWidth, MaximizeRestoreButton.ActualHeight),
                MaximizeRestoreButton.IsVisible);
        }
        catch
        {
            return false;
        }
    }

    private void SetMaximizeButtonNcHover(bool hovered)
    {
        if (_isMaxButtonNcHovered == hovered || MaximizeRestoreButton is null)
        {
            return;
        }

        _isMaxButtonNcHovered = hovered;
        if (hovered)
        {
            MaximizeRestoreButton.Background = TryFindResource("ItemHoverBrush") as Brush ?? Brushes.Transparent;
        }
        else
        {
            MaximizeRestoreButton.ClearValue(BackgroundProperty);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _placementSaveDebounceTimer.Stop();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WindowWndProc);
            _hwndSource = null;
        }

        LocationChanged -= OnWindowLocationChanged;
        _viewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedUiTheme), StringComparison.Ordinal))
        {
            SyncDwmWindowFrameTheme();
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedCategory), StringComparison.Ordinal))
        {
            SettingsContentScrollViewer?.ScrollToTop();
        }

        if (string.Equals(e.PropertyName, nameof(SettingsViewModel.CustomSteamApiKeyInput), StringComparison.Ordinal)
            || string.Equals(e.PropertyName, nameof(SettingsViewModel.ShowCustomSteamApiKey), StringComparison.Ordinal))
        {
            SyncSteamApiKeyPasswordBoxFromViewModel();
        }
    }

    private void SyncSteamApiKeyPasswordBoxFromViewModel()
    {
        if (_isSyncingSteamApiKeyPassword || SteamApiKeyPasswordBox is null)
        {
            return;
        }

        string expected = _viewModel.Settings.CustomSteamApiKeyInput ?? string.Empty;
        if (!string.Equals(SteamApiKeyPasswordBox.Password, expected, StringComparison.Ordinal))
        {
            _isSyncingSteamApiKeyPassword = true;
            try
            {
                SteamApiKeyPasswordBox.Password = expected;
            }
            finally
            {
                _isSyncingSteamApiKeyPassword = false;
            }
        }
    }

    private void OnSteamApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingSteamApiKeyPassword || sender is not PasswordBox pb)
        {
            return;
        }

        _isSyncingSteamApiKeyPassword = true;
        try
        {
            _viewModel.Settings.CustomSteamApiKeyInput = pb.Password;
        }
        finally
        {
            _isSyncingSteamApiKeyPassword = false;
        }
    }

    private void SyncDwmWindowFrameTheme()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            _ = DwmWindowFrameHelper.TrySyncImmersiveDarkMode(hwnd, _viewModel.Settings.SelectedUiTheme);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_restoreMaximizedOnLoad)
        {
            _restoreMaximizedOnLoad = false;
            WindowState = WindowState.Maximized;
        }

        _viewModel.UpdateViewportWidth(ActualWidth);
        UpdateMaximizedLayoutChrome();
        SyncSteamApiKeyPasswordBoxFromViewModel();
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        ScheduleWindowPlacementSave();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _placementSaveDebounceTimer.Stop();
        SaveWindowPlacementNow();

        if (_isApplicationExiting)
        {
            return;
        }

        if (_viewModel.Settings.CloseWindowToTray && _trayService.IsActive)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            if (!_viewModel.Settings.HasShownFirstMinimizeToTrayNotice)
            {
                _ = _viewModel.Settings.MarkFirstMinimizeToTrayNotifiedAsync();
                _trayService.ShowNotification(
                    "SPAWN 已最小化至系统托盘",
                    "后台将继续保持游戏追踪与成就监听，右键托盘图标可重新打开主界面或彻底退出。",
                    isError: false);
            }

            return;
        }

        if (_viewModel.HasAnyRunningGame || _viewModel.HasAnyRunningTrainer)
        {
            var runningDetails = new List<string>(2);
            if (_viewModel.HasAnyRunningGame)
            {
                runningDetails.Add("• 有游戏正在运行（退出后游戏本体不受影响，但会中断本次游玩时长统计与跳杯监听）");
            }

            if (_viewModel.HasAnyRunningTrainer)
            {
                runningDetails.Add("• 有托管的外部修改器正在运行（退出 SPAWN 将立即自动终止该修改器进程树）");
            }

            bool confirmExit = ShowNativeConfirmDialog(new ShellConfirmPrompt(
                Title: "确认彻底退出 SPAWN？",
                Message: $"当前检测到以下活动任务：\n{string.Join("\n", runningDetails)}\n\n确定要现在彻底退出 SPAWN 吗？",
                ConfirmLabel: "彻底退出",
                CancelLabel: "取消",
                IsDanger: true));
            if (!confirmExit)
            {
                e.Cancel = true;
                return;
            }
        }

        // User opted out of minimize-to-tray -> trigger full graceful exit
        e.Cancel = true;
        _requestApplicationShutdown();
    }

    private void OnTitleBarDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject sourceObj && IsInsideInteractiveControl(sourceObj))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // Ignore transient mouse release race during DragMove.
            }
        }
    }

    private static bool IsInsideInteractiveControl(DependencyObject? current)
    {
        while (current is not null and not Window)
        {
            if (current is ButtonBase or TextBox or PasswordBox or ComboBox or Slider)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void OnMinimizeButtonClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestoreButtonClick(object sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void OnCloseButtonClick(object sender, RoutedEventArgs e) =>
        Close();

    private void OnOpenUpdateSettingsFromBannerClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ActiveSection = PrimaryNavSection.Settings;
        _viewModel.Settings.SelectedCategory = SettingsCategory.Updates;
    }

    private void OnNarrowSidebarBackdropClick(object sender, MouseButtonEventArgs e)
    {
        _viewModel.IsNarrowSidebarOpen = false;
        e.Handled = true;
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizedLayoutChrome();
        ScheduleWindowPlacementSave();
    }

    private void UpdateMaximizedLayoutChrome()
    {
        if (WindowState == WindowState.Maximized)
        {
            Thickness resize = SystemParameters.WindowResizeBorderThickness;
            WindowOuterBorder.Padding = new Thickness(
                resize.Left + 3,
                resize.Top + 3,
                resize.Right + 3,
                resize.Bottom + 3);
            WindowOuterBorder.BorderThickness = new Thickness(0);
            MaximizeGlyphPath.Data = Geometry.Parse("M2.2,0 H10 V7.8 H7.8 M0,2.2 H7.8 V10 H0 Z");
        }
        else
        {
            WindowOuterBorder.Padding = new Thickness(0);
            WindowOuterBorder.BorderThickness = new Thickness(1);
            MaximizeGlyphPath.Data = Geometry.Parse("M0,0 H9.5 V9.5 H0 Z");
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _viewModel.UpdateViewportWidth(e.NewSize.Width);
        ScheduleWindowPlacementSave();
    }

    private async void OnSettingsManualImportTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isManualImportImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter && !_isManualImportImeComposing)
        {
            e.Handled = true;
            await _viewModel.Settings.ImportManualGamePathAsync().ConfigureAwait(true);
            return;
        }

        _isManualImportImeComposing = false;
    }

    private void OnLibrarySidebarSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isLibrarySearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_isLibrarySearchImeComposing)
            {
                return;
            }

            e.Handled = true;
            var matches = _viewModel.VisibleLibraryGroups.SelectMany(g => g.Items).Take(2).ToList();
            if (matches.Count == 1)
            {
                _viewModel.SelectLibraryGameCommand.Execute(matches[0]);
            }

            return;
        }

        if (e.Key == Key.Escape && !_isLibrarySearchImeComposing && !string.IsNullOrEmpty(_viewModel.LibrarySidebarSearchText))
        {
            _viewModel.LibrarySidebarSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        _isLibrarySearchImeComposing = false;
    }

    private void OnAchievementSidebarSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isAchievementSidebarSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_isAchievementSidebarSearchImeComposing)
            {
                return;
            }

            e.Handled = true;
            if (_viewModel.AchievementSidebarGames.Count == 1)
            {
                _viewModel.SelectAchievementSidebarGameCommand.Execute(_viewModel.AchievementSidebarGames[0]);
            }

            return;
        }

        if (e.Key == Key.Escape && !_isAchievementSidebarSearchImeComposing && !string.IsNullOrEmpty(_viewModel.AchievementSidebarSearchText))
        {
            _viewModel.AchievementSidebarSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        _isAchievementSidebarSearchImeComposing = false;
    }

    private void OnAchievementListSearchLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is not string)
        {
            tb.Tag = "ImeRegistered";
            RegisterImeHandlers(tb, v => _isAchievementListSearchImeComposing = v);
        }
    }

    private void OnAchievementListSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isAchievementListSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Escape && !_isAchievementListSearchImeComposing && !string.IsNullOrEmpty(_viewModel.AchievementSearchText))
        {
            _viewModel.ClearAchievementSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }

        _isAchievementListSearchImeComposing = false;
    }

    private void OnTrainerSearchOverrideLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is not string)
        {
            tb.Tag = "ImeRegistered";
            RegisterImeHandlers(tb, v => _isTrainerOverrideSearchImeComposing = v);
        }
    }

    private void OnTrainerSearchOverridePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isTrainerOverrideSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_isTrainerOverrideSearchImeComposing)
            {
                return;
            }

            e.Handled = true;
            if (!_viewModel.Trainer.IsBusy && _viewModel.Trainer.MatchTrainersCommand.CanExecute(null))
            {
                _viewModel.Trainer.MatchTrainersCommand.Execute(null);
            }

            return;
        }

        _isTrainerOverrideSearchImeComposing = false;
    }

    private void OnTrainerSidebarSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isTrainerSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_isTrainerSearchImeComposing)
            {
                return;
            }

            e.Handled = true;
            if (_viewModel.TrainerSidebarGames.Count == 1)
            {
                _viewModel.SelectTrainerSidebarGameCommand.Execute(_viewModel.TrainerSidebarGames[0]);
            }

            return;
        }

        if (e.Key == Key.Escape && !_isTrainerSearchImeComposing && !string.IsNullOrEmpty(_viewModel.TrainerSidebarSearchText))
        {
            _viewModel.TrainerSidebarSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        _isTrainerSearchImeComposing = false;
    }

    private void OnSaveSidebarSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isSaveSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_isSaveSearchImeComposing)
            {
                return;
            }

            e.Handled = true;
            if (_viewModel.SaveSidebarGames.Count == 1)
            {
                _viewModel.SelectSaveSidebarGameCommand.Execute(_viewModel.SaveSidebarGames[0]);
            }

            return;
        }

        if (e.Key == Key.Escape && !_isSaveSearchImeComposing && !string.IsNullOrEmpty(_viewModel.SaveSidebarSearchText))
        {
            _viewModel.SaveSidebarSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        _isSaveSearchImeComposing = false;
    }

    private void OnManagedPopupOpened(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
        {
            _openPopups.Add(popup);
        }
    }

    private void OnManagedPopupClosed(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
        {
            _openPopups.Remove(popup);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            bool closedAny = false;
            if (GameHeaderMoreToggle.IsChecked == true)
            {
                GameHeaderMoreToggle.IsChecked = false;
                closedAny = true;
            }

            if (GuardRailToggle?.IsChecked == true)
            {
                GuardRailToggle.IsChecked = false;
                closedAny = true;
            }

            if (_openPopups.Count > 0)
            {
                foreach (Popup popup in _openPopups.ToArray())
                {
                    if (popup.IsOpen)
                    {
                        popup.IsOpen = false;
                        if (popup.PlacementTarget is ToggleButton toggle)
                        {
                            toggle.IsChecked = false;
                        }

                        closedAny = true;
                    }
                }
            }

            if (!closedAny && _viewModel.IsDrawerSidebarOpen)
            {
                _viewModel.IsNarrowSidebarOpen = false;
                closedAny = true;
            }

            if (closedAny)
            {
                e.Handled = true;
            }
        }
    }

    private void OnAnyPopupMenuItemClick(object sender, RoutedEventArgs e)
    {
        Popup? owningPopup = null;
        if (sender is DependencyObject dep)
        {
            DependencyObject? cur = dep;
            while (cur is not null)
            {
                if (cur is Popup popup)
                {
                    owningPopup = popup;
                    break;
                }

                cur = LogicalTreeHelper.GetParent(cur) ?? (cur is Visual ? VisualTreeHelper.GetParent(cur) : null);
            }
        }

        // WPF Button fires Click BEFORE executing ICommand. Defer closing the Popup until Background priority
        // so the bound Command always executes completely first!
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (GameHeaderMoreToggle.IsChecked == true)
                {
                    GameHeaderMoreToggle.IsChecked = false;
                }

                if (GuardRailToggle?.IsChecked == true)
                {
                    GuardRailToggle.IsChecked = false;
                }

                if (owningPopup is not null)
                {
                    owningPopup.IsOpen = false;
                    if (owningPopup.PlacementTarget is ToggleButton toggle)
                    {
                        toggle.IsChecked = false;
                    }
                }
            }));
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DragDropOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
        {
            await _viewModel.ImportDroppedPathsAsync(dropped).ConfigureAwait(true);
        }
    }

    // -------------------------------------------------------------------------
    // Pure-WPF Native Modal Dialogs (Theme-Aware Confirm & Game Properties)
    // -------------------------------------------------------------------------

    private Brush ResolveThemeBrush(string key, Color fallbackColor) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallbackColor);

    private Style? ResolveStyle(string key) => TryFindResource(key) as Style;

    private Window CreateThemedModalWindow(string title, double width)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ShowInTaskbar = false,
            Background = ResolveThemeBrush("PageBackgroundBrush", Colors.White),
            Foreground = ResolveThemeBrush("PrimaryTextBrush", Colors.Black),
            FontFamily = TryFindResource("SpawnUiFont") as FontFamily ?? FontFamily,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };

        if (IsLoaded && IsVisible)
        {
            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.SourceInitialized += (_, _) =>
        {
            nint h = new WindowInteropHelper(dialog).Handle;
            if (h != nint.Zero)
            {
                _ = DwmWindowFrameHelper.TrySyncImmersiveDarkMode(h, _viewModel.Settings.SelectedUiTheme);
            }
        };

        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                dialog.DialogResult = false;
                dialog.Close();
                args.Handled = true;
            }
        };

        return dialog;
    }

    private bool ShowNativeConfirmDialog(ShellConfirmPrompt prompt)
    {
        var dialog = CreateThemedModalWindow(prompt.Title, width: 460);

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };

        root.Children.Add(new TextBlock
        {
            Text = prompt.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResolveThemeBrush("PrimaryTextBrush", Colors.Black),
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new TextBlock
        {
            Text = prompt.Message,
            FontSize = 13,
            LineHeight = 20,
            Foreground = ResolveThemeBrush("SecondaryTextBrush", Colors.DimGray),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 20),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var cancelBtn = new Button
        {
            Content = prompt.CancelLabel,
            IsCancel = true,
            IsDefault = prompt.IsDanger,
            Margin = new Thickness(0, 0, 10, 0),
            Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
        };
        cancelBtn.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        var confirmBtn = new Button
        {
            Content = prompt.ConfirmLabel,
            IsDefault = !prompt.IsDanger,
            Style = ResolveStyle(prompt.IsDanger ? "DangerPrimaryCapsuleButtonStyle" : "PrimaryCapsuleButtonStyle"),
        };
        confirmBtn.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };

        actions.Children.Add(cancelBtn);
        actions.Children.Add(confirmBtn);
        root.Children.Add(actions);

        dialog.Content = root;
        return dialog.ShowDialog() == true;
    }

    private GamePropertiesEditResult? ShowGamePropertiesDialog(GamePropertiesEditRequest req)
    {
        var dialog = CreateThemedModalWindow($"游戏属性与启动设置 · {req.Title}", width: 580);
        FontFamily monoFont = TryFindResource("SpawnMonoFont") as FontFamily ?? FontFamily;
        Brush secondaryBrush = ResolveThemeBrush("SecondaryTextBrush", Colors.DimGray);
        Brush primaryBrush = ResolveThemeBrush("PrimaryTextBrush", Colors.Black);
        Brush inputBg = ResolveThemeBrush("CapsuleInputBackgroundBrush", Colors.White);
        Brush borderBrush = ResolveThemeBrush("ControlBorderBrush", Colors.LightGray);
        Brush accentBrush = ResolveThemeBrush("AccentBrush", Colors.RoyalBlue);
        Brush dangerBrush = ResolveThemeBrush("Tier23BadgeForegroundBrush", Colors.Firebrick);

        TextBox CreateInput(string initialText, bool mono = false) => new()
        {
            Text = initialText,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12.5,
            FontFamily = mono ? monoFont : dialog.FontFamily,
            Background = inputBg,
            Foreground = primaryBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = accentBrush,
        };

        TextBlock CreateLabel(string text, double topMargin = 12) => new()
        {
            Text = text,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = primaryBrush,
            Margin = new Thickness(0, topMargin, 0, 4),
        };

        var root = new StackPanel { Margin = new Thickness(22, 16, 22, 20) };

        root.Children.Add(new TextBlock
        {
            Text = "游戏属性与启动设置",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = primaryBrush,
        });

        // 1. Title
        root.Children.Add(CreateLabel("游戏标题", topMargin: 12));
        TextBox titleBox = CreateInput(req.Title);
        root.Children.Add(titleBox);

        // 2. Main Executable path + candidate dropdown + Browse
        root.Children.Add(CreateLabel("主程序可执行文件（相对安装目录或绝对路径）"));
        var exeRow = new Grid();
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        exeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBox exeBox = CreateInput(req.ExecutableRelPath, mono: true);
        Grid.SetColumn(exeBox, 0);
        exeRow.Children.Add(exeBox);

        var browseExeBtn = new Button
        {
            Content = "浏览 .exe",
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
        };
        browseExeBtn.Click += (_, _) =>
        {
            var ofd = new OpenFileDialog
            {
                Title = "选择游戏主程序 (.exe)",
                Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(req.InstallDir) ? req.InstallDir : string.Empty,
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(dialog) == true)
            {
                string picked = ofd.FileName;
                if (Directory.Exists(req.InstallDir)
                    && picked.StartsWith(req.InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    exeBox.Text = Path.GetRelativePath(req.InstallDir, picked).Replace('/', '\\');
                }
                else
                {
                    exeBox.Text = picked;
                }
            }
        };
        Grid.SetColumn(browseExeBtn, 1);
        exeRow.Children.Add(browseExeBtn);
        root.Children.Add(exeRow);

        if (req.CandidateExecutables.Count > 1)
        {
            var candidateRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            candidateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            candidateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var candidateHint = new TextBlock
            {
                Text = "快速切换目录内候选：",
                FontSize = 11.5,
                Foreground = secondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(candidateHint, 0);
            candidateRow.Children.Add(candidateHint);

            var candidateCombo = new ComboBox
            {
                Height = 28,
                FontFamily = monoFont,
                FontSize = 11.5,
                ItemsSource = req.CandidateExecutables,
                SelectedItem = req.CandidateExecutables.FirstOrDefault(
                    c => string.Equals(c, req.ExecutableRelPath, StringComparison.OrdinalIgnoreCase)),
            };
            candidateCombo.SelectionChanged += (_, _) =>
            {
                if (candidateCombo.SelectedItem is string pickedRel && !string.IsNullOrWhiteSpace(pickedRel))
                {
                    exeBox.Text = pickedRel;
                }
            };
            Grid.SetColumn(candidateCombo, 1);
            candidateRow.Children.Add(candidateCombo);
            root.Children.Add(candidateRow);
        }

        // 3. Launch Arguments
        root.Children.Add(CreateLabel("自定义启动参数（可选，如 -windowed -skipintro）"));
        TextBox argsBox = CreateInput(req.LaunchArguments, mono: true);
        root.Children.Add(argsBox);

        // 4. Steam AppID & GOG Game ID
        var idsGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        idsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        idsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        idsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var steamPanel = new StackPanel();
        steamPanel.Children.Add(CreateLabel("Steam AppID（用于匹配成就与封面）", topMargin: 0));
        TextBox steamIdBox = CreateInput(
            req.SteamAppId.HasValue ? req.SteamAppId.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            mono: true);
        steamPanel.Children.Add(steamIdBox);
        Grid.SetColumn(steamPanel, 0);
        idsGrid.Children.Add(steamPanel);

        var gogPanel = new StackPanel();
        gogPanel.Children.Add(CreateLabel("GOG Game ID（可选）", topMargin: 0));
        TextBox gogIdBox = CreateInput(req.GogGameId ?? string.Empty, mono: true);
        gogPanel.Children.Add(gogIdBox);
        Grid.SetColumn(gogPanel, 2);
        idsGrid.Children.Add(gogPanel);
        root.Children.Add(idsGrid);

        // 5. Custom Cover Image & Icon Image
        Grid CreateImagePickerRow(string initialPath, out TextBox box)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBox tb = CreateInput(initialPath, mono: true);
            Grid.SetColumn(tb, 0);
            g.Children.Add(tb);

            var browseBtn = new Button
            {
                Content = "浏览",
                Height = 32,
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(8, 0, 0, 0),
                Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
            };
            browseBtn.Click += (_, _) =>
            {
                var ofd = new OpenFileDialog
                {
                    Title = "选择本地图片",
                    Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.webp;*.ico;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.ico;*.bmp|所有文件 (*.*)|*.*",
                    CheckFileExists = true,
                };
                if (ofd.ShowDialog(dialog) == true)
                {
                    tb.Text = ofd.FileName;
                }
            };
            Grid.SetColumn(browseBtn, 1);
            g.Children.Add(browseBtn);

            var clearBtn = new Button
            {
                Content = "清空",
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(6, 0, 0, 0),
                Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
            };
            clearBtn.Click += (_, _) => tb.Text = string.Empty;
            Grid.SetColumn(clearBtn, 2);
            g.Children.Add(clearBtn);

            box = tb;
            return g;
        }

        root.Children.Add(CreateLabel("自定义本地封面图（可选）"));
        root.Children.Add(CreateImagePickerRow(req.CoverImagePath ?? string.Empty, out TextBox coverBox));

        root.Children.Add(CreateLabel("自定义本地图标（可选）"));
        root.Children.Add(CreateImagePickerRow(req.IconPath ?? string.Empty, out TextBox iconBox));

        // 6. Install Directory (read-only + Open Folder)
        root.Children.Add(CreateLabel("安装目录"));
        var dirRow = new Grid();
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dirText = new TextBlock
        {
            Text = req.InstallDir,
            FontFamily = monoFont,
            FontSize = 11.5,
            Foreground = secondaryBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(dirText, 0);
        dirRow.Children.Add(dirText);

        var openDirBtn = new Button
        {
            Content = "打开目录",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 12,
            Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
        };
        openDirBtn.Click += (_, _) =>
        {
            if (Directory.Exists(req.InstallDir))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{req.InstallDir}\"",
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // Ignore shell open error.
                }
            }
        };
        Grid.SetColumn(openDirBtn, 1);
        dirRow.Children.Add(openDirBtn);
        root.Children.Add(dirRow);

        var validationText = new TextBlock
        {
            FontSize = 12,
            Foreground = dangerBrush,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        root.Children.Add(validationText);

        GamePropertiesEditResult? result = null;
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0),
            Style = ResolveStyle("SecondaryCapsuleButtonStyle"),
        };
        cancelBtn.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        var saveBtn = new Button
        {
            Content = "保存设置",
            IsDefault = true,
            Style = ResolveStyle("PrimaryCapsuleButtonStyle"),
        };
        saveBtn.Click += (_, _) =>
        {
            string titleVal = titleBox.Text.Trim();
            string exeVal = exeBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(titleVal))
            {
                validationText.Text = "请输入有效的游戏标题。";
                validationText.Visibility = Visibility.Visible;
                return;
            }

            if (string.IsNullOrWhiteSpace(exeVal))
            {
                validationText.Text = "请指定游戏主程序 .exe 路径。";
                validationText.Visibility = Visibility.Visible;
                return;
            }

            int? parsedSteamId = null;
            string rawSteamId = steamIdBox.Text.Trim();
            if (!string.IsNullOrEmpty(rawSteamId))
            {
                if (!int.TryParse(rawSteamId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sid) || sid <= 0)
                {
                    validationText.Text = "Steam AppID 必须为正整数（留空则清除）。";
                    validationText.Visibility = Visibility.Visible;
                    return;
                }

                parsedSteamId = sid;
            }

            result = new GamePropertiesEditResult(
                Title: titleVal,
                ExecutableRelPath: exeVal,
                LaunchArguments: argsBox.Text.Trim(),
                SteamAppId: parsedSteamId,
                GogGameId: gogIdBox.Text.Trim(),
                CoverImagePath: coverBox.Text.Trim(),
                IconPath: iconBox.Text.Trim());
            dialog.DialogResult = true;
            dialog.Close();
        };

        footer.Children.Add(cancelBtn);
        footer.Children.Add(saveBtn);
        root.Children.Add(footer);

        dialog.Content = root;
        return dialog.ShowDialog() == true ? result : null;
    }

    // -------------------------------------------------------------------------
    // Benign Monitor Geometry Win32 Interop (Display Work Area Validation Only)
    // -------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int CbSize;
        public NativeRect RcMonitor;
        public NativeRect RcWork;
        public uint DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref NativeMonitorInfo lpmi);
}
