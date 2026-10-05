namespace MadModStudio.AI.Routing;

/// <summary>
/// Tracks estimated AI spend (only where model prices are known) and enforces the configured session/project/task
/// budgets before each request.
/// </summary>
public sealed class BudgetGuard
{
    private readonly object _gate = new();
    private decimal _session;
    private readonly Dictionary<Guid, decimal> _projects = new();
    private bool _unknownCostSeen;

    public decimal SessionSpend { get { lock (_gate) return _session; } }
    public bool UnknownCostSeen { get { lock (_gate) return _unknownCostSeen; } }

    public void Record(Guid? projectId, decimal? cost)
    {
        lock (_gate)
        {
            if (cost is null) { _unknownCostSeen = true; return; }
            _session += cost.Value;
            if (projectId is { } p) _projects[p] = _projects.GetValueOrDefault(p) + cost.Value;
        }
    }

    public decimal ProjectSpend(Guid projectId) { lock (_gate) return _projects.GetValueOrDefault(projectId); }

    /// <summary>Returns an error message when a budget is exhausted, otherwise null.</summary>
    public string? Check(AIPolicy policy, Guid? projectId, decimal taskSpend)
    {
        lock (_gate)
        {
            if (policy.SessionBudgetUsd is { } s && _session >= s) return $"Session AI budget of ${s:0.00} reached (estimated ${_session:0.00}). Raise it in Settings → AI to continue.";
            if (policy.ProjectBudgetUsd is { } p && projectId is { } id && _projects.GetValueOrDefault(id) >= p) return $"Project AI budget of ${p:0.00} reached. Raise it in Settings → AI to continue.";
            if (policy.TaskBudgetUsd is { } t && taskSpend >= t) return $"Task AI budget of ${t:0.00} reached for this task.";
            return null;
        }
    }
}
