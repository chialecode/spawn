using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Spawn.Host.ViewModels.Saves;

namespace Spawn.Host.Views.Saves;

public partial class SaveWorkbenchControl : UserControl
{
    private readonly HashSet<Popup> _openPopups = [];
    private bool _isTreeSearchImeComposing;
    private SaveWorkbenchViewModel? _subscribedVm;

    public SaveWorkbenchControl()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;

        TextCompositionManager.AddPreviewTextInputStartHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.ScrollToSnapshotRequested -= OnScrollToSnapshotRequested;
            _subscribedVm = null;
        }

        if (e.NewValue is SaveWorkbenchViewModel vm)
        {
            _subscribedVm = vm;
            _subscribedVm.ScrollToSnapshotRequested += OnScrollToSnapshotRequested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.ScrollToSnapshotRequested -= OnScrollToSnapshotRequested;
            _subscribedVm = null;
        }
    }

    private void OnScrollToSnapshotRequested(SaveSnapshotRowViewModel row)
    {
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (SaveTreeListBox.Items.Contains(row))
                {
                    SaveTreeListBox.ScrollIntoView(row);
                }
            }));
    }

    private void OnTreeItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            if (DataContext is SaveWorkbenchViewModel vm && item.DataContext is SaveSnapshotRowViewModel row)
            {
                vm.SelectedNode = row;
            }
        }
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is SaveWorkbenchViewModel vm)
        {
            vm.UpdateViewportWidth(e.NewSize.Width);
        }
    }

    private void OnTreeSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed)
        {
            _isTreeSearchImeComposing = true;
            return;
        }

        if (e.Key == Key.Escape
            && !_isTreeSearchImeComposing
            && DataContext is SaveWorkbenchViewModel vm
            && !string.IsNullOrEmpty(vm.TreeSearchText))
        {
            vm.TreeSearchText = string.Empty;
            e.Handled = true;
            return;
        }

        _isTreeSearchImeComposing = false;
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

    private void OnControlPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        bool closed = false;
        foreach (Popup popup in _openPopups.ToArray())
        {
            if (popup.IsOpen)
            {
                popup.IsOpen = false;
                closed = true;
            }
        }

        if (!closed && DataContext is SaveWorkbenchViewModel vm)
        {
            closed = vm.TryHandleEscapeKey();
        }

        if (closed)
        {
            e.Handled = true;
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

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (owningPopup is not null)
                {
                    owningPopup.IsOpen = false;
                }
            }));
    }
}
