using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

public sealed class CompilerResultValidator : ValidatorBase
{
    public override string Id => "compiler";
    public override string DisplayName => "Compiler result";
    public override bool IsApplicable(ValidationContext context) => context.Compile is { Attempted: true };

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var c = ctx.Compile;
        if (c is null || !c.Attempted) return;
        if (!c.Succeeded)
        {
            findings.Add(F(Severity.Error, $"Compilation failed with {c.ErrorCount} error(s). The packaged DLL would be stale or missing."));
            foreach (var e in c.Diagnostics.Where(d => d.Severity == Severity.Error).Take(20))
                findings.Add(F(Severity.Error, $"{e.Code}: {e.Message}", e.FilePath, e.Line));
            return;
        }
        if (c.OutputAssemblyPath is null || !File.Exists(c.OutputAssemblyPath))
            findings.Add(F(Severity.Error, "Compiler reported success but the output DLL is missing."));
        else if (c.WarningCount > 0)
            findings.Add(F(Severity.Info, $"Compiled successfully with {c.WarningCount} warning(s)."));
    }
}
