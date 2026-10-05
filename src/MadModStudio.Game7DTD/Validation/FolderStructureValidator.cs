using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

public sealed class FolderStructureValidator : ValidatorBase
{
    private static readonly string[] ExecutableExtensions = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".sh", ".msi", ".scr", ".com", ".jar" };
    private static readonly string[] KnownTopLevel = { "Config", "Resources", "UIAtlases", "Prefabs", "ItemIcons", "Textures", "Sounds", "Harmony", "Scripts", "Source", "src", "Docs", "XUi", "Assets", "Data" };

    public override string Id => "structure";
    public override string DisplayName => "Folder structure";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var files = ModFiles(ctx).ToList();
        if (files.Count == 0)
        {
            findings.Add(F(Severity.Error, "The mod folder is empty."));
            return;
        }
        var topDirs = Directory.GetDirectories(ctx.ModRootPath).Select(Path.GetFileName).ToList();
        var config = topDirs.FirstOrDefault(d => d!.Equals("Config", StringComparison.OrdinalIgnoreCase));
        if (config != null && config != "Config")
            findings.Add(F(Severity.Warning, $"Folder '{config}' should be named 'Config' exactly. Linux dedicated servers use case-sensitive paths.", config));

        var hasConfig = files.Any(f => f.StartsWith("Config/", StringComparison.OrdinalIgnoreCase));
        var hasDll = files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (!hasConfig && !hasDll && !files.Any(f => f.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Prefabs/", StringComparison.OrdinalIgnoreCase)))
            findings.Add(F(Severity.Warning, "The mod has no Config folder, no DLL and no resources — it may not do anything when loaded."));

        foreach (var x in files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                     && !f.StartsWith("Config/", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(f).Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)).Take(20))
            findings.Add(F(Severity.Info, "XML file outside the Config folder is not applied as a config patch.", x));

        foreach (var exe in files.Where(f => ExecutableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            findings.Add(F(Severity.Warning, "Executable/script file inside a mod folder is suspicious and is never needed by the game.", exe));

        var subMods = files.Where(f => Path.GetFileName(f).Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase) && f.Contains('/')).ToList();
        foreach (var s in subMods)
            findings.Add(F(Severity.Warning, "A nested ModInfo.xml suggests a mod folder inside another mod folder. Each mod must be its own folder directly under Mods/.", s));

        foreach (var d in topDirs.Where(d => !KnownTopLevel.Contains(d, StringComparer.OrdinalIgnoreCase)))
            findings.Add(F(Severity.Info, $"Unusual top-level folder '{d}'.", d));
    }
}
