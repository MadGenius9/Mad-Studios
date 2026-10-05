using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MadModStudio.App.ViewModels;

namespace MadModStudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    private readonly List<RadioButton> _navButtons = new();

    private void NavItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && !_navButtons.Contains(rb))
        {
            _navButtons.Add(rb);
            SyncNav();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedKey)) SyncNav();
    }

    private void SyncNav()
    {
        if (DataContext is not MainViewModel vm) return;
        foreach (var rb in _navButtons) rb.IsChecked = (string?)rb.Tag == vm.SelectedKey;
    }
}
