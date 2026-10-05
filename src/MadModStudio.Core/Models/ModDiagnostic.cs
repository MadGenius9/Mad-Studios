namespace MadModStudio.Core.Models;

/// <summary>A located problem from any source (compiler, validator, log analyzer, analyzer).</summary>
public sealed record ModDiagnostic(
    Severity Severity,
    string Code,
    string Message,
    string Source,
    string? FilePath = null,
    int? Line = null,
    int? Column = null)
{
    public string Location => FilePath is null
        ? ""
        : Line is null ? FilePath : $"{FilePath}({Line}{(Column is null ? "" : "," + Column)})";

    public override string ToString() =>
        string.IsNullOrEmpty(Location) ? $"{Severity} {Code}: {Message}" : $"{Location}: {Severity} {Code}: {Message}";
}
