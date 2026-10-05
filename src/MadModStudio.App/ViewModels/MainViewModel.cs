using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Models;
using MadModStudio.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.App.ViewModels;

public sealed record NavItem(string Key, string Label, string Glyph);

/// <summary>Request to start the agent team as soon as a project opens (from New Mod / Repair Mod).</summary>
public sealed record AgentLaunch(string Request, WorkflowKind Kind, IReadOnlyList<(string Path, string Kind)> Logs, string? WorkingVersionPath);

public interface INavigator
{
    Task NavigateAsync(string key);
    Task OpenProjectAsync(Guid projectId, AgentLaunch? launch = null);
    Task StartNewModAsync(string prompt);
}

public sealed partial class MainViewModel : ObservableObject, INavigator
{
    private readonly IServiceProvider _services;
    private readonly IAIProviderRegistry _providers;
    private readonly IModelCatalog _catalog;
    private readonly AIPolicy _policy;
    private readonly ISettingsRepository _settings;
    private bool _suppressModelSave;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private string _selectedKey = "home";
    [ObservableProperty] private string _profileStatus = "";
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private ModelChoice? _defaultModel;

    public MainViewModel(IServiceProvider services, AppState state, IAIProviderRegistry providers, IModelCatalog catalog, AIPolicy policy, ISettingsRepository settings)
    {
        _services = services;
        _providers = providers;
        _catalog = catalog;
        _policy = policy;
        _settings = settings;
        State = state;
        state.PropertyChanged += (_, _) => UpdateStatus();
        _catalog.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(ReloadModels);
        ReloadModels();
    }

    /// <summary>Global default model (AUTO lets the router choose). Projects and agents can override it.</summary>
    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();

    public AppState State { get; }

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new("home", "Home", ""),
        new("newmod", "New Mod", ""),
        new("repair", "Repair Mod", ""),
        new("mymods", "My Mods", ""),
        new("scanner", "Batch Scanner", ""),
        new("aimodels", "AI Models", "\uE9D2"),
        new("profiles", "Game Profiles", ""),
        new("settings", "Settings", ""),
    };

    public async Task InitializeAsync()
    {
        await State.RefreshAsync();
        UpdateStatus();
        await NavigateAsync(State.CurrentProfile is null ? "profiles" : "home");
        // Discover models in the background. Only API keys are sent (to each provider's own model-list endpoint); no project data.
        if (_providers.All.Any(p => p.IsConfigured))
            _ = Task.Run(async () =>
            {
                try { await _catalog.RefreshAsync(); }
                catch (Exception ex) { App.Log("Background model discovery failed", ex); }
            });
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
            "aimodels" => _services.GetRequiredService<AIModelsViewModel>(),
            "settings" => _services.GetRequiredService<SettingsViewModel>(),
            _ => _services.GetRequiredService<HomeViewModel>(),
        };
        SelectedKey = key;
        CurrentPage = page;
        await page.OnNavigatedToAsync();
        UpdateStatus();
    }

    public async Task OpenProjectAsync(Guid projectId, AgentLaunch? launch = null)
    {
        var vm = _services.GetRequiredService<ProjectViewModel>();
        SelectedKey = "mymods";
        CurrentPage = vm;
        await vm.LoadAsync(projectId, launch);
    }

    public async Task StartNewModAsync(string prompt)
    {
        var vm = _services.GetRequiredService<NewModViewModel>();
        vm.Prompt = prompt;
        SelectedKey = "newmod";
        CurrentPage = vm;
        await vm.OnNavigatedToAsync();
    }

    private void ReloadModels()
    {
        _suppressModelSave = true;
        try
        {
            ModelChoices.Clear();
            ModelChoices.Add(ModelChoice.Auto);
            foreach (var m in _catalog.Models.Where(m => m.Available)) ModelChoices.Add(new ModelChoice(m.Key, m.ToString()));
            DefaultModel = ModelChoices.FirstOrDefault(c => c.Key == _policy.DefaultModel) ?? ModelChoice.Auto;
        }
        finally { _suppressModelSave = false; }
        UpdateStatus();
    }

    partial void OnDefaultModelChanged(ModelChoice? value)
    {
        if (_suppressModelSave || value is null || value.Key == _policy.DefaultModel) return;
        _policy.DefaultModel = value.Key;
        _policy.DefaultProvider = ModelKey.Parse(value.Key)?.Provider;
        _ = SaveDefaultAsync();
        UpdateStatus();
    }

    private async Task SaveDefaultAsync()
    {
        try { await _policy.SaveAsync(_settings); }
        catch (Exception ex) { App.Log("Saving default model failed", ex); }
    }

    private void UpdateStatus()
    {
        var p = State.CurrentProfile;
        ProfileStatus = p is null ? "No game profile" : $"{p.Name} · index {p.IndexStatus}";
        var configured = _providers.All.Where(x => x.IsConfigured).ToList();
        var models = _catalog.Models.Count(m => m.Available);
        AiStatus = configured.Count == 0 ? "AI: NOT CONFIGURED"
            : models == 0 ? $"AI: {configured.Count} provider(s) configured · no models discovered"
            : $"AI: {models} model(s) · routing {_policy.RoutingMode.ToString().ToUpperInvariant()} · control {_policy.ControlLevel.ToString().ToUpperInvariant()}";
    }
}
