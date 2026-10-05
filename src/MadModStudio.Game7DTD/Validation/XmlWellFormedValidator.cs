using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Mods;

namespace MadModStudio.Game7DTD.Validation;

public sealed class XmlWellFormedValidator : ValidatorBase
{
    public override string Id => "xml-wellformed";
    public override string DisplayName => "Malformed XML";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        foreach (var rel in ModFiles(ctx).Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var r = ModAnalyzer.AnalyzeXml(Path.Combine(ctx.ModRootPath, rel), rel);
            if (!r.IsWellFormed)
                findings.Add(F(Severity.Error, $"Malformed XML: {r.Error}", rel, r.ErrorLine));
            else if (r.IsConfigFile && r.PatchOperationCount == 0 && r.RootElement != null)
                findings.Add(F(Severity.Info, $"Config file has no XPath patch operations (root <{r.RootElement}>). If it is meant to patch the game config, use <configs> with append/set/remove operations.", rel));
        }
    }
}
