using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Models;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

/// <summary>
/// New Mod: creates the project and hands the request to the agent team (Lead plans, specialists research/implement,
/// compiler + validator verify). The board, approvals and escalation run in the project's Agents tab.
/// </summary>
public sealed partial class NewModViewModel : PageViewModel
{
    private readonly IAIProviderRegistry _providers;
    private readonly IModelCatalog _catalog;
    private readonly AIPolicy _policy;
    private readonly ProjectService _projects;
    private readonly INavigator _nav;

    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private string _projectName = "";

    public NewModViewModel(IAIProviderRegistry providers, IModelCatalog catalog, AIPolicy policy, ProjectService projects, INavigator nav, AppState state)
    {
        _providers = providers;
        _catalog = catalog;
        _policy = policy;
        _projects = projects;
        _nav = nav;
        State = state;
    }

    public override string Title => "New Mod";
    public AppState State { get; }
    /// <summary>True only when a provider is configured and at least one model has been discovered.</summary>
    public bool IsAIConfigured => _providers.All.Any(p => p.IsConfigured) && _catalog.Models.Any(m => m.Available);
    public string AIStatusText => !_providers.All.Any(p => p.IsConfigured)
        ? "AI is NOT CONFIGURED. Add a provider in Settings → AI Providers to have the agent team build mods. You can still create an empty project and build it manually."
        : !_catalog.Models.Any(m => m.Available)
            ? "A provider is configured but no models have been discovered yet. Open AI Models and click Refresh Models."
            : $"Agent control: {_policy.ControlLevel.ToString().ToUpperInvariant()} · routing: {_policy.RoutingMode.ToString().ToUpperInvariant()}. "
              + "Every AI change is shown for approval in GUIDED mode, and a revision is created before every change.";

    public override Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(IsAIConfigured));
        OnPropertyChanged(nameof(AIStatusText));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task CreateWithAgents() => RunAsync("Creating project...", async () =>
    {
        if (string.IsNullOrWhiteSpace(Prompt)) { ErrorMessage = "Describe what you want to build first."; return; }
        if (string.IsNullOrWhiteSpace(ProjectName)) { ErrorMessage = "Enter a mod name."; return; }
        if (!IsAIConfigured) { ErrorMessage = AIStatusText; return; }
        if (State.CurrentProfile is null) { ErrorMessage = "Select an indexed Game Profile first: agents must verify game APIs against your installed game."; return; }
        var project = await _projects.CreateNewAsync(ProjectName.Trim(), State.CurrentProfile.Id, description: Prompt);
        await _nav.OpenProjectAsync(project.Id, new AgentLaunch(Prompt.Trim(), WorkflowKind.Create, Array.Empty<(string, string)>(), null));
    });

    [RelayCommand]
    private Task CreateEmpty() => RunAsync("Creating project...", async () =>
    {
        if (string.IsNullOrWhiteSpace(ProjectName)) { ErrorMessage = "Enter a mod name."; return; }
        var project = await _projects.CreateNewAsync(ProjectName.Trim(), State.CurrentProfile?.Id, description: string.IsNullOrWhiteSpace(Prompt) ? null : Prompt);
        await _nav.OpenProjectAsync(project.Id);
    });
}
