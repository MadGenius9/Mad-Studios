namespace MadModStudio.Core.Models;

public sealed class RevisionRecord
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    /// <summary>Git commit id (SHA-1) in the project's history.git object store.</summary>
    public string CommitId { get; set; } = "";
    public string? ParentCommitId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = "";
    public string? Reason { get; set; }
    public List<string> ChangedFiles { get; set; } = new();
    public string? BuildStatus { get; set; }
    /// <summary>Who made the change: agent, provider, model, task (for AI changes), or "user".</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class BuildRecord
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public BuildConfiguration Configuration { get; set; }
    public string Version { get; set; } = "";
    public bool CompileSucceeded { get; set; }
    public bool CompileSkipped { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public Severity ValidationOutcome { get; set; }
    public string? PackagePath { get; set; }
    public bool PackagedWithErrors { get; set; }
    public bool Succeeded { get; set; }
    public string? Summary { get; set; }
}

public sealed class AppSetting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}

/// <summary>One "Deploy to Game" action: which package went into which game Mods folder, and how to undo it.</summary>
public sealed class DeploymentRecord
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid GameProfileId { get; set; }
    public DateTimeOffset DeployedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Version { get; set; } = "";
    public string PackagePath { get; set; } = "";
    /// <summary>Full path of the mod folder written inside the game's Mods folder.</summary>
    public string TargetPath { get; set; } = "";
    /// <summary>Copy of the folder that was there before (null when the target did not exist).</summary>
    public string? BackupPath { get; set; }
    /// <summary>Relative path → SHA-256 of every deployed file; used to detect later edits before undo.</summary>
    public Dictionary<string, string> Manifest { get; set; } = new();
    public DateTimeOffset? UndoneUtc { get; set; }
    public bool IsUndone => UndoneUtc != null;
}
