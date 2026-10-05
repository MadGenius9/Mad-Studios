using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Validation;

public sealed class DuplicateFileValidator : ValidatorBase
{
    public override string Id => "duplicates";
    public override string DisplayName => "Duplicate files";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var files = ModFiles(ctx).ToList();
        foreach (var g in files.GroupBy(f => f, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            findings.Add(F(Severity.Error, $"Files differ only by letter case: {string.Join(", ", g)}. Windows cannot store both.", g.First()));

        var modInfos = files.Where(f => Path.GetFileName(f).Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)).ToList();
        if (modInfos.Count > 1)
            findings.Add(F(Severity.Warning, $"Multiple ModInfo.xml files found ({string.Join(", ", modInfos)}). Nested mods inside a mod folder are not loaded as intended.", modInfos[1]));

        var configs = files.Where(f => f.StartsWith("Config/", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var g in configs.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Select(Path.GetDirectoryName).Distinct().Count() > 1))
            findings.Add(F(Severity.Info, $"Config file name appears in several folders: {string.Join(", ", g)}. Only the path relative to Config/ decides which game file is patched.", g.First()));

        var dlls = files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        var byAsm = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dlls)
        {
            var id = AssemblyInspector.ReadIdentity(Path.Combine(ctx.ModRootPath, d));
            if (id is null) continue;
            if (!byAsm.TryGetValue(id.Value.Name, out var list)) byAsm[id.Value.Name] = list = new List<string>();
            list.Add(d);
        }
        foreach (var (name, list) in byAsm.Where(kv => kv.Value.Count > 1))
            findings.Add(F(Severity.Warning, $"Assembly '{name}' is included {list.Count} times: {string.Join(", ", list)}.", list[0]));
        // Only a problem when a regular mod ships Harmony alongside its own code (not the Harmony mod itself).
        var bundlesOtherCode = byAsm.Keys.Any(k => !k.Equals("0Harmony", StringComparison.OrdinalIgnoreCase));
        foreach (var d in byAsm.Where(kv => bundlesOtherCode && kv.Key.Equals("0Harmony", StringComparison.OrdinalIgnoreCase)).SelectMany(kv => kv.Value))
            findings.Add(F(Severity.Warning, "The mod bundles its own 0Harmony.dll. The game already ships Harmony (Mods/0_TFP_Harmony); a second copy can conflict.", d));
    }
}
