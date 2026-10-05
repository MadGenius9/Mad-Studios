using MadModStudio.AI.Models;
using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI.Routing;

public sealed class RoutingRequest
{
    public AgentKind Agent { get; init; }
    public AITaskType TaskType { get; init; }
    public RoutingMode Mode { get; init; } = RoutingMode.Auto;
    public bool RequiresTools { get; init; } = true;
    public int? EstimatedContextTokens { get; init; }
    /// <summary>Models that must not be chosen (e.g. failed this task, or used by the implementer for an independent review).</summary>
    public IReadOnlySet<string> ExcludedModels { get; init; } = new HashSet<string>();
    /// <summary>"provider/model" chosen by the user for this agent (MANUAL); always honoured if available.</summary>
    public string? ManualModel { get; init; }
    /// <summary>Restrict to this provider unless the user allowed cross-provider routing.</summary>
    public string? CurrentProvider { get; init; }
    /// <summary>Prefer models from a different provider/model than these (independent review).</summary>
    public IReadOnlySet<string> PreferDifferentFrom { get; init; } = new HashSet<string>();
    /// <summary>Recent failures of models on this very task (for "previous model failed" reasons).</summary>
    public IReadOnlyDictionary<string, int> FailuresOnThisTask { get; init; } = new Dictionary<string, int>();
    /// <summary>
    /// For suggestions shown to the user (escalation): consider every configured provider. Nothing is sent until the user
    /// accepts the suggestion.
    /// </summary>
    public bool ForSuggestionOnly { get; init; }
}

public sealed record ScoredModel(ModelInfo Model, double Score, IReadOnlyList<string> Reasons);

public sealed record RoutingDecision(ModelInfo? Model, IReadOnlyList<string> Reasons, IReadOnlyList<ScoredModel> Candidates, string? Error)
{
    public static RoutingDecision Fail(string error) => new(null, Array.Empty<string>(), Array.Empty<ScoredModel>(), error);
}

public interface IModelRouter
{
    Task<RoutingDecision> SelectAsync(RoutingRequest request, CancellationToken ct = default);
}

/// <summary>
/// Chooses the most appropriate available model. Scores come from configured capabilities and Mad Mod Studio's own
/// recorded history — never from fixed assumptions about which vendor is "best" at a task.
/// </summary>
public sealed class ModelRouter : IModelRouter
{
    private readonly IModelCatalog _catalog;
    private readonly IAIProviderRegistry _providers;
    private readonly IModelPerformanceTracker _performance;
    private readonly AIPolicy _policy;

    public ModelRouter(IModelCatalog catalog, IAIProviderRegistry providers, IModelPerformanceTracker performance, AIPolicy policy)
    {
        _catalog = catalog;
        _providers = providers;
        _performance = performance;
        _policy = policy;
    }

    public async Task<RoutingDecision> SelectAsync(RoutingRequest request, CancellationToken ct = default)
    {
        var configured = _catalog.Models.Where(m => m.Available && _providers.Get(m.ProviderId)?.IsConfigured == true).ToList();
        if (configured.Count == 0)
            return RoutingDecision.Fail("No AI models are available. Configure a provider in Settings → AI Providers and click Refresh Models.");

        // 1. Explicit user choices always win when available.
        if (request.ManualModel is { } manual)
        {
            var m = _catalog.Find(manual);
            if (m is { Available: true }) return new RoutingDecision(m, new[] { "Selected manually for this agent." }, Array.Empty<ScoredModel>(), null);
            return RoutingDecision.Fail($"The manually selected model {manual} is not available. Check Settings → AI Providers or choose another model.");
        }
        if (_policy.TaskPreferences.TryGetValue(request.TaskType, out var locked) && !request.ExcludedModels.Contains(locked))
        {
            var m = _catalog.Find(locked);
            if (m is { Available: true }) return new RoutingDecision(m, new[] { $"Locked by your preference for {request.TaskType} tasks." }, Array.Empty<ScoredModel>(), null);
        }
        if (request.Mode == RoutingMode.Manual)
        {
            var d = _catalog.Find(_policy.DefaultModel);
            return d is { Available: true }
                ? new RoutingDecision(d, new[] { "Routing mode is MANUAL: using your default model." }, Array.Empty<ScoredModel>(), null)
                : RoutingDecision.Fail("Routing mode is MANUAL but no default model is set (Settings → AI). Choose a model for this agent.");
        }

        // 2. Hard filters.
        var allowedProvider = _policy.AllowCrossProviderRouting || request.ForSuggestionOnly ? null : request.CurrentProvider ?? _policy.DefaultProvider ?? ModelKey.Parse(_policy.DefaultModel)?.Provider;
        var candidates = configured
            .Where(m => !request.ExcludedModels.Contains(m.Key))
            .Where(m => !request.RequiresTools || m.SupportsTools)
            .Where(m => request.EstimatedContextTokens is null || m.ContextTokens is null || m.ContextTokens >= request.EstimatedContextTokens)
            .ToList();
        if (allowedProvider != null)
        {
            var sameProvider = candidates.Where(m => m.ProviderId.Equals(allowedProvider, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameProvider.Count > 0) candidates = sameProvider;
            else if (candidates.Count > 0 && !_policy.AllowCrossProviderRouting)
                return RoutingDecision.Fail($"No suitable model from {allowedProvider} is available and automatic cross-provider routing is off (Settings → AI → Privacy).");
        }
        if (candidates.Count == 0)
            return RoutingDecision.Fail(request.ExcludedModels.Count > 0
                ? "Every suitable model has already been tried or excluded for this task."
                : "No available model meets this task's requirements (tool support / context size).");

        // 3. Score.
        var stats = (await _performance.GetStatsAsync(ct).ConfigureAwait(false)).Where(s => s.TaskType == request.TaskType).ToList();
        var maxCost = candidates.Where(c => c.CostKnown).Select(BlendedCost).DefaultIfEmpty(0).Max();
        var scored = new List<ScoredModel>();
        foreach (var m in candidates)
        {
            var reasons = new List<string>();
            var quality = m.Tier switch { ModelTier.Frontier => 1.0, ModelTier.Balanced => 0.7, ModelTier.Fast => 0.45, _ => 0.5 };
            var speed = m.Tier switch { ModelTier.Fast => 1.0, ModelTier.Balanced => 0.7, ModelTier.Frontier => 0.4, _ => 0.5 };
            var cost = m.CostKnown && maxCost > 0 ? 1.0 - (double)(BlendedCost(m) / maxCost) * 0.9 : 0.5;
            var stat = stats.FirstOrDefault(s => s.Provider == m.ProviderId && s.Model == m.ModelId);
            double? history = null;
            if (stat is { HasEnoughData: true })
            {
                // Laplace-smoothed success rate from Mad Mod Studio's own recorded tasks.
                history = (stat.Successes + 1.0) / (stat.Total + 2.0);
                var line = $"{stat.Successes} of {stat.Total} similar {request.TaskType} tasks succeeded in your history";
                if (stat.ValidationAttempts > 0) line += $"; {stat.ValidationPasses} of {stat.ValidationAttempts} passed validation";
                if (stat.Reverted > 0) line += $"; {stat.Reverted} reverted by you";
                reasons.Add(line + ".");
            }
            else if (stat != null) reasons.Add($"Only {stat.Total} recorded {request.TaskType} task(s) — not enough history to weigh.");

            var (wq, ws, wc, wh) = request.Mode switch
            {
                RoutingMode.BestQuality => (0.6, 0.0, 0.0, 0.4),
                RoutingMode.Balanced => (0.35, 0.15, 0.2, 0.3),
                RoutingMode.Fast => (0.2, 0.6, 0.0, 0.2),
                RoutingMode.Economy => (0.2, 0.0, 0.6, 0.2),
                _ => (0.4, 0.1, 0.1, 0.4),
            };
            var score = history is null
                ? (wq * quality + ws * speed + wc * cost) / Math.Max(0.0001, wq + ws + wc)
                : wq * quality + ws * speed + wc * cost + wh * history.Value;

            if (m.Tier != ModelTier.Unknown) reasons.Add($"Capability profile: {m.Tier} tier.");
            if (request.Mode is RoutingMode.Economy or RoutingMode.Balanced && m.CostKnown) reasons.Add($"Estimated price ${m.InputCostPerMTok}/{m.OutputCostPerMTok} per million tokens.");
            if (request.EstimatedContextTokens is { } need && m.ContextTokens is { } ctx) reasons.Add($"Context {ctx:N0} tokens covers the ~{need:N0} needed.");
            if (request.PreferDifferentFrom.Count > 0)
            {
                if (request.PreferDifferentFrom.Contains(m.Key)) score -= 0.5;
                else if (request.PreferDifferentFrom.Any(k => ModelKey.Parse(k)?.Provider == m.ProviderId)) score -= 0.1;
                else { score += 0.1; reasons.Add("Independent of the model that produced the change."); }
            }
            scored.Add(new ScoredModel(m, score, reasons));
        }
        var ordered = scored.OrderByDescending(s => s.Score).ThenBy(s => s.Model.Key, StringComparer.Ordinal).ToList();
        var best = ordered[0];
        var why = new List<string>(best.Reasons);
        why.Insert(0, $"Routing mode {request.Mode}; best of {ordered.Count} available model(s) for {request.TaskType}.");
        foreach (var (k, n) in request.FailuresOnThisTask.Where(f => f.Value > 0))
            why.Add($"{k} failed this task {n} time(s).");
        why.Add($"{best.Model.ProviderName} is configured and available.");
        return new RoutingDecision(best.Model, why, ordered, null);
    }

    private static decimal BlendedCost(ModelInfo m) => (m.InputCostPerMTok ?? 0) * 3 + (m.OutputCostPerMTok ?? 0);
}
