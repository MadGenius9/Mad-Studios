using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.App.ViewModels;

public sealed record NavItem(string Key, string Label, string Glyph);

public interface INavigator
{
    Task NavigateAsync(string key);
    Task OpenProjectAsync(Guid projectId);
    Task StartNewModAsync(string prompt);
}

public sealed partial class MainViewModel : ObservableObject, INavigator
{
    private readonly IServiceProvider _services;
    private readonly IAIProvider _ai;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _selectedKey = "home";
    [ObservableProperty] private string _profileStatus = "";
    [ObservableProperty] private string _aiStatus = "";

    public MainViewModel(IServiceProvider services, AppState state, IAIProvider ai)
    {
        _services = services;
        _ai = ai;
        State = state;
        state.PropertyChanged += (_, _) => UpdateStatus();
    }

    public AppState State { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new("home", "Home", ""),
        new("newmod", "New Mod", ""),
        new("repair", "Repair Mod", ""),
        new("mymods", "My Mods", ""),
        new("scanner", "Batch Scanner", ""),
        new("profiles", "Game Profiles", ""),
        new("settings", "Settings", ""),
    };

    public async Task InitializeAsync()
    {
        await State.RefreshAsync();
        UpdateStatus();
        await NavigateAsync(State.CurrentProfile is null ? "profiles" : "home");
    }

    [RelayCommand]
    private Task Navigate(string key) => NavigateAsync(key);

    public async Task NavigateAsync(string key)
    {
        PageViewModel page = key switch
        {
            "home" => _services.GetRequiredService<HomeViewModel>(),
            "newmod" => _services.GetRequiredService<NewModViewModel>(),
            "repair" => _services.GetRequiredService<RepairViewModel>(),
            "mymods" => _services.GetRequiredService<MyModsViewModel>(),
            "scanner" => _services.GetRequiredService<BatchScannerViewModel>(),
            "profiles" => _services.GetRequiredService<GameProfilesViewModel>(),
            "settings" => _services.GetRequiredService<SettingsViewModel>(),
            _ => _services.GetRequiredService<HomeViewModel>(),
        };
        SelectedKey = key;
        CurrentPage = page;
        await page.OnNavigatedToAsync();
        UpdateStatus();
    }

    public async Task OpenProjectAsync(Guid projectId)
    {
        var vm = _services.GetRequiredService<ProjectViewModel>();
        SelectedKey = "mymods";
        CurrentPage = vm;
        await vm.LoadAsync(projectId);
    }

    public async Task StartNewModAsync(string prompt)
    {
        var vm = _services.GetRequiredService<NewModViewModel>();
        vm.Prompt = prompt;
        SelectedKey = "newmod";
        CurrentPage = vm;
        await vm.OnNavigatedToAsync();
    }

    private void UpdateStatus()
    {
        var p = State.CurrentProfile;
        ProfileStatus = p is null ? "No game profile" : $"{p.Name} · index {p.IndexStatus}";
        AiStatus = _ai.IsConfigured ? $"AI: {_ai.DisplayName} ready" : "AI: not configured";
    }
}
