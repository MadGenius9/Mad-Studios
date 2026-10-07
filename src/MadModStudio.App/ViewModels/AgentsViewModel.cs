using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.App.Services;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

public sealed record ModelChoice(string? Key, string Display)
{
    public static readonly ModelChoice Auto = new(null, "AUTO");
    public override string ToString() => Display;
}

public sealed record OptionalChoice<T>(T? Value, string Display) where T : struct
{
    public override string ToString() => Display;
}

/// <summary>One row on the Agent Board (a snapshot of an AgentTaskRecord).</summary>
public sealed partial class AgentTaskItem : ObservableObject
{
    public string Id { get; init; } = "";
    [ObservableProperty] private AgentKind _agent;
    [ObservableProperty] private string _agentName = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _modelDisplay = "";
    [ObservableProperty] private AgentTaskState _state;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _activity = "";
    public DateTimeOffset? ActivityStartedUtc { get; set; }
    public DateTimeOffset? WorkStartedUtc { get; set; }
    public AgentTaskRecord? Snapshot { get; set; }
    public bool HasActivity => Activity.Length > 0;
    partial void OnActivityChanged(string value) => OnPropertyChanged(nameof(HasActivity));

    /// <summary>Recomputes the live line, e.g. "Waiting for a1 (turn 2) · 34s · working 2m 10s". Called by a 1 s timer.</summary>
    public void Tick(string? currentActivity)
    {
        static string Span(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
        var now = DateTimeOffset.UtcNow;
        if (currentActivity is null || State is not (AgentTaskState.Working or AgentTaskState.Planning or AgentTaskState.NeedsReview)) { Activity = ""; return; }
        Activity = currentActivity
            + (ActivityStartedUtc is { } a ? $" · {Span(now - a)}" : "")
            + (WorkStartedUtc is { } w ? $" · working {Span(now - w)}" : "");
    }
    public string StateText => State switch
    {
        AgentTaskState.NeedsReview => "NEEDS REVIEW",
        _ => State.ToString().ToUpperInvariant(),
    };
    partial void OnStateChanged(AgentTaskState value) => OnPropertyChanged(nameof(StateText));
}

public sealed partial class AgentModelSetting : ObservableObject
{
    public AgentKind Agent { get; init; }
    public string DisplayName { get; init; } = "";
    public IReadOnlyList<ModelChoice> Options { get; init; } = Array.Empty<ModelChoice>();
    [ObservableProperty] private ModelChoice _selected = ModelChoice.Auto;
}

/// <summary>Agent Workspace for one project: request → coordinated agents, board, approvals, escalation, team.</summary>
public sealed partial class AgentsViewModel : ObservableObject
{
    private readonly IAgentCoordinator _coordinator;
    private readonly IModelCatalog _catalog;
    private readonly IAIProviderRegistry _providers;
    private readonly ProjectKnowledgeService _knowledge;
    private readonly IAIContextBuilder _context;
    private readonly ApprovalQueue _approvals;
    private readonly AgentCatalog _agents;
    private readonly AIPolicy _policy;
    private readonly ProjectService _projects;
    private readonly GameProfileService _profiles;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private ModProject? _project;
    private Func<Task>? _afterChanges;
    private IReadOnlyList<(string Path, string Kind)>? _launchLogs;
    private readonly System.Windows.Threading.DispatcherTimer _ticker;

    [ObservableProperty] private string _request = "";
    [ObservableProperty] private WorkflowKind _kind = WorkflowKind.Repair;
    [ObservableProperty] private bool _package = true;
    [ObservableProperty] private string _workingVersionPath = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _runStatus = "";
    [ObservableProperty] private string _report = "";
    [ObservableProperty] private AgentTaskItem? _selectedTask;
    [ObservableProperty] private string _taskDetails = "";
    [ObservableProperty] private PendingChange? _selectedPending;
    [ObservableProperty] private string? _selectedPendingFile;
    [ObservableProperty] private bool _sideBySide = true;
    [ObservableProperty] private EscalationSuggestion? _escalation;
    [ObservableProperty] private ModelChoice? _escalationChoice;
    [ObservableProperty] private OptionalChoice<RoutingMode>? _routingChoice;
    [ObservableProperty] private OptionalChoice<AgentControlLevel>? _controlChoice;
    [ObservableProperty] private string _teamSummary = "";
    [ObservableProperty] private string _whyText = "";
    [ObservableProperty] private ModelChoice? _retryModel = ModelChoice.Auto;
    [ObservableProperty] private KnowledgeArtifact? _selectedProposal;
    [ObservableProperty] private string _secondOpinionText = "";
    [ObservableProperty] private string _handoffText = "";
    [ObservableProperty] private AgentKind _manualAgent = AgentKind.GameApiResearch;
    [ObservableProperty] private string _manualInstructions = "";
    [ObservableProperty] private ModelChoice? _manualModel = ModelChoice.Auto;
    [ObservableProperty] private string _aiAvailability = "";
    [ObservableProperty] private string? _errorMessage;

    public AgentsViewModel(IAgentCoordinator coordinator, IModelCatalog catalog, IAIProviderRegistry providers, ProjectKnowledgeService knowledge,
        IAIContextBuilder context, ApprovalQueue approvals, AgentCatalog agents, AIPolicy policy, ProjectService projects, GameProfileService profiles, IDialogService dialogs)
    {
        _coordinator = coordinator; _catalog = catalog; _providers = providers; _knowledge = knowledge; _context = context; _approvals = approvals;
        _agents = agents; _policy = policy; _projects = projects; _profiles = profiles; _dialogs = dialogs;
        _approvals.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(RefreshApprovals);
        // Live elapsed times on the board while agents run (values come from real provider events, the timer only re-renders).
        _ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => { foreach (var t in Tasks) t.Tick(t.Snapshot?.CurrentActivity); };
        RoutingOptions = new[] { new OptionalChoice<RoutingMode>(null, "Global default") }.Concat(Enum.GetValues<RoutingMode>().Select(m => new OptionalChoice<RoutingMode>(m, m.ToString().ToUpperInvariant()))).ToList();
        ControlOptions = new[] { new OptionalChoice<AgentControlLevel>(null, "Global default") }.Concat(Enum.GetValues<AgentControlLevel>().Select(m => new OptionalChoice<AgentControlLevel>(m, m.ToString().ToUpperInvariant()))).ToList();
    }

    public ObservableCollection<AgentTaskItem> Tasks { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<PendingChange> PendingApprovals { get; } = new();
    public ObservableCollection<string> PendingFiles { get; } = new();
    public ObservableCollection<MadModStudio.ModAnalysis.Comparison.SideBySideRow> DiffRows { get; } = new();
    public ObservableCollection<AgentModelSetting> Team { get; } = new();
    public ObservableCollection<KnowledgeArtifact> Proposals { get; } = new();
    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();
    public IReadOnlyList<OptionalChoice<RoutingMode>> RoutingOptions { get; }
    public IReadOnlyList<OptionalChoice<AgentControlLevel>> ControlOptions { get; }
    public Array Kinds { get; } = Enum.GetValues(typeof(WorkflowKind));
    public IReadOnlyList<AgentKind> AgentKinds => _agents.Specialists.Select(s => s.Kind).ToList();
    public bool HasModels => _catalog.Models.Any(m => m.Available);
    public bool HasEscalation => Escalation != null;
    public bool HasPending => PendingApprovals.Count > 0;

    partial void OnEscalationChanged(EscalationSuggestion? value) => OnPropertyChanged(nameof(HasEscalation));

    public async Task LoadAsync(ModProject project, Func<Task> afterChanges)
    {
        _project = project;
        _afterChanges = afterChanges;
        ModelChoices.Clear();
        ModelChoices.Add(ModelChoice.Auto);
        foreach (var m in _catalog.Models.Where(m => m.Available)) ModelChoices.Add(new ModelChoice(m.Key, m.ToString()));
        OnPropertyChanged(nameof(HasModels));
        var configured = _providers.All.Where(p => p.IsConfigured).Select(p => p.DisplayName).ToList();
        AiAvailability = configured.Count == 0
            ? "NOT CONFIGURED — add an AI provider in Settings → AI Providers. Agents cannot run until a provider is configured and models are discovered."
            : !HasModels ? $"Providers configured ({string.Join(", ", configured)}) but no models discovered yet — open AI Models and click Refresh Models."
            : $"Providers: {string.Join(", ", configured)} · {ModelChoices.Count - 1} model(s) available.";

        var pk = await _knowledge.Repository.GetProjectAsync(project.Id);
        if (string.IsNullOrWhiteSpace(Request)) Request = pk.Objective ?? "Fix this mod.";
        RoutingChoice = RoutingOptions.First(o => Equals(o.Value, pk.AISettings.RoutingMode));
        ControlChoice = ControlOptions.First(o => Equals(o.Value, pk.AISettings.ControlLevel));
        Team.Clear();
        foreach (var def in _agents.All)
        {
            var key = pk.AISettings.AgentModels.GetValueOrDefault(def.Kind.ToString());
            Team.Add(new AgentModelSetting
            {
                Agent = def.Kind, DisplayName = def.DisplayName, Options = ModelChoices.ToList(),
                Selected = ModelChoices.FirstOrDefault(c => c.Key == key) ?? ModelChoice.Auto,
            });
        }
        UpdateTeamSummary();
        await ReloadBoardAsync();
        RefreshApprovals();
    }

    /// <summary>Starts a workflow handed over from New Mod / Repair Mod.</summary>
    public Task StartAsync(AgentLaunch launch)
    {
        Request = launch.Request;
        Kind = launch.Kind;
        WorkingVersionPath = launch.WorkingVersionPath ?? "";
        _launchLogs = launch.Logs.Count > 0 ? launch.Logs : null;
        return RunAgentsCommand.ExecuteAsync(null);
    }

    private void UpdateTeamSummary()
    {
        var routing = RoutingChoice?.Value?.ToString().ToUpperInvariant() ?? _policy.RoutingMode.ToString().ToUpperInvariant();
        var control = ControlChoice?.Value?.ToString().ToUpperInvariant() ?? _policy.ControlLevel.ToString().ToUpperInvariant();
        var picks = Team.Where(t => t.Selected.Key != null).Select(t => $"{t.DisplayName}: {t.Selected.Display}").ToList();
        TeamSummary = $"Routing: {routing}   Control: {control}   {(picks.Count == 0 ? "All agents: AUTO" : string.Join("   ", picks))}";
    }

    private async Task ReloadBoardAsync()
    {
        if (_project is null) return;
        var all = await _knowledge.Repository.ListTasksAsync(_project.Id);
        var lastRun = all.Select(t => t.RunId).LastOrDefault();
        Tasks.Clear();
        foreach (var t in all.Where(t => t.RunId == lastRun)) Upsert(t);
        Proposals.Clear();
        foreach (var p in await _knowledge.ListAsync(_project.Id, ArtifactKind.ProposedChange, 50)) Proposals.Add(p);
    }

    private void Upsert(AgentTaskRecord t)
    {
        AgentTaskRecord copy;
        lock (t) copy = JsonSerializer.Deserialize<AgentTaskRecord>(JsonSerializer.Serialize(t))!;
        var item = Tasks.FirstOrDefault(x => x.Id == copy.Id);
        if (item is null)
        {
            item = new AgentTaskItem { Id = copy.Id };
            Tasks.Add(item);
        }
        item.Agent = copy.Agent;
        item.AgentName = _agents.Get(copy.Agent).DisplayName;
        item.Title = copy.Title;
        item.ModelDisplay = copy.Model is null ? "—" : _catalog.Find(copy.Model)?.ToString() ?? copy.Model;
        item.State = copy.State;
        item.Summary = copy.Error ?? copy.ResultSummary ?? (copy.RecentActions.LastOrDefault() ?? "");
        item.ActivityStartedUtc = copy.ActivityStartedUtc;
        item.WorkStartedUtc = copy.WorkStartedUtc;
        item.Snapshot = copy;
        item.Tick(copy.CurrentActivity);
        if (SelectedTask == item) _ = UpdateDetailsAsync();
    }

    private sealed class UiObserver : IAgentRunObserver
    {
        private readonly AgentsViewModel _vm;
        public UiObserver(AgentsViewModel vm) => _vm = vm;
        public void OnTaskChanged(AgentTaskRecord task) => Application.Current.Dispatcher.BeginInvoke(() => _vm.Upsert(task));
        public void OnEvent(string message) => Application.Current.Dispatcher.BeginInvoke(() => _vm.Events.Add(message));
    }

    private void RefreshApprovals()
    {
        PendingApprovals.Clear();
        foreach (var p in _approvals.Pending.Where(p => _project != null && p.ProjectId == _project.Id)) PendingApprovals.Add(p);
        SelectedPending ??= PendingApprovals.FirstOrDefault();
        OnPropertyChanged(nameof(HasPending));
    }

    partial void OnSelectedTaskChanged(AgentTaskItem? value) => _ = UpdateDetailsAsync();

    partial void OnSelectedPendingChanged(PendingChange? value)
    {
        PendingFiles.Clear();
        foreach (var e in value?.Edits ?? Array.Empty<FileEdit>()) PendingFiles.Add(e.RelativePath);
        SelectedPendingFile = PendingFiles.FirstOrDefault();
        if (SelectedPendingFile is null) DiffRows.Clear();
    }

    /// <summary>Side-by-side rows for one file of the pending change: the file as it is now vs. the proposed content.</summary>
    partial void OnSelectedPendingFileChanged(string? value)
    {
        DiffRows.Clear();
        var edit = SelectedPending?.Edits.FirstOrDefault(e => e.RelativePath == value);
        if (_project is null || edit is null) return;
        string current;
        try
        {
            var full = Path.Combine(_project.SourcePath, edit.RelativePath);
            current = File.Exists(full) ? File.ReadAllText(full) : "";
        }
        catch (IOException ex) { current = $"(could not read the current file: {ex.Message})"; }
        foreach (var row in MadModStudio.ModAnalysis.Comparison.TextDiff.SideBySide(current, edit.NewContent ?? "")) DiffRows.Add(row);
    }

    private async Task UpdateDetailsAsync()
    {
        var t = SelectedTask?.Snapshot;
        if (t is null || _project is null) { TaskDetails = ""; return; }
        var findings = (await _knowledge.ListAsync(_project.Id, null, 300)).Where(a => a.TaskId == t.Id).ToList();
        var deps = t.DependsOn.Select(d => Tasks.FirstOrDefault(x => x.Id == d)?.Title ?? d).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{_agents.Get(t.Agent).DisplayName} — {t.State.ToString().ToUpperInvariant()}");
        sb.AppendLine($"Task: {t.Title}");
        if (!string.IsNullOrWhiteSpace(t.Instructions)) sb.AppendLine($"Instructions: {t.Instructions}");
        sb.AppendLine($"Provider: {t.Provider ?? "—"}   Model: {t.Model ?? "—"}");
        if (t.CurrentActivity != null) sb.AppendLine($"Now: {t.CurrentActivity}");
        if (t.ModelsTried.Count > 1) sb.AppendLine($"Models tried: {string.Join(" → ", t.ModelsTried)}");
        if (deps.Count > 0) sb.AppendLine($"Depends on: {string.Join(", ", deps)}");
        if (t.FilesRead.Count > 0) sb.AppendLine($"Files read: {string.Join(", ", t.FilesRead)}");
        if (t.FilesWritten.Count > 0) sb.AppendLine($"Files written: {string.Join(", ", t.FilesWritten)}");
        if (t.RoutingReasons.Count > 0) sb.AppendLine("Why this model:\n  " + string.Join("\n  ", t.RoutingReasons));
        if (findings.Count > 0)
        {
            sb.AppendLine("Findings / evidence produced:");
            foreach (var f in findings.Take(20)) sb.AppendLine($"  [{f.Kind}{(f.Verified ? ", verified" : "")}] {f.Title}");
        }
        if (t.ResultSummary != null) sb.AppendLine($"Result: {t.ResultSummary}");
        if (t.Error != null) sb.AppendLine($"Problem: {t.Error}");
        if (t.RecentActions.Count > 0) sb.AppendLine("Recent actions:\n  " + string.Join("\n  ", t.RecentActions.TakeLast(15)));
        TaskDetails = sb.ToString();
    }

    partial void OnIsRunningChanged(bool value)
    {
        if (value) _ticker.Start(); else _ticker.Stop();
    }

    private async Task Guard(string what, Func<Task> action)
    {
        if (IsRunning) return;
        IsRunning = true;
        ErrorMessage = null;
        RunStatus = what;
        _cts = new CancellationTokenSource();
        try { await action(); }
        catch (OperationCanceledException) { RunStatus = "Cancelled."; }
        catch (Exception ex)
        {
            ErrorMessage = MadModStudio.Core.Security.SecretRedactor.Redact(ex.Message);
            App.Log("Agent operation failed", ex);
        }
        finally
        {
            IsRunning = false;
            if (_afterChanges != null) await _afterChanges();
            await ReloadBoardAsync();
        }
    }

    [RelayCommand]
    private Task RunAgents() => Guard("Agents working…", async () =>
    {
        if (_project is null || string.IsNullOrWhiteSpace(Request)) return;
        Events.Clear();
        Tasks.Clear();
        Escalation = null;
        Report = "";
        var logDir = Path.Combine(_project.Workspace.Attachments, "logs");
        var logs = _launchLogs?.ToList() ?? (Directory.Exists(logDir)
            ? Directory.GetFiles(logDir).Select(f => (f, Path.GetFileName(f).Contains("server", StringComparison.OrdinalIgnoreCase) ? "server" : "client")).ToList()
            : new List<(string, string)>());
        _launchLogs = null;
        var request = new WorkflowRequest
        {
            Project = _project, UserRequest = Request.Trim(), Kind = Kind, Package = Package, Logs = logs,
            WorkingVersionPath = string.IsNullOrWhiteSpace(WorkingVersionPath) ? null : WorkingVersionPath,
        };
        var observer = new UiObserver(this);
        var ct = _cts!.Token;
        var result = await Task.Run(() => _coordinator.RunWorkflowAsync(request, observer, ct));
        ShowResult(result);
    });

    private void ShowResult(WorkflowResult result)
    {
        Report = result.Report;
        RunStatus = result.Status;
        Escalation = result.Escalation;
        EscalationChoice = result.Escalation is { SuggestedModelKey: { } k } ? ModelChoices.FirstOrDefault(c => c.Key == k) : ModelChoice.Auto;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void BrowseWorkingVersion()
    {
        if (_dialogs.PickFile("Last known working version (ZIP)", ImportWorkflow.ZipFilter) is { } p) WorkingVersionPath = p;
    }

    // ---------------- Approvals ----------------

    [RelayCommand]
    private void ApproveChange(PendingChange? p) { if ((p ?? SelectedPending) is { } c) _approvals.Approve(c.Id); }

    [RelayCommand]
    private void RejectChange(PendingChange? p) { if ((p ?? SelectedPending) is { } c) _approvals.Reject(c.Id); }

    // ---------------- Escalation / try another model ----------------

    [RelayCommand]
    private Task TrySuggested() => Escalation is null ? Task.CompletedTask : ContinueWith(Escalation.SuggestedModelKey);

    [RelayCommand]
    private Task KeepCurrentModel() => Escalation is null ? Task.CompletedTask : ContinueWith(Escalation.CurrentModelKey);

    [RelayCommand]
    private Task TryChosenModel() => ContinueWith(EscalationChoice?.Key);

    private Task ContinueWith(string? modelKey) => Guard("Continuing repair…", async () =>
    {
        if (_project is null) return;
        var project = _project;
        var observer = new UiObserver(this);
        var package = Package;
        var ct = _cts!.Token;
        Escalation = null;
        var result = await Task.Run(() => _coordinator.ContinueRepairAsync(project, modelKey, package, observer, ct));
        ShowResult(result);
    });

    [RelayCommand]
    private Task TryAnotherModel() => Guard("Trying another model…", async () =>
    {
        var t = SelectedTask?.Snapshot;
        if (_project is null || t is null) { ErrorMessage = "Select an agent task on the board first."; return; }
        var project = _project;
        var observer = new UiObserver(this);
        var key = RetryModel?.Key;
        var exclude = key is null ? t.ModelsTried.ToHashSet() : null;
        var ct = _cts!.Token;
        var task = await Task.Run(() => _coordinator.RunAgentTaskAsync(project, t.Agent, t.Instructions, key, exclude, observer, ct));
        RunStatus = $"{_agents.Get(task.Agent).DisplayName} with {task.Model}: {task.State} — {task.ResultSummary ?? task.Error}";
    });

    [RelayCommand]
    private Task RunManualTask() => Guard("Running agent…", async () =>
    {
        if (_project is null) return;
        var project = _project;
        var agent = SelectedTask?.Snapshot is { State: AgentTaskState.Waiting } waiting ? waiting.Agent : ManualAgent;
        var instructions = SelectedTask?.Snapshot is { State: AgentTaskState.Waiting } w ? w.Instructions : ManualInstructions;
        if (string.IsNullOrWhiteSpace(instructions)) { ErrorMessage = "Enter instructions for the agent (or select a waiting task)."; return; }
        var key = ManualModel?.Key;
        var observer = new UiObserver(this);
        var ct = _cts!.Token;
        var task = await Task.Run(() => _coordinator.RunAgentTaskAsync(project, agent, instructions, key, null, observer, ct));
        RunStatus = $"{_agents.Get(task.Agent).DisplayName}: {task.State} — {task.ResultSummary ?? task.Error}";
    });

    // ---------------- Second opinion, handoff, restore ----------------

    [RelayCommand]
    private Task AskAnotherModel() => Guard("Asking another model and evaluating both proposals with the real compiler…", async () =>
    {
        if (_project is null || SelectedProposal is null) { ErrorMessage = "Select a proposed change first."; return; }
        var project = _project;
        var id = SelectedProposal.Id;
        var key = RetryModel?.Key;
        var observer = new UiObserver(this);
        var ct = _cts!.Token;
        var r = await Task.Run(() => _coordinator.SecondOpinionAsync(project, id, key, observer, ct));
        if (r.Error != null) { SecondOpinionText = "Second opinion failed: " + r.Error; return; }
        string Eval(ProposalEvaluation e) => $"{e.Label} ({e.ModelKey}): compile {(e.CompileSkipped ? "skipped" : e.CompilePassed ? "PASS" : "FAIL")}, validation {(e.ValidationErrors == 0 ? "PASS" : $"FAIL ({e.ValidationErrors})")}\n  {e.Summary}\n  Files: {string.Join(", ", e.Files)}{(e.TopDiagnostics.Count > 0 ? "\n  " + string.Join("\n  ", e.TopDiagnostics) : "")}";
        SecondOpinionText = $"""
            ORIGINAL PROPOSAL
            {Eval(r.Original!)}

            ALTERNATIVE PROPOSAL
            {Eval(r.Alternative!)}

            AGREEMENTS
            {string.Join("\n", r.Agreements.DefaultIfEmpty("(none)"))}

            DIFFERENCES
            {string.Join("\n", r.Differences.DefaultIfEmpty("(none)"))}

            ACTUAL VALIDATION EVIDENCE
            {r.EvidenceVerdict}

            Why this second model: {string.Join(" ", r.RoutingReasons)}
            """;
    });

    [RelayCommand]
    private async Task ApplySelectedProposal()
    {
        if (_project is null || SelectedProposal?.DataJson is null) return;
        var data = JsonSerializer.Deserialize<ProposedChangeData>(SelectedProposal.DataJson);
        if (data is null || data.Applied) { ErrorMessage = "Only proposals that have not been applied yet (e.g. second opinions) can be applied here."; return; }
        if (!_dialogs.Confirm("Apply proposal", $"Apply the proposal from {data.ModelKey} to {data.Edits.Count} file(s)? A revision is created first.")) return;
        await _projects.ApplyEditsWithHistoryAsync(_project, data.Edits, $"Applied proposal from {data.ModelKey}", SelectedProposal.Content,
            new Dictionary<string, string> { ["agent"] = data.Agent, ["model"] = data.ModelKey, ["approvedBy"] = "user" });
        if (_afterChanges != null) await _afterChanges();
        RunStatus = "Proposal applied (revision recorded). Build to verify it.";
    }

    [RelayCommand]
    private async Task RestoreBeforeProposal()
    {
        if (_project is null || SelectedProposal?.DataJson is null) return;
        var data = JsonSerializer.Deserialize<ProposedChangeData>(SelectedProposal.DataJson);
        var rev = data?.BeforeRevisionId is { } id ? (await _projects.History.ListAsync(_project)).FirstOrDefault(r => r.Id == id) : null;
        if (rev is null) { ErrorMessage = "No pre-change revision is recorded for this proposal."; return; }
        if (!_dialogs.Confirm("Restore before this agent change", $"Restore the project to the snapshot taken before {data!.Agent}'s change ({rev.TimestampUtc.LocalDateTime:g})? The current state is saved first.")) return;
        await _projects.History.RestoreAsync(_project, rev);
        if (_afterChanges != null) await _afterChanges();
        RunStatus = "Restored to before the agent change.";
    }

    [RelayCommand]
    private async Task ShowHandoff()
    {
        if (_project is null) return;
        var profile = _project.GameProfileId is { } pid ? await _profiles.GetAsync(pid) : null;
        HandoffText = await _context.BuildHandoffAsync(_project, profile, SelectedTask?.Snapshot, null);
        RunStatus = "Handoff ready on the Handoff tab. Any model or provider can continue from it.";
    }

    // ---------------- Team ----------------

    [RelayCommand]
    private async Task SaveTeam()
    {
        if (_project is null) return;
        var pk = await _knowledge.Repository.GetProjectAsync(_project.Id);
        pk.AISettings.RoutingMode = RoutingChoice?.Value;
        pk.AISettings.ControlLevel = ControlChoice?.Value;
        pk.AISettings.AgentModels = Team.Where(t => t.Selected.Key != null).ToDictionary(t => t.Agent.ToString(), t => t.Selected.Key!);
        await _knowledge.Repository.SaveProjectAsync(pk);
        UpdateTeamSummary();
        RunStatus = "AI team saved. Models can be changed at any time; agents continue from Mad Mod Studio's project knowledge.";
    }

    [RelayCommand]
    private async Task WhyThisModel(AgentModelSetting? setting)
    {
        if (_project is null || setting is null) return;
        var d = await _coordinator.PreviewRoutingAsync(_project, setting.Agent);
        WhyText = d.Model is null
            ? $"{setting.DisplayName}: no model can be selected — {d.Error}"
            : $"{setting.DisplayName} → {d.Model}\n  " + string.Join("\n  ", d.Reasons);
    }
}
