using MadModStudio.AI.Agents;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.AI.Coordination;

public enum WorkflowKind { Repair, Create, Improve }

public sealed class WorkflowRequest
{
    public required ModProject Project { get; init; }
    public required string UserRequest { get; init; }
    public WorkflowKind Kind { get; init; } = WorkflowKind.Repair;
    public bool Package { get; init; } = true;
    public bool BumpVersion { get; init; } = true;
    public IReadOnlyList<(string Path, string Kind)> Logs { get; init; } = Array.Empty<(string, string)>();
    public string? WorkingVersionPath { get; init; }
}

public sealed record EscalationSuggestion(string RunId, string TaskId, AgentKind Agent, string CurrentModelKey, string? SuggestedModelKey,
    string SuggestedDisplay, IReadOnlyList<string> Reasons, string Message);

public sealed class WorkflowResult
{
    public string RunId { get; init; } = "";
    public bool Success { get; set; }
    public bool Blocked { get; set; }
    public string Status { get; set; } = "";
    public EscalationSuggestion? Escalation { get; set; }
    public string Report { get; set; } = "";
    public string? PackagePath { get; set; }
    public List<AgentTaskRecord> Tasks { get; } = new();
    public BuildResult? FinalBuild { get; set; }
    public ReviewVerdict? Review { get; set; }
    public List<string> ModelsUsed { get; } = new();
    public List<string> FailedAttempts { get; } = new();
}

public interface IAgentRunObserver
{
    void OnTaskChanged(AgentTaskRecord task);
    void OnEvent(string message);
}

public sealed class NullRunObserver : IAgentRunObserver
{
    public static readonly NullRunObserver Instance = new();
    public void OnTaskChanged(AgentTaskRecord task) { }
    public void OnEvent(string message) { }
}

public sealed record ProposalEvaluation(string Label, string ModelKey, string Summary, IReadOnlyList<string> Files, bool CompilePassed, bool CompileSkipped,
    int ValidationErrors, int ValidationWarnings, IReadOnlyList<string> TopDiagnostics)
{
    public bool Passed => (CompilePassed || CompileSkipped) && ValidationErrors == 0;
}

public sealed class SecondOpinionResult
{
    public string? Error { get; set; }
    public ProposalEvaluation? Original { get; set; }
    public ProposalEvaluation? Alternative { get; set; }
    public List<string> Agreements { get; } = new();
    public List<string> Differences { get; } = new();
    /// <summary>Decided from compiler/validator evidence only — never by AI vote.</summary>
    public string EvidenceVerdict { get; set; } = "";
    public long? AlternativeArtifactId { get; set; }
    public IReadOnlyList<string> RoutingReasons { get; set; } = Array.Empty<string>();
}

/// <summary>Data stored on a ProposedChange artifact so it can be evaluated, compared or restored later.</summary>
public sealed record ProposedChangeData(string Agent, string TaskId, string TaskTitle, string Instructions, string ModelKey, IReadOnlyList<FileEdit> Edits,
    long? BeforeRevisionId, long? AfterRevisionId, bool Applied);

public interface IAgentCoordinator
{
    Task<WorkflowResult> RunWorkflowAsync(WorkflowRequest request, IAgentRunObserver? observer = null, CancellationToken ct = default);
    /// <summary>Continues compile/repair → validate → review → package (e.g. "Try another model" after escalation).</summary>
    Task<WorkflowResult> ContinueRepairAsync(ModProject project, string? modelKey, bool package, IAgentRunObserver? observer = null, CancellationToken ct = default);
    /// <summary>Runs one agent task (MANUAL control, or "Try another model" for a failed task).</summary>
    Task<AgentTaskRecord> RunAgentTaskAsync(ModProject project, AgentKind agent, string instructions, string? modelKey, IReadOnlySet<string>? excludeModels,
        IAgentRunObserver? observer = null, CancellationToken ct = default);
    Task<SecondOpinionResult> SecondOpinionAsync(ModProject project, long proposalArtifactId, string? modelKey, IAgentRunObserver? observer = null, CancellationToken ct = default);
    Task<RoutingDecision> PreviewRoutingAsync(ModProject project, AgentKind agent, CancellationToken ct = default);
}
