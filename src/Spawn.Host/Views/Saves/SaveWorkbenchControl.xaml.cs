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

    public SaveWorkbenchControl()
    {
        InitializeComponent();

        TextCompositionManager.AddPreviewTextInputStartHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(TreeSearchTextBox, (_, _) => _isTreeSearchImeComposing = false);
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
        if (e.Key != Key.Escape || _openPopups.Count == 0)
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
