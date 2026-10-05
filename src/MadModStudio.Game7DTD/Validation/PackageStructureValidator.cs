using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Packaging;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>Checks a produced ZIP: one top-level mod folder with ModInfo.xml directly inside, no junk.</summary>
public sealed class PackageStructureValidator : ValidatorBase
{
    public override string Id => "package";
    public override string DisplayName => "ZIP package structure";
    public override bool IsApplicable(ValidationContext context) => context.PackagePath != null;

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        if (ctx.PackagePath is null) return;
        var file = Path.GetFileName(ctx.PackagePath);
        var p = PackageInspector.Inspect(ctx.PackagePath);
        if (!p.IsValidZip)
        {
            findings.Add(F(Severity.Error, $"Package is not a valid ZIP: {p.Error}", file));
            return;
        }
        if (p.RootFiles.Count > 0)
            findings.Add(F(Severity.Error, $"Package has files at the ZIP root ({string.Join(", ", p.RootFiles.Take(5))}); everything must be inside the mod folder.", file));
        if (p.TopLevelFolders.Count != 1)
            findings.Add(F(Severity.Error, $"Package should contain exactly one top-level mod folder; found {p.TopLevelFolders.Count}: {string.Join(", ", p.TopLevelFolders)}.", file));
        else if (!p.ModInfoLocations.Any(m => m.Equals(p.TopLevelFolders[0] + "/ModInfo.xml", StringComparison.OrdinalIgnoreCase)))
            findings.Add(F(Severity.Error, $"ModInfo.xml is not directly inside '{p.TopLevelFolders[0]}/'. Users extracting into Mods/ would get a broken mod.", file));
        foreach (var bad in p.ForbiddenEntries.Take(20))
            findings.Add(F(Severity.Error, $"Package contains a file that must never be shipped: {bad}", file));
    }
}
