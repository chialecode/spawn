using System.Windows;
using System.Windows.Controls;
using Spawn.Host.ViewModels;

namespace Spawn.Host.Views.Saves;

public partial class SavesSettingsControl : UserControl
{
    public SavesSettingsControl()
    {
        InitializeComponent();
    }

    private async void OnSaveCloudEndpointButtonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        string secret = EndpointSecretPasswordBox.Password;
        bool saved = await vm.SaveNewCloudEndpointAsync(secret).ConfigureAwait(true);
        if (saved)
        {
            EndpointSecretPasswordBox.Clear();
        }
    }
}
