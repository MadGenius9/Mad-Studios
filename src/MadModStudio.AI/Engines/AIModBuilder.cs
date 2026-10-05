using MadModStudio.AI.Tools;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.AI.Engines;

public sealed class PlanResult
{
    public AIPlan? Plan { get; init; }
    public AIRunResult? AIResult { get; init; }
    public string? Error { get; init; }
}

public sealed class GenerationResult
{
    public bool Applied { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public AIRunResult? AIResult { get; init; }
    public string? Error { get; init; }
    public RepairOutcome? Repair { get; init; }
}

/// <summary>Natural-language mod building: classify + plan against the indexed game, then generate files into a project.</summary>
public sealed class AIModBuilder
{
    private readonly IAIProvider _provider;
    private readonly IAIConsentService _consent;
    private readonly ProjectService _projects;
    private readonly GameProfileService _profiles;
    private readonly AIRepairEngine _repair;

    public AIModBuilder(IAIProvider provider, IAIConsentService consent, ProjectService projects, GameProfileService profiles, AIRepairEngine repair)
    {
        _provider = provider;
        _consent = consent;
        _projects = projects;
        _profiles = profiles;
        _repair = repair;
    }

    public async Task<PlanResult> PlanAsync(string request, GameProfile? profile, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        if (!_provider.IsConfigured) return new PlanResult { Error = $"{_provider.DisplayName} is not configured. Add an API key in Settings." };
        var index = _profiles.GetIndex(profile);
        if (index is null) return new PlanResult { Error = "The Game Profile must be indexed first so the plan can be based on your installed game (Game Profiles → Reindex Game)." };
        var notice = new AIEgressNotice(_provider.DisplayName, "Plan mod", new[] { "Your request text", "Game API names/signatures and XML entries the AI searches for" }, "Anthropic API (api.anthropic.com)");
        if (!await _consent.ConfirmAsync(notice, ct).ConfigureAwait(false)) return new PlanResult { Error = "Cancelled: you declined sending data to the AI provider." };

        var toolbox = new ModToolbox(null, null, index, profile, allowProposals: false, allowPlan: true);
        var ai = await _provider.RunAsync(new AIRunRequest { SystemPrompt = AIPrompts.Plan(profile), UserMessage = request, Effort = "high" }, toolbox, progress, ct).ConfigureAwait(false);
        if (toolbox.Plan is null)
            return new PlanResult { AIResult = ai, Error = ai.Error ?? "The AI did not submit a plan." + (string.IsNullOrWhiteSpace(ai.FinalText) ? "" : " " + ai.FinalText) };
        return new PlanResult { Plan = toolbox.Plan, AIResult = ai };
    }

    public async Task<GenerationResult> GenerateAsync(ModProject project, string request, AIPlan plan, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        var profile = project.GameProfileId is { } id ? await _profiles.GetAsync(id, ct).ConfigureAwait(false) : null;
        var index = _profiles.GetIndex(profile);
        var notice = new AIEgressNotice(_provider.DisplayName, "Generate mod files", new[] { "Your request and plan", "Project files the AI asks to read", "Game API names/signatures and XML entries the AI searches for" }, "Anthropic API (api.anthropic.com)");
        if (!await _consent.ConfirmAsync(notice, ct).ConfigureAwait(false)) return new GenerationResult { Error = "Cancelled: you declined sending data to the AI provider." };

        var toolbox = new ModToolbox(project, _projects, index, profile, allowProposals: true, allowPlan: false);
        var message = $"""
            Request: {request}

            Approved plan ({plan.Classification}):
            {string.Join("\n", plan.Steps.Select((s, i) => $"{i + 1}. {s}"))}
            Verified game APIs: {string.Join(", ", plan.GameApis)}
            """;
        var ai = await _provider.RunAsync(new AIRunRequest { SystemPrompt = AIPrompts.Generate(profile), UserMessage = message, Effort = "high", MaxTokens = 64000 }, toolbox, progress, ct).ConfigureAwait(false);
        if (toolbox.Proposal is null || toolbox.Proposal.Edits.Count == 0)
            return new GenerationResult { AIResult = ai, Error = ai.Error ?? "The AI did not produce any files." };

        await _projects.ApplyEditsAsync(project, toolbox.Proposal.Edits, "AI generation", toolbox.Proposal.Summary, ct).ConfigureAwait(false);
        progress?.Report(AIEvent.Now(AIEventKind.Info, $"Wrote {toolbox.Proposal.Edits.Count} file(s). Compiling and validating..."));
        var repair = await _repair.RepairAsync(project, new RepairRequestOptions(), progress, ct).ConfigureAwait(false);
        return new GenerationResult
        {
            Applied = true,
            Summary = toolbox.Proposal.Summary,
            Files = toolbox.Proposal.Edits.Select(e => e.RelativePath).ToList(),
            AIResult = ai,
            Repair = repair,
        };
    }
}
