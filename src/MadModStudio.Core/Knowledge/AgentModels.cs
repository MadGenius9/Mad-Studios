namespace MadModStudio.Core.Knowledge;

/// <summary>Built-in specialist agents. Stored by name, so new kinds can be added without migrations.</summary>
public enum AgentKind
{
    Lead,
    Architect,
    GameApiResearch,
    CSharpHarmony,
    XmlXPath,
    LogDetective,
    CompilerRepair,
    DllAnalysis,
    Compatibility,
    Validator,
    Documentation,
}

/// <summary>Kinds of work used for routing and performance statistics.</summary>
public enum AITaskType
{
    Planning,
    Architecture,
    ApiResearch,
    HarmonyImplementation,
    HarmonyRepair,
    XmlImplementation,
    XmlRepair,
    LogAnalysis,
    CompilerRepair,
    DllAnalysis,
    Compatibility,
    Review,
    Documentation,
}

public enum AgentTaskState
{
    Idle,
    Planning,
    Working,
    Waiting,
    Blocked,
    NeedsReview,
    Failed,
    Complete,
    Skipped,
}

public enum RoutingMode { Auto, BestQuality, Balanced, Fast, Economy, Manual }

public enum AgentControlLevel { Automatic, Guided, Manual }

/// <summary>Kinds of structured artifacts agents exchange instead of chat transcripts.</summary>
public enum ArtifactKind
{
    Task,
    Finding,
    Evidence,
    ApiFinding,
    NegativeApiFinding,
    XmlFinding,
    LogFinding,
    Diagnostic,
    ProposedChange,
    ImplementationResult,
    BuildResult,
    ValidationResult,
    Review,
    Decision,
    FailedAttempt,
    SuccessPattern,
    Handoff,
}

/// <summary>A unit of persisted, provider-independent project knowledge.</summary>
public sealed class KnowledgeArtifact
{
    public long Id { get; set; }
    /// <summary>Null for cross-project knowledge (e.g. success patterns tied to a game profile).</summary>
    public Guid? ProjectId { get; set; }
    public ArtifactKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Optional machine-readable payload (JSON).</summary>
    public string? DataJson { get; set; }
    public string? Agent { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? TaskId { get; set; }
    public string? RunId { get; set; }
    public Guid? GameProfileId { get; set; }
    /// <summary>Fingerprint of the game assemblies when the artifact was produced.</summary>
    public string? GameFingerprint { get; set; }
    /// <summary>True when the content was checked against actual local tools (index, compiler, validators).</summary>
    public bool Verified { get; set; }
    /// <summary>True after a game update: must be re-verified before use.</summary>
    public bool Stale { get; set; }
    /// <summary>"Local tool" for tool-produced evidence, otherwise "AI interpretation".</summary>
    public string Source { get; set; } = "AI interpretation";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A node in an agent task graph.</summary>
public sealed class AgentTaskRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string RunId { get; set; } = "";
    public Guid ProjectId { get; set; }
    public AgentKind Agent { get; set; }
    public AITaskType TaskType { get; set; }
    public string Title { get; set; } = "";
    public string Instructions { get; set; } = "";
    public List<string> DependsOn { get; set; } = new();
    public AgentTaskState State { get; set; } = AgentTaskState.Waiting;
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public List<string> RoutingReasons { get; set; } = new();
    public string? ResultSummary { get; set; }
    public List<string> FilesRead { get; set; } = new();
    public List<string> FilesWritten { get; set; } = new();
    public List<string> ModelsTried { get; set; } = new();
    public List<string> RecentActions { get; set; } = new();
    public string? Error { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Per-project AI settings (routing/control overrides and the team's model assignments).</summary>
public sealed class ProjectAISettings
{
    public RoutingMode? RoutingMode { get; set; }
    public AgentControlLevel? ControlLevel { get; set; }
    /// <summary>Agent kind name → "provider/model". Missing = AUTO.</summary>
    public Dictionary<string, string> AgentModels { get; set; } = new();
}

public sealed class ProjectKnowledgeRecord
{
    public Guid ProjectId { get; set; }
    public string? Objective { get; set; }
    public string? Summary { get; set; }
    public string? Plan { get; set; }
    public ProjectAISettings AISettings { get; set; } = new();
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum TaskOutcome { Pending, Success, Failure, Cancelled }

/// <summary>One AI task execution, used to learn which models actually succeed in Mad Mod Studio.</summary>
public sealed class ModelPerformanceRecord
{
    public long Id { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public AgentKind Agent { get; set; }
    public AITaskType TaskType { get; set; }
    public Guid? ProjectId { get; set; }
    public string? TaskId { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public long LatencyMs { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal? EstimatedCostUsd { get; set; }
    public TaskOutcome Outcome { get; set; } = TaskOutcome.Pending;
    /// <summary>null = not applicable / not run.</summary>
    public bool? CompilePassed { get; set; }
    public bool? ValidationPassed { get; set; }
    public int RepairAttempts { get; set; }
    public bool UserReverted { get; set; }
    /// <summary>Revision created before this task's change (used to detect user reverts).</summary>
    public long? BeforeRevisionId { get; set; }
    public string? Notes { get; set; }
}

public sealed record ModelTaskStats(string Provider, string Model, AITaskType TaskType, int Total, int Successes, int CompilePasses, int CompileAttempts, int ValidationPasses, int ValidationAttempts, int Reverted, double AvgLatencyMs)
{
    public const int MinimumSamples = 3;
    public bool HasEnoughData => Total >= MinimumSamples;
    public double SuccessRate => Total == 0 ? 0 : (double)Successes / Total;
}
