using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.AI.Agents;

public sealed class PendingChange
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Guid ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required string Agent { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<FileEdit> Edits { get; init; }
    /// <summary>Unified diff of every edit against the current file contents.</summary>
    public required string Diff { get; init; }
    public DateTimeOffset CreatedUtc { get; } = DateTimeOffset.UtcNow;
    internal TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Decides whether a proposed AI change may be applied (GUIDED / MANUAL control).</summary>
public interface IChangeApprovalService
{
    Task<bool> RequestAsync(PendingChange change, CancellationToken ct = default);
}

/// <summary>Queue the UI observes; the user approves or rejects each proposal. Cancellation rejects.</summary>
public sealed class ApprovalQueue : IChangeApprovalService
{
    private readonly object _gate = new();
    private readonly List<PendingChange> _pending = new();

    public event EventHandler? Changed;

    public IReadOnlyList<PendingChange> Pending { get { lock (_gate) return _pending.ToList(); } }

    public async Task<bool> RequestAsync(PendingChange change, CancellationToken ct = default)
    {
        lock (_gate) _pending.Add(change);
        Changed?.Invoke(this, EventArgs.Empty);
        using (ct.Register(() => change.Decision.TrySetResult(false)))
        {
            try { return await change.Decision.Task.ConfigureAwait(false); }
            finally
            {
                lock (_gate) _pending.Remove(change);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Approve(Guid id) => Resolve(id, true);
    public void Reject(Guid id) => Resolve(id, false);

    private void Resolve(Guid id, bool ok)
    {
        PendingChange? c;
        lock (_gate) c = _pending.FirstOrDefault(p => p.Id == id);
        c?.Decision.TrySetResult(ok);
    }
}

/// <summary>Non-interactive policy (CLI / tests): approve everything or nothing.</summary>
public sealed class FixedApproval : IChangeApprovalService
{
    private readonly bool _approve;
    public FixedApproval(bool approve) => _approve = approve;
    public List<PendingChange> Seen { get; } = new();
    public Task<bool> RequestAsync(PendingChange change, CancellationToken ct = default)
    {
        lock (Seen) Seen.Add(change);
        return Task.FromResult(_approve);
    }
}
