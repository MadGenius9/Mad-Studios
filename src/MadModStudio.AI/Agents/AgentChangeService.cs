using System.Collections.Concurrent;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.AI.Agents;

public sealed record ChangeApplyResult(bool Applied, IReadOnlyList<string> Conflicts, RevisionRecord? Before, RevisionRecord? After);

/// <summary>
/// Applies agent proposals safely: acquires file write ownership, refuses to overwrite files that changed since the
/// agent read them (another agent's work), and records revisions before/after with agent, provider, model and task.
/// </summary>
public sealed class AgentChangeService
{
    private readonly ProjectService _projects;
    private readonly FileOwnershipManager _ownership;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectGates = new();

    public AgentChangeService(ProjectService projects, FileOwnershipManager ownership)
    {
        _projects = projects;
        _ownership = ownership;
    }

    public FileOwnershipManager Ownership => _ownership;

    public async Task<ChangeApplyResult> ApplyAsync(ModProject project, ProposedChanges proposal, IReadOnlyDictionary<string, string> readHashes,
        string owner, IReadOnlyDictionary<string, string> metadata, string action, CancellationToken ct = default)
    {
        await using var _ = await _ownership.AcquireWriteAsync(project.Id, proposal.Edits.Select(e => e.RelativePath), owner, ct).ConfigureAwait(false);
        var conflicts = new List<string>();
        foreach (var e in proposal.Edits)
        {
            var full = Path.Combine(project.SourcePath, e.RelativePath);
            var current = FileOwnershipManager.HashFile(full);
            if (current is null) continue; // new file
            if (!readHashes.TryGetValue(e.RelativePath, out var seen))
                conflicts.Add($"{e.RelativePath}: the agent did not read this file before changing it.");
            else if (seen != current)
                conflicts.Add($"{e.RelativePath}: changed by another agent or the user after this agent read it.");
        }
        if (conflicts.Count > 0) return new ChangeApplyResult(false, conflicts, null, null);

        // Revisions are serialized per project so concurrent agents never race on history.
        var gate = _projectGates.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await _projects.ApplyEditsWithHistoryAsync(project, proposal.Edits, action, proposal.Summary, metadata, ct).ConfigureAwait(false);
            return new ChangeApplyResult(true, Array.Empty<string>(), r.Before, r.After);
        }
        finally { gate.Release(); }
    }
}
