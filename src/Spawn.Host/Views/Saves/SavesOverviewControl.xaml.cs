using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Spawn.Host.ViewModels.Saves;

namespace Spawn.Host.Views.Saves;

public partial class SavesOverviewControl : UserControl
{
    public SavesOverviewControl()
    {
        InitializeComponent();
    }

    private void OnGameCardRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsClickInsideInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: ManagedSaveGameOverviewItemViewModel item }
            && item.OpenTreeCommand.CanExecute(null))
        {
            item.OpenTreeCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsClickInsideInteractiveControl(DependencyObject? source)
    {
        DependencyObject? cur = source;
        while (cur is not null)
        {
            if (cur is ButtonBase)
            {
                return true;
            }

            cur = VisualTreeHelper.GetParent(cur);
        }

        return false;
    }
}
