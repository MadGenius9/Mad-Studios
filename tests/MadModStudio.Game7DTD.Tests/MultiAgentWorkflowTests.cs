using System.IO.Compression;
using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.TestSupport;
using static MadModStudio.Game7DTD.Tests.ScriptedProvider;

namespace MadModStudio.Game7DTD.Tests;

public class MultiAgentWorkflowTests
{
    private static string Bad(int n) => SampleMod.PatchSource.Replace("__instance.IsHome", $"__instance.StillWrong{n}");

    private static ScriptStep Propose(Func<int, string> source, string summary) => async (req, tools, n) =>
    {
        var read = await Call(tools, ToolNames.ReadProjectFile, new { path = "Harmony/Patches.cs" });
        Assert.False(read.IsError, read.Content);
        var r = await Call(tools, ToolNames.ProposeFileChanges, new { summary = $"{summary} #{n}", files = new[] { new { path = "Harmony/Patches.cs", content = source(n) } } });
        Assert.False(r.IsError, r.Content);
        return Done(summary);
    };

    private static ScriptStep Lead(params object[] tasks) => async (req, tools, n) =>
    {
        await Call(tools, ToolNames.ListProjectFiles, new { });
        var diag = await Call(tools, ToolNames.GetCompilerDiagnostics, new { });
        Assert.Contains("IsAtHomeBase", diag.Content); // the Lead sees the ACTUAL compiler output gathered beforehand
        var r = await Call(tools, ToolNames.SubmitTaskPlan, new { objective = "Fix the mod", summary = "Patches.cs uses a member that does not exist", tasks });
        Assert.False(r.IsError, r.Content);
        return Done("Planned.");
    };

    private static ScriptStep Approve => async (req, tools, n) =>
    {
        await Call(tools, ToolNames.GetCompilerDiagnostics, new { });
        await Call(tools, ToolNames.SubmitReview, new { verdict = "approve", summary = "Compiler and validators pass; API verified.", issues = Array.Empty<string>(), evidence_checked = new[] { "build", "validation" } });
        return Done("Reviewed.");
    };

    private static object[] StandardPlan => new object[]
    {
        new { id = "api", agent = "GameApiResearch", title = "Verify EntityDrone members", instructions = "Check IsAtHomeBase / IsHome" },
        new { id = "logs", agent = "LogDetective", title = "Check logs", instructions = "Look for related errors" },
        new { id = "fix", agent = "CSharpHarmony", title = "Fix Patches.cs", instructions = "Use the verified member", depends_on = new[] { "api", "logs" } },
    };

    private static ScriptedProvider Alpha(bool repairAlwaysFails = true)
    {
        var alpha = new ScriptedProvider("alpha", "a1");
        alpha.Steps["Lead"] = Lead(StandardPlan);
        alpha.Steps["GameApiResearch"] = async (req, tools, n) =>
        {
            await Task.Delay(300);
            var neg = await Call(tools, ToolNames.VerifyApi, new { type_name = "EntityDrone", member_name = "IsAtHomeBase" });
            Assert.True(neg.IsError);
            Assert.Contains("does NOT exist", neg.Content);
            var pos = await Call(tools, ToolNames.VerifyApi, new { type_name = "EntityDrone", member_name = "IsHome" });
            Assert.False(pos.IsError, pos.Content);
            return Done("IsHome exists; IsAtHomeBase does not.");
        };
        alpha.Steps["LogDetective"] = async (req, tools, n) =>
        {
            await Task.Delay(300);
            var logs = await Call(tools, ToolNames.GetLogProblems, new { });
            await Call(tools, ToolNames.RecordFinding, new { kind = "LogFinding", title = "No logs attached", content = logs.Content });
            return Done();
        };
        alpha.Steps["CSharpHarmony"] = Propose(Bad, "Harmony fix (wrong member)");
        alpha.Steps["CompilerRepair"] = Propose(n => repairAlwaysFails ? Bad(100 + n) : SampleMod.PatchSource, "Repair attempt");
        alpha.Steps["Validator"] = Approve;
        return alpha;
    }

    private static ScriptedProvider Beta()
    {
        var beta = new ScriptedProvider("beta", "b1");
        beta.Steps["CompilerRepair"] = Propose(_ => SampleMod.PatchSource, "Use EntityDrone.IsHome (verified)");
        beta.Steps["CSharpHarmony"] = Propose(_ => SampleMod.PatchSource, "Alternative: use EntityDrone.IsHome");
        beta.Steps["Validator"] = Approve;
        return beta;
    }

    [Fact]
    public async Task Full_repair_with_parallel_agents_escalation_review_and_package()
    {
        var alpha = Alpha();
        var beta = Beta();
        using var host = new AITestHost(policy: p => { p.AutoEscalate = true; p.AllowCrossProviderRouting = true; }, providers: new[] { alpha, beta });
        var (project, _) = await host.ImportBrokenAsync();
        var events = new List<string>();

        var result = await host.Get<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix this mod." }, new ListObserver(events));

        Assert.True(result.Success, result.Report);
        Assert.Equal("MadWorkingRacks_1.0.9.zip", Path.GetFileName(result.PackagePath));
        using (var zip = ZipFile.OpenRead(result.PackagePath!)) Assert.Contains(zip.Entries, e => e.FullName == "MadWorkingRacks/MadWorkingRacks.dll");
        Assert.True(alpha.MaxConcurrent >= 2, "Independent research tasks should run concurrently.");
        Assert.Contains("alpha/a1", result.ModelsUsed);
        Assert.Contains("beta/b1", result.ModelsUsed);
        Assert.Equal(2, result.FailedAttempts.Count);
        Assert.Contains(events, e => e.Contains("Escalating") && e.Contains("b1"));
        Assert.Equal("approve", result.Review!.Verdict);
        Assert.Contains("REPAIR COMPLETE", result.Report);
        Assert.Contains(result.Tasks, t => t.Agent == AgentKind.CompilerRepair && t.State == AgentTaskState.Complete && t.ModelsTried.SequenceEqual(new[] { "alpha/a1", "beta/b1" }));

        // Failure memory: the escalated model was told what not to repeat, derived from actual compiler errors.
        var betaRepair = beta.Requests.Single(r => r.Tag == "CompilerRepair");
        Assert.Contains("DO NOT REPEAT", betaRepair.UserMessage);
        Assert.Contains("StillWrong101", betaRepair.UserMessage);
        Assert.Contains("not present in the current Game Profile", betaRepair.UserMessage);
        // Verified API findings flow to later agents.
        var harmonyRequest = alpha.Requests.Single(r => r.Tag == "CSharpHarmony");
        Assert.Contains("EntityDrone.IsHome", harmonyRequest.UserMessage);
        Assert.Contains("IsAtHomeBase does NOT exist", harmonyRequest.UserMessage);

        // Revisions record agent/provider/model, and every change was preceded by a snapshot.
        var history = await host.Get<ProjectService>().History.ListAsync(project);
        Assert.Contains(history, r => r.Action.StartsWith("Before: AI:") && r.Metadata.GetValueOrDefault("filesAboutToChange") == "Harmony/Patches.cs" && r.Metadata.ContainsKey("model"));
        Assert.Contains(history, r => r.Action == "AI: Repair compile/validation errors" && r.Metadata["model"] == "beta/b1");

        // Performance history is real: alpha's repairs failed, beta's succeeded.
        var records = await host.Get<IModelPerformanceTracker>().GetRecordsAsync(project.Id);
        Assert.Equal(2, records.Count(r => r.Model == "a1" && r.TaskType == AITaskType.CompilerRepair && r.Outcome == TaskOutcome.Failure));
        Assert.Single(records, r => r.Model == "b1" && r.TaskType == AITaskType.CompilerRepair && r.Outcome == TaskOutcome.Success && r.CompilePassed == true);
        Assert.Single(records, r => r.TaskType == AITaskType.HarmonyRepair && r.Outcome == TaskOutcome.Failure && r.CompilePassed == false);
        Assert.Contains(records, r => r.TaskType == AITaskType.ApiResearch && r.Outcome == TaskOutcome.Success);
    }

    [Fact]
    public async Task Every_agent_call_is_recorded_in_the_spend_ledger_with_known_or_unknown_cost()
    {
        var alpha = Alpha(repairAlwaysFails: false);
        using var host = new AITestHost(providers: alpha);
        // alpha/a1 has no built-in price; set one the way the AI Models page does.
        host.Get<MadModStudio.AI.Models.ModelProfileStore>().SetPrice("alpha", "a1", 2m, 10m);
        host.Get<MadModStudio.AI.Models.IModelCatalog>().Rebuild();
        var (project, _) = await host.ImportBrokenAsync();

        var result = await host.Get<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix this mod." });

        var ledger = await host.Get<IAISpendRepository>().ListAsync(projectId: project.Id);
        Assert.Equal(alpha.Requests.Count, ledger.Count);
        Assert.All(ledger, r => Assert.Equal("a1", r.Model));
        Assert.All(ledger, r => Assert.NotNull(r.CostUsd));
        var expected = ledger.Sum(r => (r.InputTokens * 2m + r.OutputTokens * 10m) / 1_000_000m);
        Assert.Equal(expected, await host.Get<IAISpendRepository>().ProjectTotalAsync(project.Id));
        Assert.Contains(ledger, r => r.Agent == AgentKind.Lead);
        Assert.True(result.Success, result.Report);
    }

    [Fact]
    public async Task Project_budget_counts_spend_recorded_before_a_restart()
    {
        using var host = new AITestHost(providers: Alpha());
        var (project, _) = await host.ImportBrokenAsync();
        var ledger = host.Get<IAISpendRepository>();
        await ledger.AddAsync(new AISpendRecord { ProjectId = project.Id, Provider = "alpha", Model = "a1", CostUsd = 0.60m });
        await ledger.AddAsync(new AISpendRecord { ProjectId = project.Id, Provider = "alpha", Model = "a1", CostUsd = null }); // unpriced: never counted
        await ledger.AddAsync(new AISpendRecord { ProjectId = Guid.NewGuid(), Provider = "alpha", Model = "a1", CostUsd = 5m }); // other project

        // A fresh guard (as after restarting the app) still sees this project's recorded spend.
        var guard = new BudgetGuard(ledger);
        var policy = new AIPolicy { ProjectBudgetUsd = 0.50m };
        Assert.Contains("Project AI budget", await guard.CheckAsync(policy, project.Id, 0));
        policy.ProjectBudgetUsd = 1m;
        Assert.Null(await guard.CheckAsync(policy, project.Id, 0));
        Assert.Equal(0m, guard.SessionSpend);
    }

    [Fact]
    public async Task Without_auto_escalation_the_user_is_asked_and_can_try_the_suggested_model()
    {
        var alpha = Alpha();
        var beta = Beta();
        using var host = new AITestHost(policy: p => { p.AutoEscalate = false; p.AllowCrossProviderRouting = true; }, providers: new[] { alpha, beta });
        var (project, _) = await host.ImportBrokenAsync();
        var coordinator = host.Get<IAgentCoordinator>();

        var result = await coordinator.RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix this mod." });

        Assert.False(result.Success);
        Assert.True(result.Blocked);
        Assert.Equal("beta/b1", result.Escalation!.SuggestedModelKey);
        Assert.Contains("failed 2 repair attempt", result.Escalation.Message);
        Assert.Empty(beta.Requests); // no data was sent to the other provider without permission
        Assert.Contains(result.Tasks, t => t.Agent == AgentKind.CompilerRepair && t.State == AgentTaskState.Blocked);

        var continued = await coordinator.ContinueRepairAsync(project, result.Escalation.SuggestedModelKey, package: true);
        Assert.True(continued.Success, continued.Report);
        Assert.NotNull(continued.PackagePath);
    }

    [Fact]
    public async Task Cross_provider_routing_off_keeps_work_on_one_provider()
    {
        var alpha = Alpha();
        var beta = Beta();
        using var host = new AITestHost(policy: p => { p.AutoEscalate = true; p.AllowCrossProviderRouting = false; p.DefaultProvider = "alpha"; }, providers: new[] { alpha, beta });
        var (project, _) = await host.ImportBrokenAsync();

        var result = await host.Get<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix this mod." });

        Assert.Empty(beta.Requests);
        Assert.True(result.Blocked, result.Report); // escalation suggested, not performed automatically
        Assert.Equal("beta/b1", result.Escalation!.SuggestedModelKey);
    }

    [Fact]
    public async Task Guided_control_requires_approval_and_rejection_changes_nothing()
    {
        var alpha = Alpha();
        alpha.Steps["CSharpHarmony"] = Propose(_ => SampleMod.PatchSource, "Use IsHome");
        var approvals = new FixedApproval(false);
        using var host = new AITestHost(approvals, p => p.ControlLevel = AgentControlLevel.Guided, alpha);
        var (project, _) = await host.ImportBrokenAsync();
        var path = Path.Combine(project.SourcePath, "Harmony", "Patches.cs");
        var before = File.ReadAllText(path);

        var task = await host.Get<IAgentCoordinator>().RunAgentTaskAsync(project, AgentKind.CSharpHarmony, "Fix the compile error", null, null);

        Assert.Equal(AgentTaskState.Failed, task.State);
        Assert.Equal("Rejected by you.", task.Error);
        Assert.Equal(before, File.ReadAllText(path));
        var pending = Assert.Single(approvals.Seen);
        var lines = pending.Diff.Split('\n');
        Assert.Contains(lines, l => l.StartsWith('-') && l.Contains("if (__instance.IsAtHomeBase)"));
        Assert.Contains(lines, l => l.StartsWith('+') && l.Contains("if (__instance.IsHome)"));
        Assert.Equal("alpha/a1", pending.Model);
    }

    [Fact]
    public async Task Manual_control_stops_after_planning()
    {
        var alpha = Alpha();
        using var host = new AITestHost(policy: p => p.ControlLevel = AgentControlLevel.Manual, providers: alpha);
        var (project, _) = await host.ImportBrokenAsync();

        var result = await host.Get<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix this mod." });

        Assert.Contains("MANUAL", result.Status);
        Assert.Equal(3, result.Tasks.Count(t => t.State == AgentTaskState.Waiting));
        Assert.DoesNotContain(alpha.Requests, r => r.Tag is "CSharpHarmony" or "GameApiResearch");
    }

    [Fact]
    public async Task Invalid_plans_are_rejected_back_to_the_lead()
    {
        var alpha = Alpha();
        alpha.Steps["Lead"] = async (req, tools, n) =>
        {
            var bad = await Call(tools, ToolNames.SubmitTaskPlan, new
            {
                objective = "x", summary = "x",
                tasks = new object[] { new { id = "a", agent = "XmlXPath", title = "a", instructions = "", depends_on = new[] { "b" } }, new { id = "b", agent = "Wizard", title = "b", instructions = "" } },
            });
            Assert.True(bad.IsError);
            Assert.Contains("unknown or unavailable agent 'Wizard'", bad.Content);
            var cyc = await Call(tools, ToolNames.SubmitTaskPlan, new
            {
                objective = "x", summary = "x",
                tasks = new object[] { new { id = "a", agent = "XmlXPath", title = "a", instructions = "", depends_on = new[] { "b" } }, new { id = "b", agent = "XmlXPath", title = "b", instructions = "", depends_on = new[] { "a" } } },
            });
            Assert.Contains("cycle", cyc.Content);
            return Done("gave up");
        };
        using var host = new AITestHost(providers: alpha);
        var (project, _) = await host.ImportBrokenAsync();
        var result = await host.Get<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest { Project = project, UserRequest = "Fix" });
        Assert.StartsWith("Planning failed", result.Status);
    }

    [Fact]
    public async Task Second_opinion_is_decided_by_compiler_and_validator_evidence()
    {
        var alpha = Alpha();
        var beta = Beta();
        using var host = new AITestHost(policy: p => p.AllowCrossProviderRouting = true, providers: new[] { alpha, beta });
        var (project, _) = await host.ImportBrokenAsync();
        var coordinator = host.Get<IAgentCoordinator>();
        await coordinator.RunAgentTaskAsync(project, AgentKind.CSharpHarmony, "Fix the compile error", "alpha/a1", null);
        var proposal = (await host.Get<MadModStudio.AI.Knowledge.ProjectKnowledgeService>().ListAsync(project.Id, ArtifactKind.ProposedChange)).First();
        var appliedContent = File.ReadAllText(Path.Combine(project.SourcePath, "Harmony", "Patches.cs"));

        var opinion = await coordinator.SecondOpinionAsync(project, proposal.Id, null);

        Assert.Null(opinion.Error);
        Assert.False(opinion.Original!.Passed);
        Assert.True(opinion.Alternative!.Passed);
        Assert.Equal("beta/b1", opinion.Alternative.ModelKey);
        Assert.StartsWith("Evidence favours the ALTERNATIVE", opinion.EvidenceVerdict);
        Assert.Contains(opinion.Differences, d => d.Contains("Harmony/Patches.cs"));
        Assert.Contains(opinion.Original.TopDiagnostics, d => d.Contains("CS1061"));
        Assert.Equal(appliedContent, File.ReadAllText(Path.Combine(project.SourcePath, "Harmony", "Patches.cs"))); // evaluation never touches the project
    }

    [Fact]
    public async Task Privacy_setting_blocks_source_from_external_providers()
    {
        var alpha = Alpha();
        AIToolResult? read = null;
        alpha.Steps["GameApiResearch"] = async (req, tools, n) =>
        {
            read = await Call(tools, ToolNames.ReadProjectFile, new { path = "Harmony/Patches.cs" });
            return Done();
        };
        using var host = new AITestHost(policy: p => p.AllowSourceToExternal = false, providers: alpha);
        var (project, _) = await host.ImportBrokenAsync();
        var coordinator = host.Get<IAgentCoordinator>();

        await coordinator.RunAgentTaskAsync(project, AgentKind.GameApiResearch, "Look at the patch", null, null);
        var write = await coordinator.RunAgentTaskAsync(project, AgentKind.CSharpHarmony, "Fix", null, null);

        Assert.True(read!.IsError);
        Assert.Contains("disabled", read.Content);
        Assert.Equal(AgentTaskState.Failed, write.State);
        Assert.Contains("Privacy", write.Error);
    }

    private sealed class ListObserver : IAgentRunObserver
    {
        private readonly List<string> _events;
        public ListObserver(List<string> events) => _events = events;
        public void OnEvent(string message) { lock (_events) _events.Add(message); }
        public void OnTaskChanged(AgentTaskRecord task) { }
    }
}
