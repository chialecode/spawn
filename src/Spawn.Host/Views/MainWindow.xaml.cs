using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Spawn.Contracts;
using Spawn.Host.Helpers;
using Spawn.Host.Services;
using Spawn.Host.ViewModels;

namespace Spawn.Host.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIconService _trayService;
    private readonly Action _requestApplicationShutdown;
    private readonly HashSet<Popup> _openPopups = [];
    private bool _isManualImportImeComposing;
    private bool _isTrainerSearchImeComposing;
    private bool _isApplicationExiting;

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

        TextCompositionManager.AddPreviewTextInputStartHandler(SettingsManualImportTextBox, (_, _) => _isManualImportImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(SettingsManualImportTextBox, (_, _) => _isManualImportImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(SettingsManualImportTextBox, (_, _) => _isManualImportImeComposing = false);

        TextCompositionManager.AddPreviewTextInputStartHandler(TrainerSidebarSearchTextBox, (_, _) => _isTrainerSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(TrainerSidebarSearchTextBox, (_, _) => _isTrainerSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(TrainerSidebarSearchTextBox, (_, _) => _isTrainerSearchImeComposing = false);

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
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

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        SyncDwmWindowFrameTheme();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(e.PropertyName, nameof(SettingsViewModel.SelectedUiTheme), StringComparison.Ordinal))
        {
            SyncDwmWindowFrameTheme();
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
        _viewModel.UpdateViewportWidth(ActualWidth);
        UpdateMaximizedLayoutChrome();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isApplicationExiting)
        {
            return;
        }

        if (_viewModel.Settings.CloseWindowToTray && _trayService.IsActive)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
            return;
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
            if (current is ButtonBase or TextBox or ComboBox or Slider)
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

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizedLayoutChrome();
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

            if (_openPopups.Count > 0)
            {
                foreach (Popup popup in _openPopups.ToArray())
                {
                    if (popup.IsOpen)
                    {
                        popup.IsOpen = false;
                        closedAny = true;
                    }
                }
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

                if (owningPopup is not null)
                {
                    owningPopup.IsOpen = false;
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
}
