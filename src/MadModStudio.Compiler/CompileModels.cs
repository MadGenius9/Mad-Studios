using MadModStudio.Core.Models;

namespace MadModStudio.Compiler;

public sealed class CompileRequest
{
    public required string AssemblyName { get; init; }
    public required IReadOnlyList<string> SourceFiles { get; init; }
    public required IReadOnlyList<string> ReferencePaths { get; init; }
    public required string OutputDirectory { get; init; }
    public BuildConfiguration Configuration { get; init; } = BuildConfiguration.Release;
    public string LanguageVersion { get; init; } = "9.0";
    public IReadOnlyList<string> PreprocessorSymbols { get; init; } = Array.Empty<string>();
    public bool AllowUnsafe { get; init; }
    public bool EmitPdb { get; init; } = true;
    /// <summary>Root used to display relative file paths in diagnostics.</summary>
    public string? SourceRoot { get; init; }
    public bool NullableEnabled { get; init; }
}

public sealed record CompilerDiagnostic(
    string Id,
    Severity Severity,
    string Message,
    string? FilePath,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn)
{
    public ModDiagnostic ToModDiagnostic() => new(Severity, Id, Message, "Compiler", FilePath, Line, Column);

    public override string ToString() =>
        FilePath is null ? $"{Severity.ToString().ToLowerInvariant()} {Id}: {Message}"
            : $"{FilePath}({Line},{Column}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

public sealed class CompileResult
{
    public bool Success { get; init; }
    public string? OutputAssemblyPath { get; init; }
    public string? PdbPath { get; init; }
    public IReadOnlyList<CompilerDiagnostic> Diagnostics { get; init; } = Array.Empty<CompilerDiagnostic>();
    public IReadOnlyList<string> ReferencesUsed { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
    /// <summary>Human readable build log (equivalent of stdout/stderr of a command-line compiler).</summary>
    public string Log { get; init; } = "";

    public int ErrorCount => Diagnostics.Count(d => d.Severity == Severity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == Severity.Warning);
    public IEnumerable<CompilerDiagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error);
}

public interface IModCompiler
{
    Task<CompileResult> CompileAsync(CompileRequest request, CancellationToken ct = default);
}
