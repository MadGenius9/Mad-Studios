using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI.Routing;

/// <summary>
/// Tracks estimated AI spend (only where model prices are known) and enforces the configured session/project/task
/// budgets before each request. Every call is written to the spend ledger, so project budgets and the cost view are
/// based on all recorded calls, not just the current session.
/// </summary>
public sealed class BudgetGuard
{
    private readonly object _gate = new();
    private readonly IAISpendRepository? _ledger;
    private decimal _session;
    private bool _unknownCostSeen;

    public BudgetGuard(IAISpendRepository? ledger = null) => _ledger = ledger;

    public decimal SessionSpend { get { lock (_gate) return _session; } }
    public bool UnknownCostSeen { get { lock (_gate) return _unknownCostSeen; } }

    public async Task RecordAsync(Guid? projectId, MadModStudio.AI.Models.ModelInfo model, AgentKind agent, long inputTokens, long outputTokens, decimal? cost, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (cost is null) _unknownCostSeen = true;
            else _session += cost.Value;
        }
        if (_ledger != null)
            await _ledger.AddAsync(new AISpendRecord
            {
                ProjectId = projectId, Provider = model.ProviderId, Model = model.ModelId, Agent = agent,
                InputTokens = inputTokens, OutputTokens = outputTokens, CostUsd = cost,
            }, ct).ConfigureAwait(false);
    }

    /// <summary>Returns an error message when a budget is exhausted, otherwise null.</summary>
    public async Task<string?> CheckAsync(AIPolicy policy, Guid? projectId, decimal taskSpend, CancellationToken ct = default)
    {
        decimal session;
        lock (_gate) session = _session;
        if (policy.SessionBudgetUsd is { } s && session >= s)
            return $"Session AI budget of ${s:0.00} reached (estimated ${session:0.00}). Raise it in Settings → AI to continue.";
        if (policy.ProjectBudgetUsd is { } p && projectId is { } id && _ledger != null)
        {
            var spent = await _ledger.ProjectTotalAsync(id, ct).ConfigureAwait(false);
            if (spent >= p) return $"Project AI budget of ${p:0.00} reached (estimated ${spent:0.00} recorded for this project). Raise it in Settings → AI to continue.";
        }
        if (policy.TaskBudgetUsd is { } t && taskSpend >= t) return $"Task AI budget of ${t:0.00} reached for this task.";
        return null;
    }
}
