using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Engines;
using MadModStudio.AI.Tools;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

public sealed partial class NewModViewModel : PageViewModel
{
    private readonly AIModBuilder _builder;
    private readonly IAIProvider _provider;
    private readonly ProjectService _projects;
    private readonly INavigator _nav;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private AIPlan? _plan;
    [ObservableProperty] private string _planText = "";

    public NewModViewModel(AIModBuilder builder, IAIProvider provider, ProjectService projects, INavigator nav, AppState state)
    {
        _builder = builder;
        _provider = provider;
        _projects = projects;
        _nav = nav;
        State = state;
    }

    public override string Title => "New Mod";
    public AppState State { get; }
    public bool IsAIConfigured => _provider.IsConfigured;
    public bool HasPlan => Plan != null;

    partial void OnPlanChanged(AIPlan? value) => OnPropertyChanged(nameof(HasPlan));
    public ObservableCollection<string> Events { get; } = new();

    public override Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(IsAIConfigured));
        return Task.CompletedTask;
    }

    private IProgress<AIEvent> Progress() => new Progress<AIEvent>(e =>
    {
        if (e.Kind == AIEventKind.Thinking) return;
        Events.Add($"{e.TimestampUtc.LocalDateTime:HH:mm:ss} [{e.Kind}] {e.Message}");
    });

    [RelayCommand]
    private Task MakePlan() => RunAsync("Investigating your installed game and planning...", async () =>
    {
        if (string.IsNullOrWhiteSpace(Prompt)) { ErrorMessage = "Describe what you want to build first."; return; }
        Events.Clear();
        Plan = null;
        _cts = new CancellationTokenSource();
        var progress = Progress();
        var prompt = Prompt;
        var profile = State.CurrentProfile;
        var r = await Task.Run(() => _builder.PlanAsync(prompt, profile, progress, _cts.Token));
        if (r.Plan is null) { ErrorMessage = r.Error; return; }
        Plan = r.Plan;
        PlanText = $"Classification: {r.Plan.Classification}\n\n{r.Plan.Reasoning}\n\nSteps:\n{string.Join("\n", r.Plan.Steps.Select((s, i) => $"  {i + 1}. {s}"))}\n\nVerified game APIs/XML:\n{string.Join("\n", r.Plan.GameApis.Select(a => "  • " + a))}\n\nRisks:\n{string.Join("\n", r.Plan.Risks.Select(a => "  • " + a))}";
        if (string.IsNullOrWhiteSpace(ProjectName)) ProjectName = r.Plan.SuggestedName ?? "";
        StatusMessage = $"Plan ready ({r.AIResult?.ToolCallCount ?? 0} game lookups, {r.AIResult?.Usage.InputTokens + r.AIResult?.Usage.OutputTokens:N0} tokens).";
    });

    [RelayCommand]
    private Task Generate() => RunAsync("Creating project and generating files...", async () =>
    {
        if (Plan is null) return;
        if (string.IsNullOrWhiteSpace(ProjectName)) { ErrorMessage = "Enter a mod name."; return; }
        _cts = new CancellationTokenSource();
        var project = await _projects.CreateNewAsync(ProjectName.Trim(), State.CurrentProfile?.Id, description: Prompt);
        var progress = Progress();
        var plan = Plan;
        var prompt = Prompt;
        var r = await Task.Run(() => _builder.GenerateAsync(project, prompt, plan, progress, _cts.Token));
        if (!r.Applied)
        {
            ErrorMessage = (r.Error ?? "Generation failed.") + " The empty project was created; you can continue manually.";
        }
        else
        {
            StatusMessage = r.Repair?.Succeeded == true ? "Generated, compiled and validated." : $"Generated. {r.Repair?.StopReason}";
        }
        await _nav.OpenProjectAsync(project.Id);
    });

    [RelayCommand]
    private Task CreateEmpty() => RunAsync("Creating project...", async () =>
    {
        if (string.IsNullOrWhiteSpace(ProjectName)) { ErrorMessage = "Enter a mod name."; return; }
        var project = await _projects.CreateNewAsync(ProjectName.Trim(), State.CurrentProfile?.Id, description: string.IsNullOrWhiteSpace(Prompt) ? null : Prompt);
        await _nav.OpenProjectAsync(project.Id);
    });

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
