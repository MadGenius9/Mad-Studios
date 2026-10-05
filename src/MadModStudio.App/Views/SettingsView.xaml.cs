using System.Windows;
using System.Windows.Controls;
using MadModStudio.App.ViewModels;

namespace MadModStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.SaveKey(KeyBox.Password);
            KeyBox.Clear();
        }
    }
}
