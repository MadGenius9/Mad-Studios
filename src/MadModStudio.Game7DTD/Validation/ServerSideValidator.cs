using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Mods;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>When the project is marked Server-Side Only, reports evidence that clients would need the mod.</summary>
public sealed class ServerSideValidator : ValidatorBase
{
    public const string AnalysisKey = "mod.analysis";

    public override string Id => "server-side";
    public override string DisplayName => "Server-side only";
    public override bool IsApplicable(ValidationContext context) => context.Project is { ServerSideOnly: true };

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        if (ctx.Project is not { ServerSideOnly: true }) return;
        if (!ctx.Items.TryGetValue(AnalysisKey, out var a) || a is not ModAnalysisReport report) return;
        var side = report.Side;
        var severity = side.Result switch
        {
            SideRequirement.ClientRequired => Severity.Warning,
            SideRequirement.LikelyServerSide => Severity.Info,
            _ => Severity.Warning,
        };
        findings.Add(F(severity, $"Project is marked Server-Side Only; analysis result: {Display(side.Result)}.", evidence: string.Join("\n", side.Reasons)));
        foreach (var r in side.Reasons.Where(r => r.StartsWith("CLIENT REQUIRED")))
            findings.Add(F(Severity.Warning, r));
    }

    public static string Display(SideRequirement r) => r switch
    {
        SideRequirement.LikelyServerSide => "LIKELY SERVER-SIDE",
        SideRequirement.ClientRequired => "CLIENT REQUIRED",
        SideRequirement.Warning => "WARNING",
        _ => "UNKNOWN",
    };
}
