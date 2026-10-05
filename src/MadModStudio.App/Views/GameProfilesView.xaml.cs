using System.Windows.Controls;
using System.Windows.Input;
using MadModStudio.App.ViewModels;

namespace MadModStudio.App.Views;

public partial class GameProfilesView : UserControl
{
    public GameProfilesView() => InitializeComponent();

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is GameProfilesViewModel vm)
        {
            if (sender is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            vm.SearchCommand.Execute(null);
        }
    }
}
