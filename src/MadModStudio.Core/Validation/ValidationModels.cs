using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Models;

namespace MadModStudio.Core.Validation;

public sealed record ValidationFinding(
    string ValidatorId,
    Severity Severity,
    string Message,
    string? FilePath = null,
    int? Line = null,
    string? Evidence = null)
{
    public ModDiagnostic ToDiagnostic() => new(Severity, ValidatorId, Message, "Validator", FilePath, Line);
}

/// <summary>Everything a validator may look at. Optional members are null when unavailable.</summary>
public sealed class ValidationContext
{
    public required string ModRootPath { get; init; }
    public ModProject? Project { get; init; }
    public GameProfile? GameProfile { get; init; }
    public IGameKnowledgeIndex? GameIndex { get; init; }
    /// <summary>Set when a compile step ran in this pipeline.</summary>
    public CompileOutcome? Compile { get; init; }
    /// <summary>Set when validating a produced package.</summary>
    public string? PackagePath { get; init; }
    /// <summary>Arbitrary additional data for game-specific validators.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}

/// <summary>Minimal, compiler-agnostic summary of a compile so validators don't depend on Roslyn.</summary>
public sealed record CompileOutcome(bool Attempted, bool Succeeded, int ErrorCount, int WarningCount, string? OutputAssemblyPath, IReadOnlyList<ModDiagnostic> Diagnostics);

public interface IModValidator
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>False when the check is irrelevant for this context (it is then reported as skipped, never as passed).</summary>
    bool IsApplicable(ValidationContext context) => true;
    Task<IReadOnlyList<ValidationFinding>> ValidateAsync(ValidationContext context, CancellationToken ct = default);
}

public sealed class ValidationReport
{
    public ValidationReport(IReadOnlyList<ValidationFinding> findings, IReadOnlyList<string> validatorsRun, IReadOnlyList<string>? validatorsSkipped = null)
    {
        Findings = findings;
        ValidatorsRun = validatorsRun;
        ValidatorsSkipped = validatorsSkipped ?? Array.Empty<string>();
    }

    public IReadOnlyList<string> ValidatorsSkipped { get; }

    public IReadOnlyList<ValidationFinding> Findings { get; }
    public IReadOnlyList<string> ValidatorsRun { get; }
    public int ErrorCount => Findings.Count(f => f.Severity == Severity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);
    public bool HasErrors => ErrorCount > 0;
    public Severity Outcome => Findings.Count == 0 ? Severity.Pass : Findings.Max(f => f.Severity);
}

/// <summary>Runs a set of validators. A validator that throws produces an ERROR finding rather than crashing.</summary>
public sealed class ValidationRunner
{
    private readonly IEnumerable<IModValidator> _validators;

    public ValidationRunner(IEnumerable<IModValidator> validators) => _validators = validators;

    public async Task<ValidationReport> RunAsync(ValidationContext context, CancellationToken ct = default)
    {
        var findings = new List<ValidationFinding>();
        var ran = new List<string>();
        var skipped = new List<string>();
        foreach (var v in _validators)
        {
            ct.ThrowIfCancellationRequested();
            if (!v.IsApplicable(context))
            {
                skipped.Add(v.Id);
                continue;
            }
            ran.Add(v.Id);
            try
            {
                var result = await v.ValidateAsync(context, ct).ConfigureAwait(false);
                if (result.Count == 0)
                    findings.Add(new ValidationFinding(v.Id, Severity.Pass, $"{v.DisplayName}: passed"));
                else
                    findings.AddRange(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                findings.Add(new ValidationFinding(v.Id, Severity.Error, $"{v.DisplayName} could not run: {ex.Message}"));
            }
        }
        return new ValidationReport(findings, ran, skipped);
    }
}
