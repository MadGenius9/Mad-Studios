using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.History;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Mods;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Projects;

public sealed record FileEdit(string RelativePath, string? NewContent)
{
    /// <summary>True when the edit deletes the file.</summary>
    public bool IsDelete => NewContent is null;
}

/// <summary>Project lifecycle and all source modifications. Every change goes through revision history.</summary>
public sealed class ProjectService
{
    private readonly IProjectRepository _projects;
    private readonly ProjectHistoryService _history;
    private readonly ModImporter _importer;
    private readonly AppPaths _paths;
    private readonly ILogger<ProjectService> _log;

    public ProjectService(IProjectRepository projects, ProjectHistoryService history, ModImporter importer, AppPaths paths, ILogger<ProjectService>? log = null)
    {
        _projects = projects;
        _history = history;
        _importer = importer;
        _paths = paths;
        _log = log ?? NullLogger<ProjectService>.Instance;
    }

    public ProjectHistoryService History => _history;

    public Task<IReadOnlyList<ModProject>> ListAsync(CancellationToken ct = default) => _projects.ListAsync(ct);
    public Task<ModProject?> GetAsync(Guid id, CancellationToken ct = default) => _projects.GetAsync(id, ct);
    public Task SaveAsync(ModProject project, CancellationToken ct = default) => _projects.SaveAsync(project, ct);

    public async Task<IReadOnlyList<ImportedMod>> ImportAsync(string path, Guid? gameProfileId, ProjectOrigin origin = ProjectOrigin.Imported, CancellationToken ct = default)
    {
        var imported = await _importer.ImportAsync(path, gameProfileId, origin, ct).ConfigureAwait(false);
        foreach (var mod in imported)
        {
            await _projects.SaveAsync(mod.Project, ct).ConfigureAwait(false);
            await _history.CreateRevisionAsync(mod.Project, "Imported", $"Imported from {Path.GetFileName(path)}", force: true, ct: ct).ConfigureAwait(false);
        }
        return imported;
    }

    /// <summary>Creates an empty XML-ready mod project with a valid ModInfo.xml.</summary>
    public async Task<ModProject> CreateNewAsync(string displayName, Guid? gameProfileId, string? author = null, string? description = null, CancellationToken ct = default)
    {
        var folder = PathSafety.SanitizeFileName(new string(displayName.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray()), "NewMod");
        var project = new ModProject
        {
            Name = displayName,
            ModFolderName = folder,
            Version = "1.0.0",
            Description = description,
            Author = author,
            GameProfileId = gameProfileId,
            Origin = ProjectOrigin.New,
            Status = ProjectStatus.Draft,
        };
        project.WorkspacePath = _paths.ProjectWorkspace(project.Id);
        project.Workspace.EnsureCreated();
        ModInfoFile.Create(Path.Combine(project.ModRootPath, ModInfoFile.FileName), folder, displayName, project.Version, description, author);
        Directory.CreateDirectory(Path.Combine(project.ModRootPath, "Config"));
        await _projects.SaveAsync(project, ct).ConfigureAwait(false);
        await _history.CreateRevisionAsync(project, "Created project", null, force: true, ct: ct).ConfigureAwait(false);
        return project;
    }

    public string ReadFile(ModProject project, string relativePath, long maxBytes = 8 * 1024 * 1024)
    {
        var full = Resolve(project, relativePath);
        return FileUtil.ReadTextLimited(full, maxBytes, out _);
    }

    /// <summary>
    /// Saves a manual edit. Any unrecorded working changes are snapshotted first, then the edit itself becomes a revision.
    /// </summary>
    public async Task<RevisionRecord?> SaveFileAsync(ModProject project, string relativePath, string content, string? reason = null, CancellationToken ct = default)
    {
        var full = Resolve(project, relativePath);
        if (_history.HasUnrecordedChanges(project))
            await _history.CreateRevisionAsync(project, "Auto-snapshot", "Unrecorded changes captured before a manual edit", ct: ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, ct).ConfigureAwait(false);
        await TouchAsync(project, ct).ConfigureAwait(false);
        return await _history.CreateRevisionAsync(project, $"Edited {relativePath}", reason ?? "Manual edit", ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a set of edits (e.g. from the AI repair engine) atomically with respect to history: a revision is always
    /// created before the change, and another after it.
    /// </summary>
    public async Task<RevisionRecord?> ApplyEditsAsync(ModProject project, IReadOnlyList<FileEdit> edits, string action, string reason, CancellationToken ct = default)
    {
        if (edits.Count == 0) return null;
        var resolved = edits.Select(e => (Edit: e, Full: Resolve(project, e.RelativePath))).ToList();
        await _history.CreateRevisionAsync(project, $"Before: {action}", "Automatic safety snapshot before modification", force: true, ct: ct).ConfigureAwait(false);
        foreach (var (edit, full) in resolved)
        {
            if (edit.IsDelete)
            {
                if (File.Exists(full)) File.Delete(full);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, edit.NewContent, ct).ConfigureAwait(false);
            }
        }
        await TouchAsync(project, ct).ConfigureAwait(false);
        return await _history.CreateRevisionAsync(project, action, reason, force: true, ct: ct).ConfigureAwait(false);
    }

    /// <summary>Sets the mod version in ModInfo.xml and the project, recording revisions.</summary>
    public async Task SetVersionAsync(ModProject project, string version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Version cannot be empty.");
        var modInfo = Path.Combine(project.ModRootPath, ModInfoFile.FileName);
        if (_history.HasUnrecordedChanges(project))
            await _history.CreateRevisionAsync(project, "Auto-snapshot", "Unrecorded changes captured before version change", ct: ct).ConfigureAwait(false);
        var old = project.Version;
        if (File.Exists(modInfo)) ModInfoFile.SetVersion(modInfo, version.Trim());
        project.Version = version.Trim();
        await _projects.SaveAsync(project, ct).ConfigureAwait(false);
        await _history.CreateRevisionAsync(project, $"Set version {project.Version}", $"Version changed from {old} to {project.Version}", force: true, ct: ct).ConfigureAwait(false);
    }

    public async Task<string> AttachFileAsync(ModProject project, string path, string kind, CancellationToken ct = default)
    {
        var dir = Path.Combine(project.Workspace.Attachments, kind);
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "_" + PathSafety.SanitizeFileName(Path.GetFileName(path)));
        await using (var src = File.OpenRead(path))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        return dest;
    }

    public async Task DeleteAsync(ModProject project, CancellationToken ct = default)
    {
        await _projects.DeleteAsync(project.Id, ct).ConfigureAwait(false);
        if (PathSafety.IsUnder(_paths.Projects, project.WorkspacePath))
            FileUtil.DeleteDirectory(project.WorkspacePath);
    }

    public IEnumerable<string> ListFiles(ModProject project) =>
        FileUtil.EnumerateRelativeFiles(project.SourcePath, FileExclusionRules.ForSnapshots()).OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    private async Task TouchAsync(ModProject project, CancellationToken ct)
    {
        await _projects.SaveAsync(project, ct).ConfigureAwait(false);
    }

    private static string Resolve(ModProject project, string relativePath) =>
        PathSafety.ResolveUnderRoot(project.SourcePath, relativePath)
        ?? throw new ArgumentException($"'{relativePath}' is not a valid path inside the project.");
}
