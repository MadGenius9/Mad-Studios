using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Core.History;

public sealed record FileChange(char Kind, string Path)
{
    public override string ToString() => $"{Kind} {Path}";
}

/// <summary>
/// Project-level revision system. Every revision is a real Git commit of the project's source/ folder plus a database
/// record with action, reason, changed files and build status.
/// </summary>
public sealed class ProjectHistoryService
{
    private readonly IRevisionRepository _revisions;
    private readonly ILogger<ProjectHistoryService> _log;

    public ProjectHistoryService(IRevisionRepository revisions, ILogger<ProjectHistoryService>? log = null)
    {
        _revisions = revisions;
        _log = log ?? NullLogger<ProjectHistoryService>.Instance;
    }

    private static GitObjectStore Store(ModProject project)
    {
        var store = new GitObjectStore(project.Workspace.History);
        store.EnsureInitialized();
        return store;
    }

    /// <summary>
    /// Snapshots the current source folder. If nothing changed since the latest revision and
    /// <paramref name="force"/> is false, no revision is created and null is returned.
    /// </summary>
    public async Task<RevisionRecord?> CreateRevisionAsync(ModProject project, string action, string? reason = null,
        string? buildStatus = null, bool force = false, CancellationToken ct = default)
    {
        var store = Store(project);
        var tree = store.SnapshotDirectory(project.SourcePath);
        var parent = store.ReadRef();
        IReadOnlyList<FileChange> changes;
        if (parent != null)
        {
            var parentCommit = store.ReadCommit(parent);
            if (parentCommit.Tree == tree && !force)
            {
                _log.LogDebug("No changes since revision {Commit}; not creating a new revision for '{Action}'", parent, action);
                return null;
            }
            changes = DiffTrees(store, parentCommit.Tree, tree);
        }
        else
        {
            changes = store.FlattenTree(tree).Keys.OrderBy(k => k).Select(k => new FileChange('A', k)).ToList();
        }

        var message = string.IsNullOrWhiteSpace(reason) ? action : action + "\n\n" + reason;
        var now = DateTimeOffset.UtcNow;
        var commit = store.WriteCommit(tree, parent, "Mad Mod Studio", "studio@madmodstudio.local", now, message);
        store.UpdateRef(commit);

        var record = new RevisionRecord
        {
            ProjectId = project.Id,
            CommitId = commit,
            ParentCommitId = parent,
            TimestampUtc = now,
            Action = action,
            Reason = reason,
            ChangedFiles = changes.Select(c => c.ToString()).ToList(),
            BuildStatus = buildStatus,
        };
        record = await _revisions.AddAsync(record, ct).ConfigureAwait(false);
        _log.LogInformation("Created revision {Commit} for project {Project}: {Action} ({Count} changes)", commit[..10], project.Name, action, changes.Count);
        return record;
    }

    /// <summary>Returns true when the working copy differs from the latest revision (or there is no revision yet).</summary>
    public bool HasUnrecordedChanges(ModProject project)
    {
        var store = Store(project);
        var head = store.ReadRef();
        if (head == null) return true;
        return store.ReadCommit(head).Tree != store.SnapshotDirectory(project.SourcePath);
    }

    /// <summary>
    /// Restores source/ to the given revision. The current state is recorded first so the restore can itself be undone.
    /// </summary>
    public async Task<RevisionRecord?> RestoreAsync(ModProject project, RevisionRecord revision, CancellationToken ct = default)
    {
        var store = Store(project);
        await CreateRevisionAsync(project, "Before restore", $"Automatic safety snapshot before restoring revision {revision.CommitId[..10]}", ct: ct).ConfigureAwait(false);

        var commit = store.ReadCommit(revision.CommitId);
        var files = store.FlattenTree(commit.Tree);

        // Remove files that are tracked-type files not present in the target; leave excluded junk (bin/obj) alone.
        var rules = FileExclusionRules.ForSnapshots();
        foreach (var rel in FileUtil.EnumerateRelativeFiles(project.SourcePath, rules).ToList())
        {
            if (!files.ContainsKey(rel))
                File.Delete(Path.Combine(project.SourcePath, rel));
        }
        foreach (var (rel, blob) in files)
        {
            var target = PathSafety.ResolveUnderRoot(project.SourcePath, rel)
                ?? throw new InvalidDataException($"Revision contains an unsafe path '{rel}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, store.ReadBlob(blob));
        }
        RemoveEmptyDirectories(project.SourcePath);

        return await CreateRevisionAsync(project, $"Restored revision {revision.CommitId[..10]}",
            $"Restored to '{revision.Action}' from {revision.TimestampUtc.LocalDateTime:g}", force: true, ct: ct).ConfigureAwait(false);
    }

    public IReadOnlyDictionary<string, string> GetFiles(ModProject project, string commitId)
    {
        var store = Store(project);
        return store.FlattenTree(store.ReadCommit(commitId).Tree);
    }

    public byte[] ReadFile(ModProject project, string commitId, string relativePath)
    {
        var store = Store(project);
        var files = store.FlattenTree(store.ReadCommit(commitId).Tree);
        return files.TryGetValue(relativePath, out var blob)
            ? store.ReadBlob(blob)
            : throw new FileNotFoundException($"'{relativePath}' does not exist in revision {commitId[..10]}.");
    }

    public Task<IReadOnlyList<RevisionRecord>> ListAsync(ModProject project, CancellationToken ct = default) =>
        _revisions.ListAsync(project.Id, ct);

    public static IReadOnlyList<FileChange> DiffTrees(GitObjectStore store, string oldTree, string newTree)
    {
        var a = store.FlattenTree(oldTree);
        var b = store.FlattenTree(newTree);
        var changes = new List<FileChange>();
        foreach (var (path, sha) in b)
        {
            if (!a.TryGetValue(path, out var old)) changes.Add(new FileChange('A', path));
            else if (old != sha) changes.Add(new FileChange('M', path));
        }
        foreach (var path in a.Keys)
            if (!b.ContainsKey(path)) changes.Add(new FileChange('D', path));
        return changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }
}
