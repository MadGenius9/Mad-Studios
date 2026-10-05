using MadModStudio.AI.Tools;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.AI.Engines;

public sealed class RepairAttempt
{
    public int Number { get; init; }
    public int ErrorsBefore { get; init; }
    public string? Summary { get; set; }
    public IReadOnlyList<string> ChangedFiles { get; set; } = Array.Empty<string>();
    public long? RevisionId { get; set; }
    public AIRunResult? AIResult { get; set; }
    public bool CompileSucceededAfter { get; set; }
    public int ValidationErrorsAfter { get; set; }
}

public sealed class RepairOutcome
{
    public List<RepairAttempt> Attempts { get; } = new();
    public bool Succeeded { get; set; }
    public string StopReason { get; set; } = "";
    public BuildResult? FinalBuild { get; set; }
}

public sealed class RepairRequestOptions
{
    /// <summary>Maximum automatic attempts before stopping and showing diagnostics. Default 3.</summary>
    public int MaxAttempts { get; init; } = 3;
    public bool RequireValidationPass { get; init; } = true;
    public string? UserInstructions { get; init; }
    public Diagnosis? Diagnosis { get; init; }
}

/// <summary>
/// Compiler → structured diagnostics → AI analysis (with game API search) → proposed patch → revision → apply → compile
/// again, bounded by <see cref="RepairRequestOptions.MaxAttempts"/>.
/// </summary>
public sealed class AIRepairEngine
{
    private readonly IAIProvider _provider;
    private readonly IAIConsentService _consent;
    private readonly ModBuildPipeline _pipeline;
    private readonly ProjectService _projects;
    private readonly GameProfileService _profiles;
    private readonly ILogger<AIRepairEngine> _log;
    private readonly AIOptions _options;

    public AIRepairEngine(IAIProvider provider, IAIConsentService consent, ModBuildPipeline pipeline, ProjectService projects, GameProfileService profiles, AIOptions? options = null, ILogger<AIRepairEngine>? log = null)
    {
        _provider = provider;
        _consent = consent;
        _pipeline = pipeline;
        _projects = projects;
        _profiles = profiles;
        _options = options ?? new AIOptions();
        _log = log ?? NullLogger<AIRepairEngine>.Instance;
    }

    public async Task<RepairOutcome> RepairAsync(ModProject project, RepairRequestOptions options, IProgress<AIEvent>? progress = null, CancellationToken ct = default)
    {
        var outcome = new RepairOutcome();
        if (!_provider.IsConfigured)
        {
            outcome.StopReason = $"{_provider.DisplayName} is not configured (no API key). Add a key in Settings to use AI repair.";
            return outcome;
        }
        var notice = new AIEgressNotice(_provider.DisplayName, "AI repair",
            new[] { "Compiler diagnostics", "Validation findings", "Project source files the AI asks to read", "Game API names/signatures and XML entries the AI searches for", options.Diagnosis != null ? "Log excerpts and diagnosis report" : "" }.Where(s => s.Length > 0).ToList(),
            "Anthropic API (api.anthropic.com)");
        if (!await _consent.ConfirmAsync(notice, ct).ConfigureAwait(false))
        {
            outcome.StopReason = "Cancelled: you declined sending data to the AI provider.";
            return outcome;
        }

        var profile = project.GameProfileId is { } id ? await _profiles.GetAsync(id, ct).ConfigureAwait(false) : null;
        var index = _profiles.GetIndex(profile);

        for (var n = 1; ; n++)
        {
            progress?.Report(AIEvent.Now(AIEventKind.Info, "Compiling and validating..."));
            var build = await _pipeline.RunAsync(project, new BuildOptions { Package = false }, null, ct).ConfigureAwait(false);
            outcome.FinalBuild = build;
            var compileOk = build.CompileSucceeded;
            var validationErrors = build.Validation?.ErrorCount ?? 0;
            if (outcome.Attempts.Count > 0)
            {
                outcome.Attempts[^1].CompileSucceededAfter = compileOk;
                outcome.Attempts[^1].ValidationErrorsAfter = validationErrors;
            }
            if (compileOk && (!options.RequireValidationPass || validationErrors == 0))
            {
                outcome.Succeeded = true;
                outcome.StopReason = outcome.Attempts.Count == 0 ? "Nothing to repair: the mod compiles and validates." : $"Repaired after {outcome.Attempts.Count} attempt(s).";
                progress?.Report(AIEvent.Now(AIEventKind.Info, outcome.StopReason));
                return outcome;
            }
            if (n > options.MaxAttempts)
            {
                outcome.StopReason = $"Stopped after {options.MaxAttempts} automatic attempt(s). Review the diagnostics; you can request another attempt manually.";
                progress?.Report(AIEvent.Now(AIEventKind.Warning, outcome.StopReason));
                return outcome;
            }

            var diagnostics = build.AllDiagnostics.Select(d => d.ToModDiagnostic())
                .Concat(build.Validation?.Findings.Where(f => f.Severity >= Severity.Warning).Select(f => f.ToDiagnostic()) ?? Enumerable.Empty<ModDiagnostic>())
                .ToList();
            var attempt = new RepairAttempt { Number = n, ErrorsBefore = diagnostics.Count(d => d.Severity == Severity.Error) };
            outcome.Attempts.Add(attempt);
            progress?.Report(AIEvent.Now(AIEventKind.Info, $"Attempt {n}/{options.MaxAttempts}: {attempt.ErrorsBefore} error(s). Asking {_provider.DisplayName}..."));

            var toolbox = new ModToolbox(project, _projects, index, profile, allowProposals: true, allowPlan: false)
            {
                Diagnostics = diagnostics,
                DiagnosisReport = options.Diagnosis?.Report,
                Logs = options.Diagnosis?.Logs ?? new(),
                Comparison = options.Diagnosis?.Comparison,
            };
            var message = $"""
                The mod project "{project.Name}" {project.Version} currently has these problems:
                {string.Join("\n", diagnostics.Take(60).Select(d => d.ToString()))}
                {(options.Diagnosis != null ? "\nA diagnosis report, attached logs and a version diff are available via tools." : "")}
                {(string.IsNullOrWhiteSpace(options.UserInstructions) ? "" : "\nUser instructions: " + options.UserInstructions)}
                """;
            var ai = await _provider.RunAsync(new AIRunRequest { SystemPrompt = AIPrompts.Repair(profile), UserMessage = message, Effort = "high", Model = _options.Model }, toolbox, progress, ct).ConfigureAwait(false);
            attempt.AIResult = ai;
            if (!ai.Success && toolbox.Proposal is null)
            {
                outcome.StopReason = ai.Error ?? "The AI request failed.";
                progress?.Report(AIEvent.Now(AIEventKind.Error, outcome.StopReason));
                return outcome;
            }
            if (toolbox.Proposal is null || toolbox.Proposal.Edits.Count == 0)
            {
                outcome.StopReason = "The AI did not propose any changes." + (string.IsNullOrWhiteSpace(ai.FinalText) ? "" : " Its explanation: " + ai.FinalText);
                progress?.Report(AIEvent.Now(AIEventKind.Warning, outcome.StopReason));
                return outcome;
            }
            attempt.Summary = toolbox.Proposal.Summary;
            attempt.ChangedFiles = toolbox.Proposal.Edits.Select(e => (e.IsDelete ? "D " : "W ") + e.RelativePath).ToList();
            var rev = await _projects.ApplyEditsAsync(project, toolbox.Proposal.Edits, $"AI repair attempt {n}", toolbox.Proposal.Summary, ct).ConfigureAwait(false);
            attempt.RevisionId = rev?.Id;
            progress?.Report(AIEvent.Now(AIEventKind.Info, $"Applied {toolbox.Proposal.Edits.Count} change(s) (revision recorded). Recompiling..."));
            _log.LogInformation("AI repair attempt {N} on {Project}: {Files}", n, project.Name, string.Join(", ", attempt.ChangedFiles));
        }
    }
}
