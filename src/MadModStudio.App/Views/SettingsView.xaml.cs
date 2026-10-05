using System.Windows;
using System.Windows.Controls;
using MadModStudio.App.ViewModels;

namespace MadModStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Keys go straight from the PasswordBox to the secret store; they are never bound to a view-model property.</summary>
    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is Button { Tag: PasswordBox box, DataContext: ProviderItem item })
        {
            vm.SaveKey(item, box.Password);
            box.Clear();
        }
    }
}
