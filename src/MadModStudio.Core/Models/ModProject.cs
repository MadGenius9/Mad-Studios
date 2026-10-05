using System.Text.Json.Serialization;

namespace MadModStudio.Core.Models;

public sealed class ModProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string? Description { get; set; }
    public string? Author { get; set; }
    /// <summary>Root folder of the project workspace (contains original/, source/, build/, output/, history/).</summary>
    public string WorkspacePath { get; set; } = "";
    /// <summary>Path of the mod folder (the one containing ModInfo.xml) relative to source/. Empty = source/ itself.</summary>
    public string ModRootRelativePath { get; set; } = "";
    /// <summary>Folder name used inside packages, e.g. "MadWorkingRacks".</summary>
    public string ModFolderName { get; set; } = "";
    public Guid? GameProfileId { get; set; }
    public ModType ModType { get; set; } = ModType.Unknown;
    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public ProjectOrigin Origin { get; set; } = ProjectOrigin.New;
    public bool ServerSideOnly { get; set; }
    public SideRequirement SideRequirement { get; set; } = SideRequirement.Unknown;
    public EacCompatibility EacCompatibility { get; set; } = EacCompatibility.Unknown;
    public string? ImportedFrom { get; set; }
    public bool IncludeSourceInPackage { get; set; }
    /// <summary>Extra DLLs (e.g. from dependency mods) referenced when compiling this project. Never packaged.</summary>
    public List<string> AdditionalReferencePaths { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public ProjectWorkspace Workspace => new(WorkspacePath);
    [JsonIgnore]
    public string SourcePath => Workspace.Source;
    [JsonIgnore]
    public string ModRootPath => string.IsNullOrEmpty(ModRootRelativePath)
        ? Workspace.Source
        : Path.Combine(Workspace.Source, ModRootRelativePath);
}

/// <summary>Well-known layout of a project workspace directory.</summary>
public sealed class ProjectWorkspace
{
    public ProjectWorkspace(string root) => Root = root;

    public string Root { get; }
    /// <summary>Untouched copy of whatever was imported. Never modified after import.</summary>
    public string Original => Path.Combine(Root, "original");
    /// <summary>The editable working copy.</summary>
    public string Source => Path.Combine(Root, "source");
    public string Build => Path.Combine(Root, "build");
    public string Output => Path.Combine(Root, "output");
    /// <summary>Bare Git-format object database for revisions.</summary>
    public string History => Path.Combine(Root, "history.git");
    public string Logs => Path.Combine(Root, "logs");
    public string Attachments => Path.Combine(Root, "attachments");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Original, Source, Build, Output, Logs, Attachments })
            Directory.CreateDirectory(d);
    }
}
