using System.Text;
using System.Text.Json;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.AI.Routing;
using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.ModAnalysis.Comparison;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.AI.Coordination;

/// <summary>
/// Orchestrates the multi-agent workflow: tool evidence → Lead plan → task graph (parallel where independent, one
/// writer per file) → actual compile/validate → bounded repair with model escalation → independent review → package.
/// Mad Mod Studio owns all state; models are interchangeable workers. Tool results always outrank AI claims.
/// </summary>
public sealed class AgentCoordinator : IAgentCoordinator
{
    private readonly IModelRouter _router;
    private readonly IModelCatalog _catalog;
    private readonly IModelPerformanceTracker _performance;
    private readonly ProjectKnowledgeService _knowledge;
    private readonly IAIContextBuilder _context;
    private readonly AgentCatalog _agents;
    private readonly AgentRunner _runner;
    private readonly AgentChangeService _changes;
    private readonly IChangeApprovalService _approval;
    private readonly IAIConsentService _consent;
    private readonly IAIProviderRegistry _providers;
    private readonly AIPolicy _policy;
    private readonly BudgetGuard _budget;
    private readonly ModBuildPipeline _pipeline;
    private readonly ProjectService _projects;
    private readonly GameProfileService _profiles;
    private readonly ModAnalyzer _analyzer;
    private readonly RepairService _repair;
    private readonly AppPaths _paths;
    private readonly ILogger<AgentCoordinator> _log;

    public AgentCoordinator(IModelRouter router, IModelCatalog catalog, IModelPerformanceTracker performance, ProjectKnowledgeService knowledge,
        IAIContextBuilder context, AgentCatalog agents, AgentRunner runner, AgentChangeService changes, IChangeApprovalService approval,
        IAIConsentService consent, IAIProviderRegistry providers, AIPolicy policy, BudgetGuard budget, ModBuildPipeline pipeline,
        ProjectService projects, GameProfileService profiles, ModAnalyzer analyzer, RepairService repair, AppPaths paths, ILogger<AgentCoordinator>? log = null)
    {
        _router = router; _catalog = catalog; _performance = performance; _knowledge = knowledge; _context = context; _agents = agents;
        _runner = runner; _changes = changes; _approval = approval; _consent = consent; _providers = providers; _policy = policy;
        _budget = budget; _pipeline = pipeline; _projects = projects; _profiles = profiles; _analyzer = analyzer; _repair = repair; _paths = paths;
        _log = log ?? NullLogger<AgentCoordinator>.Instance;
    }

    private sealed class RunContext
    {
        public required string RunId { get; init; }
        public required ModProject Project { get; init; }
        public required WorkflowRequest Request { get; init; }
        public GameProfile? Profile { get; init; }
        public IGameKnowledgeIndex? Index { get; init; }
        public required ProjectAISettings Settings { get; init; }
        public required AgentControlLevel Control { get; init; }
        public required RoutingMode Mode { get; init; }
        public required IAgentRunObserver Observer { get; init; }
        public required CancellationToken Ct { get; init; }
        public ModAnalysisReport? Analysis { get; set; }
        public List<LogReport> Logs { get; } = new();
        public VersionComparison? Comparison { get; set; }
        public string? DiagnosisReport { get; set; }
        public BuildResult? InitialBuild { get; set; }
        public string? PrimaryProvider { get; set; }
        public List<AgentTaskRecord> Tasks { get; } = new();
        public HashSet<string> ModelsUsed { get; } = new();
        public HashSet<string> WriterModels { get; } = new();
        public List<string> FailedAttempts { get; } = new();
        public List<(AgentTaskRecord Task, ModelPerformanceRecord Perf)> PendingImplementations { get; } = new();
        public List<string> ChangeLog { get; } = new();
        public bool ChangesApplied { get; set; }
        public int RepairAttempts { get; set; }
        public bool ConsentGiven { get; set; }
    }

    // =====================================================================================================
    // Public entry points
    // =====================================================================================================

    public async Task<WorkflowResult> RunWorkflowAsync(WorkflowRequest request, IAgentRunObserver? observer = null, CancellationToken ct = default)
    {
        var rc = await CreateContextAsync(request.Project, request, observer, ct).ConfigureAwait(false);
        var result = new WorkflowResult { RunId = rc.RunId };
        var pk = await _knowledge.Repository.GetProjectAsync(rc.Project.Id, ct).ConfigureAwait(false);
        pk.Objective = request.UserRequest;
        await _knowledge.Repository.SaveProjectAsync(pk, ct).ConfigureAwait(false);

        await GatherEvidenceAsync(rc).ConfigureAwait(false);

        // ---- Plan (Lead agent) ----
        var lead = NewTask(rc, AgentKind.Lead, AITaskType.Planning, "Plan the work", request.UserRequest, Array.Empty<string>());
        lead.State = AgentTaskState.Planning;
        await SaveTaskAsync(rc, lead).ConfigureAwait(false);
        var leadExec = await RunTaskCoreAsync(rc, lead, null, null).ConfigureAwait(false);
        var plan = leadExec?.Toolbox.Plan;
        if (plan is null)
        {
            await FinishTaskAsync(rc, lead, AgentTaskState.Failed, leadExec?.Ai.Error ?? leadExec?.Toolbox.PlanError ?? "The Lead agent did not submit a plan.").ConfigureAwait(false);
            result.Status = "Planning failed: " + lead.Error;
            return await FinalizeAsync(rc, result, null, null).ConfigureAwait(false);
        }
        await FinishTaskAsync(rc, lead, AgentTaskState.Complete, $"Planned {plan.Tasks.Count} task(s): {plan.Summary}").ConfigureAwait(false);
        pk.Summary = plan.Summary;
        pk.Plan = string.Join("\n", plan.Tasks.Select(t => $"{t.Id}. [{t.Agent}] {t.Title}{(t.DependsOn.Count > 0 ? " (after " + string.Join(", ", t.DependsOn) + ")" : "")}"));
        await _knowledge.Repository.SaveProjectAsync(pk, ct).ConfigureAwait(false);
        await _knowledge.AddAsync(new KnowledgeArtifact
        {
            ProjectId = rc.Project.Id, Kind = ArtifactKind.Decision, Title = "Task plan", Content = pk.Plan, Agent = "Lead", RunId = rc.RunId,
            Provider = lead.Provider, Model = lead.Model,
        }, ct).ConfigureAwait(false);

        var idMap = new Dictionary<string, string>();
        foreach (var p in plan.Tasks)
        {
            var t = NewTask(rc, p.Agent, TaskTypeFor(p.Agent, request.Kind), p.Title, p.Instructions, Array.Empty<string>());
            idMap[p.Id] = t.Id;
        }
        foreach (var p in plan.Tasks)
        {
            var t = rc.Tasks.First(x => x.Id == idMap[p.Id]);
            t.DependsOn = p.DependsOn.Select(d => idMap[d]).ToList();
            await SaveTaskAsync(rc, t).ConfigureAwait(false);
        }

        if (rc.Control == AgentControlLevel.Manual)
        {
            result.Status = "Plan ready. Agent Control is MANUAL: run each task yourself from the Agent Board.";
            return await FinalizeAsync(rc, result, null, null).ConfigureAwait(false);
        }

        // ---- Execute the task graph ----
        await ExecuteGraphAsync(rc).ConfigureAwait(false);

        // ---- Actual tools: compile, repair, validate, review, package ----
        return await RunGatesAsync(rc, result, null).ConfigureAwait(false);
    }

    public async Task<WorkflowResult> ContinueRepairAsync(ModProject project, string? modelKey, bool package, IAgentRunObserver? observer = null, CancellationToken ct = default)
    {
        var request = new WorkflowRequest { Project = project, UserRequest = "Continue repair", Kind = WorkflowKind.Repair, Package = package };
        var rc = await CreateContextAsync(project, request, observer, ct).ConfigureAwait(false);
        rc.Analysis = _analyzer.Analyze(project.ModRootPath, project.SourcePath, rc.Index, rc.Profile);
        rc.ChangesApplied = true;
        return await RunGatesAsync(rc, new WorkflowResult { RunId = rc.RunId }, modelKey).ConfigureAwait(false);
    }

    public async Task<AgentTaskRecord> RunAgentTaskAsync(ModProject project, AgentKind agent, string instructions, string? modelKey, IReadOnlySet<string>? excludeModels,
        IAgentRunObserver? observer = null, CancellationToken ct = default)
    {
        var request = new WorkflowRequest { Project = project, UserRequest = instructions, Kind = WorkflowKind.Improve, Package = false };
        var rc = await CreateContextAsync(project, request, observer, ct).ConfigureAwait(false);
        rc.Analysis = _analyzer.Analyze(project.ModRootPath, project.SourcePath, rc.Index, rc.Profile);
        var task = NewTask(rc, agent, TaskTypeFor(agent, WorkflowKind.Improve), $"{_agents.Get(agent).DisplayName} (manual)", instructions, Array.Empty<string>());
        await SaveTaskAsync(rc, task).ConfigureAwait(false);
        await RunPlannedTaskAsync(rc, task, modelKey, excludeModels).ConfigureAwait(false);
        if (rc.ChangesApplied)
        {
            var build = await BuildAsync(rc, false, null).ConfigureAwait(false);
            await ResolveImplementationRecordsAsync(rc, build).ConfigureAwait(false);
        }
        return task;
    }

    public async Task<RoutingDecision> PreviewRoutingAsync(ModProject project, AgentKind agent, CancellationToken ct = default)
    {
        var pk = await _knowledge.Repository.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
        return await _router.SelectAsync(new RoutingRequest
        {
            Agent = agent,
            TaskType = _agents.Get(agent).DefaultTaskType,
            Mode = pk.AISettings.RoutingMode ?? _policy.RoutingMode,
            ManualModel = pk.AISettings.AgentModels.GetValueOrDefault(agent.ToString()),
        }, ct).ConfigureAwait(false);
    }

    // =====================================================================================================
    // Context & evidence
    // =====================================================================================================

    private async Task<RunContext> CreateContextAsync(ModProject project, WorkflowRequest request, IAgentRunObserver? observer, CancellationToken ct)
    {
        var profile = project.GameProfileId is { } id ? await _profiles.GetAsync(id, ct).ConfigureAwait(false) : null;
        var pk = await _knowledge.Repository.GetProjectAsync(project.Id, ct).ConfigureAwait(false);
        return new RunContext
        {
            RunId = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
            Project = project,
            Request = request,
            Profile = profile,
            Index = _profiles.GetIndex(profile),
            Settings = pk.AISettings,
            Control = pk.AISettings.ControlLevel ?? _policy.ControlLevel,
            Mode = pk.AISettings.RoutingMode ?? _policy.RoutingMode,
            Observer = observer ?? NullRunObserver.Instance,
            Ct = ct,
        };
    }

    private void Emit(RunContext rc, string message) => rc.Observer.OnEvent($"{DateTime.Now:HH:mm:ss}  {message}");

    private async Task GatherEvidenceAsync(RunContext rc)
    {
        Emit(rc, "Tools: analyzing mod files…");
        rc.Analysis = await Task.Run(() => _analyzer.Analyze(rc.Project.ModRootPath, rc.Project.SourcePath, rc.Index, rc.Profile), rc.Ct).ConfigureAwait(false);
        await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.Evidence, $"Static analysis: {rc.Analysis.ModType}", rc.Analysis.Summary()), rc.Ct).ConfigureAwait(false);
        if (rc.Profile is null) Emit(rc, "Warning: no Game Profile assigned — agents cannot verify game APIs.");
        else if (rc.Index is null) Emit(rc, "Warning: Game Profile not indexed — run Reindex Game so agents can verify APIs.");

        Emit(rc, "Tools: compiling and validating the current state…");
        rc.InitialBuild = await BuildAsync(rc, false, null).ConfigureAwait(false);

        if (rc.Request.Logs.Count > 0 || rc.Request.WorkingVersionPath != null)
        {
            Emit(rc, "Tools: parsing logs and comparing versions…");
            var inputs = new RepairInputs { WorkingVersionPath = rc.Request.WorkingVersionPath };
            inputs.Logs.AddRange(rc.Request.Logs);
            var d = await _repair.DiagnoseAsync(rc.Project, inputs, null, rc.Ct).ConfigureAwait(false);
            rc.Logs.AddRange(d.Logs);
            rc.Comparison = d.Comparison;
            rc.DiagnosisReport = d.Report;
            foreach (var g in d.RelevantGroups.Take(10))
                await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.LogFinding, $"[{g.Category}] x{g.Count}: {Trim(g.First.Message, 120)}",
                    $"Line {g.First.LineNumber}: {g.First.Message}\n" + string.Join("\n", g.First.StackTrace.Take(5).Select(f => $"  at {f.Type}.{f.Method}"))), rc.Ct).ConfigureAwait(false);
            foreach (var c in d.LikelyCauses.Take(10))
                await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.Diagnostic, Trim(c, 160), c), rc.Ct).ConfigureAwait(false);
        }
    }

    private KnowledgeArtifact ToolArtifact(RunContext rc, ArtifactKind kind, string title, string content) => new()
    {
        ProjectId = rc.Project.Id, Kind = kind, Title = title, Content = content, RunId = rc.RunId,
        GameProfileId = rc.Profile?.Id, GameFingerprint = rc.Profile?.AssemblyFingerprint,
        Verified = true, Source = "Local tool", Agent = "Mad Mod Studio tools",
    };

    // =====================================================================================================
    // Tasks
    // =====================================================================================================

    private AgentTaskRecord NewTask(RunContext rc, AgentKind agent, AITaskType type, string title, string instructions, IReadOnlyList<string> deps)
    {
        var t = new AgentTaskRecord
        {
            RunId = rc.RunId, ProjectId = rc.Project.Id, Agent = agent, TaskType = type, Title = title, Instructions = instructions,
            DependsOn = deps.ToList(), State = AgentTaskState.Waiting,
        };
        lock (rc.Tasks) rc.Tasks.Add(t);
        return t;
    }

    private async Task SaveTaskAsync(RunContext rc, AgentTaskRecord t)
    {
        await _knowledge.Repository.SaveTaskAsync(t, rc.Ct).ConfigureAwait(false);
        rc.Observer.OnTaskChanged(t);
    }

    private async Task FinishTaskAsync(RunContext rc, AgentTaskRecord t, AgentTaskState state, string? summaryOrError)
    {
        lock (t)
        {
            t.State = state;
            if (state is AgentTaskState.Failed or AgentTaskState.Blocked) t.Error = summaryOrError;
            else t.ResultSummary = summaryOrError;
        }
        await SaveTaskAsync(rc, t).ConfigureAwait(false);
    }

    private AITaskType TaskTypeFor(AgentKind agent, WorkflowKind kind) => agent switch
    {
        AgentKind.CSharpHarmony => kind == WorkflowKind.Repair ? AITaskType.HarmonyRepair : AITaskType.HarmonyImplementation,
        AgentKind.XmlXPath => kind == WorkflowKind.Repair ? AITaskType.XmlRepair : AITaskType.XmlImplementation,
        _ => _agents.Get(agent).DefaultTaskType,
    };

    private string? CurrentProvider(RunContext rc) => _policy.AllowCrossProviderRouting ? null : rc.PrimaryProvider;

    /// <summary>Routes, runs the agent, records performance. Returns null when no model could be selected.</summary>
    private async Task<AgentExecutionResult?> RunTaskCoreAsync(RunContext rc, AgentTaskRecord task, string? modelKey, IReadOnlySet<string>? exclude,
        IReadOnlySet<string>? preferDifferentFrom = null, string? extraContext = null)
    {
        var decision = await _router.SelectAsync(new RoutingRequest
        {
            Agent = task.Agent,
            TaskType = task.TaskType,
            Mode = rc.Mode,
            ManualModel = modelKey ?? rc.Settings.AgentModels.GetValueOrDefault(task.Agent.ToString()),
            ExcludedModels = exclude ?? new HashSet<string>(),
            CurrentProvider = CurrentProvider(rc),
            PreferDifferentFrom = preferDifferentFrom ?? new HashSet<string>(),
        }, rc.Ct).ConfigureAwait(false);
        if (decision.Model is null)
        {
            await FinishTaskAsync(rc, task, AgentTaskState.Failed, decision.Error).ConfigureAwait(false);
            return null;
        }
        var model = decision.Model;
        rc.PrimaryProvider ??= model.ProviderId;

        if (!rc.ConsentGiven && _policy.AskBeforeEachRun)
        {
            var providers = _policy.AllowCrossProviderRouting
                ? string.Join(", ", _providers.All.Where(p => p.IsConfigured).Select(p => $"{p.DisplayName} ({p.Destination})"))
                : $"{model.ProviderName} ({_providers.Get(model.ProviderId)?.Destination})";
            var categories = new List<string> { "Your request and the project summary", "Game API names/signatures and XML entries the agents look up" };
            if (_policy.AllowSourceToExternal) categories.Add("Project source/XML files the agents explicitly read");
            if (_policy.AllowLogsToExternal && rc.Logs.Count > 0) categories.Add("Excerpts from attached logs");
            categories.Add("Compiler diagnostics and validator findings");
            if (EstimateRunCost() is { } est) categories.Add(est);
            if (!await _consent.ConfirmAsync(new AIEgressNotice(providers, "Agent workflow", categories, providers), rc.Ct).ConfigureAwait(false))
            {
                await FinishTaskAsync(rc, task, AgentTaskState.Failed, "Cancelled: you declined sending data to the AI provider.").ConfigureAwait(false);
                return null;
            }
            rc.ConsentGiven = true;
        }

        lock (task)
        {
            task.Provider = model.ProviderName;
            task.Model = model.Key;
            task.RoutingReasons = decision.Reasons.ToList();
            if (!task.ModelsTried.Contains(model.Key)) task.ModelsTried.Add(model.Key);
            if (task.State != AgentTaskState.Planning) task.State = AgentTaskState.Working;
        }
        rc.ModelsUsed.Add(model.Key);
        await SaveTaskAsync(rc, task).ConfigureAwait(false);
        Emit(rc, $"{_agents.Get(task.Agent).DisplayName}: {task.Title} → {model}");

        var ctx = new ToolContext
        {
            Project = rc.Project, Profile = rc.Profile, Index = rc.Index, Projects = _projects, Knowledge = _knowledge, Policy = _policy, Task = task,
            Provider = model.ProviderId, Model = model.ModelId, Analysis = rc.Analysis, Logs = rc.Logs, Comparison = rc.Comparison,
            DiagnosisReport = rc.DiagnosisReport, PlannableAgents = _agents.Specialists.Select(s => s.Kind).ToHashSet(),
        };
        var perf = await _performance.StartAsync(model.ProviderId, model.ModelId, task.Agent, task.TaskType, rc.Project.Id, task.Id, rc.Ct).ConfigureAwait(false);
        var progress = new SyncProgress<AIEvent>(e => { if (e.Kind is AIEventKind.ToolCall or AIEventKind.Warning) rc.Observer.OnTaskChanged(task); });
        AgentExecutionResult exec;
        try
        {
            exec = await _runner.ExecuteAsync(ctx, model, task.Instructions, progress, 0, extraContext, rc.Ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            perf.Outcome = TaskOutcome.Cancelled;
            await _performance.CompleteAsync(perf, CancellationToken.None).ConfigureAwait(false);
            await FinishTaskAsync(rc, task, AgentTaskState.Failed, "Cancelled.").ConfigureAwait(false);
            throw;
        }
        perf.LatencyMs = exec.LatencyMs;
        perf.InputTokens = exec.Ai.Usage.InputTokens;
        perf.OutputTokens = exec.Ai.Usage.OutputTokens;
        perf.EstimatedCostUsd = exec.EstimatedCost;

        var def = _agents.Get(task.Agent);
        var produced = exec.Toolbox.Recorded.Count > 0 || exec.Toolbox.Plan != null || exec.Toolbox.Review != null || exec.Toolbox.Proposal != null;
        if (!exec.Ai.Success && !produced)
        {
            perf.Outcome = TaskOutcome.Failure;
            perf.Notes = exec.Ai.Error;
            await _performance.CompleteAsync(perf, rc.Ct).ConfigureAwait(false);
            return exec;
        }
        if (def.CanWrite && exec.Toolbox.Proposal != null)
        {
            // Outcome is decided by the real compiler/validators later.
            rc.PendingImplementations.Add((task, perf));
            await _performance.CompleteAsync(perf, rc.Ct).ConfigureAwait(false);
        }
        else
        {
            // Keep the agent's summary as shared knowledge even if it didn't call record_finding.
            if (exec.Toolbox.Recorded.Count == 0 && !string.IsNullOrWhiteSpace(exec.Ai.FinalText) && task.Agent is not (AgentKind.Lead or AgentKind.Validator))
                exec.Toolbox.Recorded.Add(await _knowledge.AddAsync(new KnowledgeArtifact
                {
                    ProjectId = rc.Project.Id, Kind = ArtifactKind.Finding, Title = $"{def.DisplayName}: {task.Title}", Content = exec.Ai.FinalText,
                    Agent = task.Agent.ToString(), Provider = model.ProviderId, Model = model.ModelId, TaskId = task.Id, RunId = rc.RunId,
                    GameProfileId = rc.Profile?.Id, GameFingerprint = rc.Profile?.AssemblyFingerprint,
                }, rc.Ct).ConfigureAwait(false));
            perf.Outcome = def.CanWrite ? TaskOutcome.Failure : TaskOutcome.Success;
            if (def.CanWrite) perf.Notes = "Write agent produced no proposal.";
            await _performance.CompleteAsync(perf, rc.Ct).ConfigureAwait(false);
        }
        return exec;
    }

    private async Task RunPlannedTaskAsync(RunContext rc, AgentTaskRecord task, string? modelKey = null, IReadOnlySet<string>? exclude = null)
    {
        var exec = await RunTaskCoreAsync(rc, task, modelKey, exclude).ConfigureAwait(false);
        if (exec is null) return;
        var def = _agents.Get(task.Agent);
        if (!exec.Ai.Success && exec.Toolbox.Recorded.Count == 0 && exec.Toolbox.Proposal is null)
        {
            await FinishTaskAsync(rc, task, AgentTaskState.Failed, exec.Ai.Error ?? "The agent failed.").ConfigureAwait(false);
            return;
        }
        if (def.CanWrite)
        {
            if (exec.Toolbox.Proposal is null)
            {
                await FinishTaskAsync(rc, task, AgentTaskState.Failed, "No change was proposed. " + Trim(exec.Ai.FinalText, 400)).ConfigureAwait(false);
                return;
            }
            var applied = await ApplyProposalAsync(rc, task, exec).ConfigureAwait(false);
            await FinishTaskAsync(rc, task, applied ? AgentTaskState.Complete : AgentTaskState.Failed,
                applied ? exec.Toolbox.Proposal.Summary : task.Error ?? "The change was not applied.").ConfigureAwait(false);
            return;
        }
        await FinishTaskAsync(rc, task, AgentTaskState.Complete,
            exec.Toolbox.Recorded.Count > 0 ? $"{exec.Toolbox.Recorded.Count} finding(s): " + string.Join("; ", exec.Toolbox.Recorded.Take(4).Select(r => r.Title)) : Trim(exec.Ai.FinalText, 400)).ConfigureAwait(false);
    }

    private async Task<bool> ApplyProposalAsync(RunContext rc, AgentTaskRecord task, AgentExecutionResult exec, bool allowRetryOnConflict = true)
    {
        var proposal = exec.Toolbox.Proposal!;
        var model = exec.Model;
        var diff = BuildDiff(rc.Project, proposal);
        if (rc.Control != AgentControlLevel.Automatic)
        {
            lock (task) task.State = AgentTaskState.NeedsReview;
            await SaveTaskAsync(rc, task).ConfigureAwait(false);
            Emit(rc, $"{_agents.Get(task.Agent).DisplayName} proposes changes to {proposal.Edits.Count} file(s) — waiting for your approval.");
            var approved = await _approval.RequestAsync(new PendingChange
            {
                ProjectId = rc.Project.Id, TaskId = task.Id, Agent = _agents.Get(task.Agent).DisplayName, Provider = model.ProviderName, Model = model.Key,
                Summary = proposal.Summary, Edits = proposal.Edits, Diff = diff,
            }, rc.Ct).ConfigureAwait(false);
            if (!approved)
            {
                await _knowledge.AddAsync(new KnowledgeArtifact
                {
                    ProjectId = rc.Project.Id, Kind = ArtifactKind.Decision, Title = $"User rejected change from {task.Agent}", Content = proposal.Summary,
                    Agent = "User", TaskId = task.Id, RunId = rc.RunId, Provider = model.ProviderId, Model = model.ModelId,
                }, rc.Ct).ConfigureAwait(false);
                lock (task) task.Error = "Rejected by you.";
                MarkPending(rc, task, TaskOutcome.Cancelled);
                return false;
            }
            lock (task) task.State = AgentTaskState.Working;
        }
        var metadata = new Dictionary<string, string>
        {
            ["agent"] = _agents.Get(task.Agent).DisplayName, ["provider"] = model.ProviderName, ["model"] = model.Key,
            ["task"] = task.Title, ["taskId"] = task.Id, ["runId"] = rc.RunId,
        };
        var result = await _changes.ApplyAsync(rc.Project, proposal, exec.Toolbox.ReadHashes, $"{task.Agent} ({model.Key})", metadata, $"AI: {task.Title}", rc.Ct).ConfigureAwait(false);
        if (!result.Applied)
        {
            Emit(rc, $"Conflict: {string.Join(" ", result.Conflicts)}");
            if (allowRetryOnConflict)
            {
                // Re-run once with fresh reads so the agent builds on the other agent's work instead of overwriting it.
                var retry = await RunTaskCoreAsync(rc, task, model.Key, null, null,
                    "\nYOUR PREVIOUS PROPOSAL CONFLICTED: " + string.Join(" ", result.Conflicts) + " Re-read the files and propose again on top of the current contents.").ConfigureAwait(false);
                if (retry?.Toolbox.Proposal != null) return await ApplyProposalAsync(rc, task, retry, false).ConfigureAwait(false);
            }
            lock (task) task.Error = "Conflict: " + string.Join(" ", result.Conflicts);
            MarkPending(rc, task, TaskOutcome.Failure);
            return false;
        }
        lock (task) foreach (var e in proposal.Edits) if (!task.FilesWritten.Contains(e.RelativePath)) task.FilesWritten.Add(e.RelativePath);
        rc.ChangesApplied = true;
        rc.WriterModels.Add(model.Key);
        rc.ChangeLog.Add($"{_agents.Get(task.Agent).DisplayName} ({model}): {string.Join(", ", proposal.Edits.Select(e => (e.IsDelete ? "deleted " : "") + e.RelativePath))} — {Trim(proposal.Summary, 200)}");
        foreach (var p in rc.PendingImplementations.Where(p => p.Task == task)) p.Perf.BeforeRevisionId = result.Before?.Id;
        await _knowledge.AddAsync(new KnowledgeArtifact
        {
            ProjectId = rc.Project.Id, Kind = ArtifactKind.ProposedChange, Title = $"{_agents.Get(task.Agent).DisplayName}: {task.Title}",
            Content = $"{proposal.Summary}\nFiles: {string.Join(", ", proposal.Edits.Select(e => e.RelativePath))}",
            DataJson = JsonSerializer.Serialize(new ProposedChangeData(task.Agent.ToString(), task.Id, task.Title, task.Instructions, model.Key, proposal.Edits, result.Before?.Id, result.After?.Id, true)),
            Agent = task.Agent.ToString(), Provider = model.ProviderId, Model = model.ModelId, TaskId = task.Id, RunId = rc.RunId,
            GameProfileId = rc.Profile?.Id, GameFingerprint = rc.Profile?.AssemblyFingerprint,
        }, rc.Ct).ConfigureAwait(false);
        Emit(rc, $"Applied {proposal.Edits.Count} file change(s) from {_agents.Get(task.Agent).DisplayName} (revision recorded).");
        return true;
    }

    private static void MarkPending(RunContext rc, AgentTaskRecord task, TaskOutcome outcome)
    {
        foreach (var p in rc.PendingImplementations.Where(p => p.Task == task && p.Perf.Outcome == TaskOutcome.Pending)) p.Perf.Outcome = outcome;
    }

    private static string BuildDiff(ModProject project, ProposedChanges proposal)
    {
        var sb = new StringBuilder();
        foreach (var e in proposal.Edits)
        {
            var full = Path.Combine(project.SourcePath, e.RelativePath);
            var old = File.Exists(full) ? File.ReadAllText(full) : "";
            if (e.IsDelete) { sb.AppendLine($"--- a/{e.RelativePath}\n+++ (deleted)"); continue; }
            var (d, _, _) = TextDiff.Unified(old, e.NewContent!, File.Exists(full) ? "a/" + e.RelativePath : "/dev/null", "b/" + e.RelativePath);
            sb.AppendLine(string.IsNullOrEmpty(d) ? $"(no textual change: {e.RelativePath})" : d);
        }
        return sb.ToString();
    }

    private async Task ExecuteGraphAsync(RunContext rc)
    {
        var running = new Dictionary<Task, AgentTaskRecord>();
        var limit = Math.Max(1, _policy.MaxConcurrentAgents);
        while (true)
        {
            rc.Ct.ThrowIfCancellationRequested();
            List<AgentTaskRecord> waiting;
            lock (rc.Tasks) waiting = rc.Tasks.Where(t => t.State == AgentTaskState.Waiting && t.Agent != AgentKind.Lead).ToList();
            foreach (var t in waiting)
            {
                var failedDep = t.DependsOn.Select(d => rc.Tasks.FirstOrDefault(x => x.Id == d)).FirstOrDefault(d => d is { State: AgentTaskState.Failed or AgentTaskState.Blocked or AgentTaskState.Skipped });
                if (failedDep != null) await FinishTaskAsync(rc, t, AgentTaskState.Blocked, $"Dependency '{failedDep.Title}' did not complete.").ConfigureAwait(false);
            }
            if (await _budget.CheckAsync(_policy, rc.Project.Id, 0, rc.Ct).ConfigureAwait(false) is { } budgetError)
            {
                foreach (var t in waiting.Where(t => t.State == AgentTaskState.Waiting)) await FinishTaskAsync(rc, t, AgentTaskState.Skipped, budgetError).ConfigureAwait(false);
            }
            var ready = waiting.Where(t => t.State == AgentTaskState.Waiting && t.DependsOn.All(d => rc.Tasks.FirstOrDefault(x => x.Id == d)?.State == AgentTaskState.Complete)).ToList();
            foreach (var t in ready)
            {
                if (running.Count >= limit) break;
                running[Task.Run(() => RunPlannedTaskAsync(rc, t), rc.Ct)] = t;
            }
            if (running.Count == 0) break;
            var done = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var task = running[done];
            running.Remove(done);
            try { await done.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Agent task {Task} crashed", task.Title);
                await FinishTaskAsync(rc, task, AgentTaskState.Failed, "Internal error: " + ex.Message).ConfigureAwait(false);
            }
        }
    }

    // =====================================================================================================
    // Gates: compile → repair (with escalation) → validate → review → package
    // =====================================================================================================

    private static bool Passed(BuildResult b) => (b.CompileSkipped || b.CompileSucceeded) && b.Validation is { HasErrors: false };

    private async Task<BuildResult> BuildAsync(RunContext rc, bool package, string? newVersion)
    {
        var build = await Task.Run(() => _pipeline.RunAsync(rc.Project, new BuildOptions { Package = package, NewVersion = newVersion }, null, rc.Ct), rc.Ct).ConfigureAwait(false);
        var errors = build.AllDiagnostics.Where(d => d.Severity >= Severity.Warning).ToList();
        var compileTitle = build.CompileSkipped ? "Compile: SKIPPED (no C# source)"
            : build.CompileSucceeded ? $"Compile: PASS ({build.Compiles.Sum(c => c.Result.WarningCount)} warning(s))"
            : $"Compile: FAIL ({build.Compiles.Sum(c => c.Result.ErrorCount)} error(s))";
        await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.BuildResult, compileTitle,
            errors.Count == 0 ? "No compiler diagnostics." : string.Join("\n", errors.Take(60).Select(d => d.ToString()))), rc.Ct).ConfigureAwait(false);
        var v = build.Validation;
        var findings = v?.Findings.Where(f => f.Severity >= Severity.Warning).ToList() ?? new();
        await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.ValidationResult,
            v is null ? "Validation: not run" : v.HasErrors ? $"Validation: FAIL ({v.ErrorCount} error(s), {v.WarningCount} warning(s))" : $"Validation: PASS ({v.WarningCount} warning(s))",
            findings.Count == 0 ? "No validation errors or warnings." : string.Join("\n", findings.Take(60).Select(f => $"{f.Severity.ToString().ToUpperInvariant()} [{f.ValidatorId}] {f.Message}{(f.FilePath != null ? $" ({f.FilePath}{(f.Line != null ? ":" + f.Line : "")})" : "")}"))), rc.Ct).ConfigureAwait(false);
        Emit(rc, $"Tools: {compileTitle}; {(v is null ? "validation not run" : v.HasErrors ? $"validation FAIL ({v.ErrorCount})" : "validation PASS")}{(build.Package?.Success == true ? "; packaged " + build.Package.ZipPath : "")}");
        rc.Analysis = build.Analysis ?? rc.Analysis;
        return build;
    }

    private async Task ResolveImplementationRecordsAsync(RunContext rc, BuildResult build)
    {
        foreach (var (task, perf) in rc.PendingImplementations.Where(p => p.Perf.Outcome == TaskOutcome.Pending).ToList())
        {
            perf.CompilePassed = build.CompileSkipped ? null : build.CompileSucceeded;
            perf.ValidationPassed = build.Validation is { } v ? !v.HasErrors : null;
            perf.Outcome = Passed(build) ? TaskOutcome.Success : TaskOutcome.Failure;
            await _performance.CompleteAsync(perf, rc.Ct).ConfigureAwait(false);
        }
    }

    private async Task<WorkflowResult> RunGatesAsync(RunContext rc, WorkflowResult result, string? forcedRepairModel)
    {
        var build = await BuildAsync(rc, false, null).ConfigureAwait(false);
        await ResolveImplementationRecordsAsync(rc, build).ConfigureAwait(false);
        if (!Passed(build))
        {
            var (fixedBuild, escalation) = await RepairLoopAsync(rc, build, forcedRepairModel).ConfigureAwait(false);
            build = fixedBuild;
            if (escalation != null)
            {
                result.Blocked = true;
                result.Escalation = escalation;
                result.Status = escalation.Message;
                return await FinalizeAsync(rc, result, build, null).ConfigureAwait(false);
            }
        }
        result.FinalBuild = build;
        if (!Passed(build))
        {
            result.Status = $"Stopped: the mod still fails ({(build.CompileSucceeded || build.CompileSkipped ? "" : "compile, ")}{build.Validation?.ErrorCount ?? 0} validation error(s)). Review the diagnostics or try another model.";
            return await FinalizeAsync(rc, result, build, null).ConfigureAwait(false);
        }

        if (rc.ChangesApplied)
        {
            result.Review = await RunReviewAsync(rc).ConfigureAwait(false);
            await _knowledge.RecordSuccessPatternAsync(rc.Project, rc.Profile, $"{rc.Request.Kind}: {Trim(rc.Request.UserRequest, 80)}",
                string.Join("\n", rc.ChangeLog), string.Join(", ", rc.WriterModels), string.Join(", ", rc.WriterModels), rc.Ct).ConfigureAwait(false);
        }

        if (rc.Request.Package)
        {
            var version = rc.Request.BumpVersion && rc.ChangesApplied ? ModInfoFile.IncrementVersion(rc.Project.Version) ?? rc.Project.Version : rc.Project.Version;
            var packaged = await BuildAsync(rc, true, version).ConfigureAwait(false);
            result.FinalBuild = packaged;
            result.PackagePath = packaged.Package?.Success == true ? packaged.Package.ZipPath : null;
            result.Success = packaged.Succeeded;
            result.Status = packaged.Succeeded ? $"Complete: {result.PackagePath}" : "Packaging did not complete: " + packaged.Summary;
        }
        else
        {
            result.Success = true;
            result.Status = "Complete: the mod compiles and validates.";
        }
        return await FinalizeAsync(rc, result, result.FinalBuild, null).ConfigureAwait(false);
    }

    private async Task<(BuildResult Build, EscalationSuggestion? Escalation)> RepairLoopAsync(RunContext rc, BuildResult build, string? forcedModel)
    {
        var task = NewTask(rc, AgentKind.CompilerRepair, AITaskType.CompilerRepair, "Repair compile/validation errors",
            "Make the mod compile and pass validation against the installed game. Use the LATEST BUILD and LATEST VALIDATION sections and the DO NOT REPEAT list.", Array.Empty<string>());
        await SaveTaskAsync(rc, task).ConfigureAwait(false);
        var failures = new Dictionary<string, int>();
        var tried = new HashSet<string>();
        string? current = forcedModel;
        while (rc.RepairAttempts < _policy.MaxRepairAttempts)
        {
            rc.RepairAttempts++;
            var exclude = failures.Where(f => f.Value >= _policy.EscalationFailureThreshold && f.Key != current).Select(f => f.Key).ToHashSet();
            var exec = await RunTaskCoreAsync(rc, task, current, exclude).ConfigureAwait(false);
            if (exec is null) return (build, null);
            var modelKey = exec.Model.Key;
            current = modelKey;
            tried.Add(modelKey);
            var attemptPerf = rc.PendingImplementations.LastOrDefault(p => p.Task == task && p.Perf.Outcome == TaskOutcome.Pending).Perf;
            bool attemptOk = false;
            if (exec.Toolbox.Proposal != null && await ApplyProposalAsync(rc, task, exec).ConfigureAwait(false))
            {
                var errorsBefore = build.AllDiagnostics.Count(d => d.Severity == Severity.Error) + (build.Validation?.ErrorCount ?? 0);
                build = await BuildAsync(rc, false, null).ConfigureAwait(false);
                attemptOk = Passed(build);
                if (attemptPerf != null)
                {
                    attemptPerf.CompilePassed = build.CompileSkipped ? null : build.CompileSucceeded;
                    attemptPerf.ValidationPassed = build.Validation is { } v ? !v.HasErrors : null;
                    attemptPerf.RepairAttempts = rc.RepairAttempts;
                    attemptPerf.Outcome = attemptOk ? TaskOutcome.Success : TaskOutcome.Failure;
                    await _performance.CompleteAsync(attemptPerf, rc.Ct).ConfigureAwait(false);
                }
                if (!attemptOk)
                {
                    var diags = build.AllDiagnostics.Select(d => d.ToModDiagnostic()).Concat(build.Validation?.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.ToDiagnostic()) ?? Enumerable.Empty<ModDiagnostic>()).ToList();
                    var fa = await _knowledge.RecordFailedAttemptAsync(rc.Project, rc.Profile, exec.Toolbox.Proposal.Summary, diags, task.Agent.ToString(), exec.Model.ProviderId, exec.Model.ModelId, task.Id, rc.RunId, rc.Ct).ConfigureAwait(false);
                    rc.FailedAttempts.Add($"Attempt {rc.RepairAttempts} ({exec.Model}): {Trim(exec.Toolbox.Proposal.Summary, 160)} → {fa.Content.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT"))}");
                    Emit(rc, $"Repair attempt {rc.RepairAttempts} with {exec.Model} did not pass ({errorsBefore} → {diags.Count} problem(s)).");
                }
            }
            else
            {
                if (attemptPerf != null) { attemptPerf.Outcome = TaskOutcome.Failure; await _performance.CompleteAsync(attemptPerf, rc.Ct).ConfigureAwait(false); }
                rc.FailedAttempts.Add($"Attempt {rc.RepairAttempts} ({exec.Model}): no applicable change. {Trim(exec.Ai.Error ?? exec.Ai.FinalText, 160)}");
                Emit(rc, $"Repair attempt {rc.RepairAttempts} with {exec.Model} produced no applicable change.");
            }
            if (attemptOk)
            {
                await FinishTaskAsync(rc, task, AgentTaskState.Complete, $"Repaired after {rc.RepairAttempts} attempt(s) with {exec.Model}.").ConfigureAwait(false);
                return (build, null);
            }

            failures[modelKey] = failures.GetValueOrDefault(modelKey) + 1;
            if (failures[modelKey] >= _policy.EscalationFailureThreshold && rc.RepairAttempts < _policy.MaxRepairAttempts)
            {
                var candidate = await _router.SelectAsync(new RoutingRequest
                {
                    Agent = AgentKind.CompilerRepair, TaskType = AITaskType.CompilerRepair, Mode = rc.Mode == RoutingMode.Manual ? RoutingMode.Auto : rc.Mode,
                    ExcludedModels = tried, CurrentProvider = null, FailuresOnThisTask = failures, ForSuggestionOnly = true,
                }, rc.Ct).ConfigureAwait(false);
                if (candidate.Model is { } next)
                {
                    var sameProvider = next.ProviderId == exec.Model.ProviderId;
                    var canAuto = _policy.AutoEscalate && tried.Count < _policy.MaxModelsPerTask && (_policy.AllowCrossProviderRouting || sameProvider);
                    if (canAuto)
                    {
                        Emit(rc, $"Escalating: {exec.Model} failed {failures[modelKey]} repair attempt(s); trying {next}.");
                        current = next.Key;
                        continue;
                    }
                    var message = $"Current model failed {failures[modelKey]} repair attempt(s). Suggested: try {next}.";
                    await FinishTaskAsync(rc, task, AgentTaskState.Blocked, message).ConfigureAwait(false);
                    return (build, new EscalationSuggestion(rc.RunId, task.Id, task.Agent, modelKey, next.Key, next.ToString(), candidate.Reasons, message));
                }
            }
        }
        await FinishTaskAsync(rc, task, AgentTaskState.Failed, $"Stopped after {rc.RepairAttempts} repair attempt(s) (limit {_policy.MaxRepairAttempts}).").ConfigureAwait(false);
        return (build, null);
    }

    private async Task<ReviewVerdict?> RunReviewAsync(RunContext rc)
    {
        var task = NewTask(rc, AgentKind.Validator, AITaskType.Review, "Independent review of the changes",
            "Review every change made in this run against the actual tool evidence (latest build, validation, verified API findings). Do not simply agree.", Array.Empty<string>());
        await SaveTaskAsync(rc, task).ConfigureAwait(false);
        var exec = await RunTaskCoreAsync(rc, task, null, null, rc.WriterModels, "\nCHANGES MADE IN THIS RUN:\n" + string.Join("\n", rc.ChangeLog)).ConfigureAwait(false);
        if (exec?.Toolbox.Review is not { } review)
        {
            if (task.State != AgentTaskState.Failed) await FinishTaskAsync(rc, task, AgentTaskState.Failed, exec?.Ai.Error ?? "No review verdict was submitted.").ConfigureAwait(false);
            return null;
        }
        await _knowledge.AddAsync(new KnowledgeArtifact
        {
            ProjectId = rc.Project.Id, Kind = ArtifactKind.Review, Title = $"Review: {review.Verdict.ToUpperInvariant()}",
            Content = $"{review.Summary}\nIssues:\n{string.Join("\n", review.Issues.Select(i => "  - " + i))}\nEvidence checked: {string.Join(", ", review.EvidenceChecked)}",
            Agent = task.Agent.ToString(), Provider = exec.Model.ProviderId, Model = exec.Model.ModelId, TaskId = task.Id, RunId = rc.RunId,
        }, rc.Ct).ConfigureAwait(false);
        await FinishTaskAsync(rc, task, AgentTaskState.Complete, $"{review.Verdict.ToUpperInvariant()}: {review.Summary}").ConfigureAwait(false);
        return review;
    }

    // =====================================================================================================
    // Report
    // =====================================================================================================

    private async Task<WorkflowResult> FinalizeAsync(RunContext rc, WorkflowResult result, BuildResult? build, string? note)
    {
        lock (rc.Tasks) result.Tasks.AddRange(rc.Tasks);
        result.ModelsUsed.AddRange(rc.ModelsUsed);
        result.FailedAttempts.AddRange(rc.FailedAttempts);
        var sb = new StringBuilder();
        sb.AppendLine(result.Success ? (rc.Request.Kind == WorkflowKind.Repair ? "REPAIR COMPLETE" : "COMPLETE") : result.Blocked ? "NEEDS YOUR DECISION" : "NOT COMPLETE");
        sb.AppendLine(new string('=', 40));
        sb.AppendLine($"Project: {rc.Project.Name} {rc.Project.Version}");
        if (rc.Project.ImportedFrom != null) sb.AppendLine($"Original: {Path.GetFileName(rc.Project.ImportedFrom)} (preserved, unmodified)");
        if (result.PackagePath != null) sb.AppendLine($"Output: {result.PackagePath}");
        sb.AppendLine($"Status: {result.Status}");
        sb.AppendLine();
        if (rc.InitialBuild is { } ib)
            sb.AppendLine($"What was broken (before): compile {(ib.CompileSkipped ? "skipped" : ib.CompileSucceeded ? "PASS" : $"FAIL ({ib.AllDiagnostics.Count(d => d.Severity == Severity.Error)} errors)")}, validation {(ib.Validation?.HasErrors == true ? $"FAIL ({ib.Validation.ErrorCount} errors)" : "PASS")}{(rc.Logs.Count > 0 ? $", {rc.Logs.Sum(l => l.Groups.Count)} distinct log problem(s)" : "")}.");
        if (rc.ChangeLog.Count > 0)
        {
            sb.AppendLine("What changed:");
            foreach (var c in rc.ChangeLog) sb.AppendLine("  - " + c);
        }
        var agentsUsed = rc.Tasks.Where(t => t.Model != null).Select(t => t.Agent).Distinct().ToList();
        sb.AppendLine($"Agents used: {agentsUsed.Count} ({string.Join(", ", agentsUsed.Select(a => _agents.Get(a).DisplayName))})");
        sb.AppendLine($"Models used: {(rc.ModelsUsed.Count == 0 ? "none" : string.Join(", ", rc.ModelsUsed))}");
        if (rc.FailedAttempts.Count > 0)
        {
            sb.AppendLine("Failed attempts:");
            foreach (var f in rc.FailedAttempts) sb.AppendLine("  - " + f);
        }
        if (build != null)
        {
            sb.AppendLine($"Compilation: {(build.CompileSkipped ? "SKIPPED (no source)" : build.CompileSucceeded ? "PASS" : "FAIL")}");
            sb.AppendLine($"Validation: {(build.Validation is null ? "NOT RUN" : build.Validation.HasErrors ? $"FAIL ({build.Validation.ErrorCount} errors)" : "PASS")}");
            sb.AppendLine($"Package: {(build.Package?.Success == true && !build.PackagedWithErrors ? "PASS" : build.Package is null ? "NOT CREATED" : "FAILED")}");
        }
        if (rc.ChangeLog.Count > 0 && result.Success)
            sb.AppendLine("Why this repair was selected: it is the latest applied change after which the real compiler and validators passed.");
        if (result.Review is { } rv) sb.AppendLine($"Independent review: {rv.Verdict.ToUpperInvariant()} — {rv.Summary}{(rv.Issues.Count > 0 ? "\n  Concerns: " + string.Join("; ", rv.Issues) : "")}");
        if (rc.Analysis is { } a)
            sb.AppendLine($"Compatibility: server-side {a.Side.Result}, EAC {a.Eac.Result} (evidence-based assessments, not guarantees).");
        if (result.Escalation is { } esc) sb.AppendLine($"Suggestion: {esc.Message}\n  Why: {string.Join(" ", esc.Reasons)}");
        if (note != null) sb.AppendLine(note);
        if (_budget.UnknownCostSeen) sb.AppendLine("Cost: some models have no configured price, so spend could not be fully estimated.");
        else if (_budget.SessionSpend > 0) sb.AppendLine($"Estimated AI cost this session: ${_budget.SessionSpend:0.000}");
        result.Report = sb.ToString();
        await _knowledge.AddAsync(ToolArtifact(rc, ArtifactKind.ImplementationResult, $"Run {rc.RunId}: {(result.Success ? "complete" : result.Blocked ? "needs decision" : "not complete")}", result.Report), CancellationToken.None).ConfigureAwait(false);
        Emit(rc, result.Status);
        return result;
    }

    private string? EstimateRunCost()
    {
        var spent = _budget.SessionSpend;
        return _policy.SessionBudgetUsd is { } b ? $"Session budget ${b:0.00} (estimated ${spent:0.00} used so far)" : null;
    }

    private static string Trim(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n] + "…";

    // =====================================================================================================
    // Second opinion
    // =====================================================================================================

    public async Task<SecondOpinionResult> SecondOpinionAsync(ModProject project, long proposalArtifactId, string? modelKey, IAgentRunObserver? observer = null, CancellationToken ct = default)
    {
        var result = new SecondOpinionResult();
        var artifact = (await _knowledge.ListAsync(project.Id, ArtifactKind.ProposedChange, 1000, ct).ConfigureAwait(false)).FirstOrDefault(a => a.Id == proposalArtifactId);
        if (artifact?.DataJson is null || JsonSerializer.Deserialize<ProposedChangeData>(artifact.DataJson) is not { } data)
        {
            result.Error = "That proposal could not be found.";
            return result;
        }
        var request = new WorkflowRequest { Project = project, UserRequest = "Second opinion: " + data.TaskTitle, Kind = WorkflowKind.Improve, Package = false };
        var rc = await CreateContextAsync(project, request, observer, ct).ConfigureAwait(false);
        rc.Analysis = _analyzer.Analyze(project.ModRootPath, project.SourcePath, rc.Index, rc.Profile);
        var agent = Enum.Parse<AgentKind>(data.Agent);
        var task = NewTask(rc, agent, TaskTypeFor(agent, WorkflowKind.Repair), "Second opinion: " + data.TaskTitle,
            data.Instructions + "\nSolve this independently. Your proposal will be compared with another model's using the real compiler and validators; it will not be applied automatically.", Array.Empty<string>());
        await SaveTaskAsync(rc, task).ConfigureAwait(false);
        var exclude = new HashSet<string> { data.ModelKey };
        var exec = await RunTaskCoreAsync(rc, task, modelKey, modelKey is null ? exclude : null, exclude).ConfigureAwait(false);
        result.RoutingReasons = task.RoutingReasons;
        if (exec?.Toolbox.Proposal is not { } alt)
        {
            if (task.State != AgentTaskState.Failed) await FinishTaskAsync(rc, task, AgentTaskState.Failed, exec?.Ai.Error ?? "The second model did not propose a change.").ConfigureAwait(false);
            result.Error = task.Error ?? "The second model did not propose a change.";
            return result;
        }
        await FinishTaskAsync(rc, task, AgentTaskState.Complete, "Alternative proposal: " + Trim(alt.Summary, 300)).ConfigureAwait(false);
        var altArtifact = await _knowledge.AddAsync(new KnowledgeArtifact
        {
            ProjectId = project.Id, Kind = ArtifactKind.ProposedChange, Title = $"Alternative ({exec.Model}): {data.TaskTitle}",
            Content = $"{alt.Summary}\nFiles: {string.Join(", ", alt.Edits.Select(e => e.RelativePath))}",
            DataJson = JsonSerializer.Serialize(new ProposedChangeData(data.Agent, task.Id, data.TaskTitle, data.Instructions, exec.Model.Key, alt.Edits, data.BeforeRevisionId, null, false)),
            Agent = data.Agent, Provider = exec.Model.ProviderId, Model = exec.Model.ModelId, TaskId = task.Id, RunId = rc.RunId,
        }, ct).ConfigureAwait(false);
        result.AlternativeArtifactId = altArtifact.Id;

        Emit(rc, "Evaluating both proposals with the real compiler and validators in a scratch copy…");
        var baseDir = await MaterializeBaseAsync(project, data.BeforeRevisionId, ct).ConfigureAwait(false);
        try
        {
            result.Original = await EvaluateAsync(project, baseDir, "Original", data.ModelKey, artifact.Content, data.Edits, ct).ConfigureAwait(false);
            result.Alternative = await EvaluateAsync(project, baseDir, "Alternative", exec.Model.Key, alt.Summary, alt.Edits, ct).ConfigureAwait(false);
        }
        finally { TryDelete(baseDir); }

        var a = data.Edits.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        var b = alt.Edits.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var path in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p))
        {
            if (a.TryGetValue(path, out var ea) && b.TryGetValue(path, out var eb))
            {
                if (ea.NewContent == eb.NewContent) result.Agreements.Add($"Both make the same change to {path}.");
                else
                {
                    var (_, added, removed) = TextDiff.Unified(ea.NewContent ?? "", eb.NewContent ?? "", "original", "alternative");
                    result.Agreements.Add($"Both change {path}.");
                    result.Differences.Add($"{path}: the versions differ by +{added}/-{removed} line(s).");
                }
            }
            else result.Differences.Add(a.ContainsKey(path) ? $"Only the original changes {path}." : $"Only the alternative changes {path}.");
        }
        result.EvidenceVerdict = (result.Original.Passed, result.Alternative.Passed) switch
        {
            (true, false) => $"Evidence favours the ORIGINAL: it compiles and validates; the alternative does not ({result.Alternative.ValidationErrors} validation error(s){(result.Alternative.CompilePassed || result.Alternative.CompileSkipped ? "" : ", compile failed")}).",
            (false, true) => $"Evidence favours the ALTERNATIVE: it compiles and validates; the original does not.",
            (true, true) => "Both compile and validate. Choose based on the differences — no AI vote is used to decide.",
            _ => "Neither proposal passes the real compiler/validators. See the diagnostics.",
        };
        return result;
    }

    private async Task<string> MaterializeBaseAsync(ModProject project, long? beforeRevisionId, CancellationToken ct)
    {
        var dir = Path.Combine(_paths.Scratch, "opinion-" + Guid.NewGuid().ToString("N")[..10], "source");
        Directory.CreateDirectory(dir);
        var rev = beforeRevisionId is { } id ? (await _projects.History.ListAsync(project, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == id) : null;
        if (rev is null)
        {
            FileUtil.CopyDirectory(project.SourcePath, dir, FileExclusionRules.ForSnapshots());
            return dir;
        }
        foreach (var (rel, _) in _projects.History.GetFiles(project, rev.CommitId))
        {
            var target = PathSafety.ResolveUnderRoot(dir, rel);
            if (target is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, _projects.History.ReadFile(project, rev.CommitId, rel), ct).ConfigureAwait(false);
        }
        return dir;
    }

    private async Task<ProposalEvaluation> EvaluateAsync(ModProject project, string baseDir, string label, string modelKey, string summary, IReadOnlyList<FileEdit> edits, CancellationToken ct)
    {
        var root = Path.Combine(_paths.Scratch, "eval-" + Guid.NewGuid().ToString("N")[..10]);
        var temp = new ModProject
        {
            Name = project.Name, Version = project.Version, WorkspacePath = root, ModRootRelativePath = project.ModRootRelativePath,
            ModFolderName = project.ModFolderName, GameProfileId = project.GameProfileId, IncludeSourceInPackage = project.IncludeSourceInPackage,
            ServerSideOnly = project.ServerSideOnly, AdditionalReferencePaths = project.AdditionalReferencePaths.ToList(),
        };
        try
        {
            temp.Workspace.EnsureCreated();
            FileUtil.CopyDirectory(baseDir, temp.SourcePath);
            foreach (var e in edits)
            {
                var full = PathSafety.ResolveUnderRoot(temp.SourcePath, e.RelativePath);
                if (full is null) continue;
                if (e.IsDelete) { if (File.Exists(full)) File.Delete(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, e.NewContent, ct).ConfigureAwait(false);
            }
            var build = await Task.Run(() => _pipeline.RunAsync(temp, new BuildOptions { Package = false, Persist = false }, null, ct), ct).ConfigureAwait(false);
            var diags = build.AllDiagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString())
                .Concat(build.Validation?.Findings.Where(f => f.Severity == Severity.Error).Select(f => $"[{f.ValidatorId}] {f.Message}") ?? Enumerable.Empty<string>()).Take(10).ToList();
            return new ProposalEvaluation(label, modelKey, summary, edits.Select(e => e.RelativePath).ToList(), build.CompileSucceeded && !build.CompileSkipped, build.CompileSkipped,
                build.Validation?.ErrorCount ?? 0, build.Validation?.WarningCount ?? 0, diags);
        }
        finally { TryDelete(root); }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            var target = Path.GetFileName(dir) == "source" ? Path.GetDirectoryName(dir)! : dir;
            FileUtil.DeleteDirectory(target);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) => _a(value);
}
