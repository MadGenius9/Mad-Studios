using System.Diagnostics;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI.Agents;

public sealed class AgentExecutionResult
{
    public required AgentToolbox Toolbox { get; init; }
    public required AIRunResult Ai { get; init; }
    public required ModelInfo Model { get; init; }
    public long LatencyMs { get; init; }
    public decimal? EstimatedCost { get; init; }
}

/// <summary>Runs one agent definition on one model: builds context, restricts tools, calls the provider, accounts cost.</summary>
public sealed class AgentRunner
{
    private readonly IAIProviderRegistry _providers;
    private readonly AgentCatalog _agents;
    private readonly IAIContextBuilder _context;
    private readonly BudgetGuard _budget;
    private readonly AIPolicy _policy;

    public AgentRunner(IAIProviderRegistry providers, AgentCatalog agents, IAIContextBuilder context, BudgetGuard budget, AIPolicy policy)
    {
        _providers = providers;
        _agents = agents;
        _context = context;
        _budget = budget;
        _policy = policy;
    }

    public async Task<AgentExecutionResult> ExecuteAsync(ToolContext ctx, ModelInfo model, string? userInstructions, IProgress<AIEvent>? progress,
        decimal taskSpend = 0, string? extraContext = null, CancellationToken ct = default)
    {
        var def = _agents.Get(ctx.Task.Agent);
        var toolbox = new AgentToolbox(ctx, def.Tools);
        AgentExecutionResult Fail(string error) => new() { Toolbox = toolbox, Model = model, Ai = new AIRunResult { Error = error, ModelUsed = model.ModelId } };

        if (await _budget.CheckAsync(_policy, ctx.Project.Id, taskSpend, ct).ConfigureAwait(false) is { } budgetError) return Fail(budgetError);
        var provider = _providers.Get(model.ProviderId);
        if (provider is null || !provider.IsConfigured) return Fail($"{model.ProviderName} is not configured.");
        if (def.NeedsSource && !_policy.AllowSourceToExternal && def.CanWrite)
            return Fail("This agent needs to read project source, but sending source to external providers is disabled (Settings → AI → Privacy).");

        var context = await _context.BuildAsync(new ContextRequest
        {
            Project = ctx.Project,
            Profile = ctx.Profile,
            Task = ctx.Task,
            UserInstructions = userInstructions,
            Sections = def.NeedsLogs || ctx.Task.Agent is AgentKind.Lead ? ContextSection.All : ContextSection.All & ~ContextSection.LogFindings,
        }, ct).ConfigureAwait(false);

        var system = $"{AgentCatalog.GroundRules}\n\nAGENT: {def.DisplayName}\n{def.Purpose}";
        if (ctx.Task.Agent == AgentKind.Lead)
            system += "\n\nAvailable specialist agents:\n" + string.Join("\n", _agents.Specialists.Where(s => ctx.PlannableAgents.Contains(s.Kind))
                .Select(s => $"- {s.Kind}: {s.DisplayName} — {s.Purpose.Split(". ")[0].Replace("You are the ", "")}"));
        var message = $"{context}\n{extraContext}\nComplete the CURRENT TASK using the tools. Keep your final message to a short summary of what you found or did, with evidence.";

        var sw = Stopwatch.StartNew();
        var ai = await provider.RunAsync(new AIRunRequest
        {
            SystemPrompt = system,
            UserMessage = message,
            Model = model.ModelId,
            MaxToolTurns = def.MaxToolTurns,
            MaxTokens = def.CanWrite ? 64000 : 16000,
            ReasoningEffort = "high",
            Tag = def.Kind.ToString(),
        }, toolbox, progress, ct).ConfigureAwait(false);
        sw.Stop();
        var cost = model.EstimateCost(ai.Usage.InputTokens, ai.Usage.OutputTokens);
        await _budget.RecordAsync(ctx.Project.Id, model, def.Kind, ai.Usage.InputTokens, ai.Usage.OutputTokens, cost, CancellationToken.None).ConfigureAwait(false);
        return new AgentExecutionResult { Toolbox = toolbox, Ai = ai, Model = model, LatencyMs = sw.ElapsedMilliseconds, EstimatedCost = cost };
    }
}
