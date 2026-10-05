using System.Windows.Controls;
using System.Windows.Input;
using MadModStudio.App.ViewModels;
using MadModStudio.Core.Models;

namespace MadModStudio.App.Views;

public partial class MyModsView : UserControl
{
    public MyModsView() => InitializeComponent();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MyModsViewModel vm && Grid.SelectedItem is ModProject p) vm.OpenCommand.Execute(p);
    }
}
