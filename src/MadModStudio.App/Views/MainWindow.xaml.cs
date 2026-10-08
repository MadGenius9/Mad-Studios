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
        FitToWorkArea();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PropertyChanged += OnVmChanged;
        };
    }

    // On small or high-DPI screens the design size (1440x900) can exceed the usable area, which
    // pushes the title bar (minimize/maximize/close) off-screen. Clamp size and center in the work area.
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
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
