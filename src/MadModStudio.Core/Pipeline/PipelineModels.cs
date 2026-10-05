namespace MadModStudio.Core.Pipeline;

public enum PipelineStage
{
    Analyze = 0,
    GenerateOrRepair = 1,
    Compile = 2,
    Validate = 3,
    Package = 4,
}

public enum StageStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
    Warning = 5,
}

/// <summary>A real progress event emitted by an operation that actually happened.</summary>
public sealed record PipelineEvent(PipelineStage Stage, StageStatus Status, string Message, DateTimeOffset TimestampUtc)
{
    public static PipelineEvent Now(PipelineStage stage, StageStatus status, string message) =>
        new(stage, status, message, DateTimeOffset.UtcNow);
}
