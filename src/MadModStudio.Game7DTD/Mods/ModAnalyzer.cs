using System.Xml;
using MadModStudio.Compiler;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.Game7DTD.Mods;

/// <summary>
/// Static analysis of a 7 Days to Die mod folder. Reads files only; DLLs are inspected through metadata and never loaded.
/// </summary>
public sealed class ModAnalyzer
{
    public static readonly string[] PatchOperations =
    {
        "append", "prepend", "set", "setattribute", "remove", "removeattribute", "insertafter", "insertbefore", "csv", "conditional",
    };

    private static readonly string[] AssetExtensions = { ".unity3d", ".bundle", ".png", ".jpg", ".jpeg", ".tga", ".dds", ".wav", ".ogg", ".mp3", ".mesh", ".prefab", ".tts", ".nim", ".ins" };

    private readonly AssemblyInspector _inspector;
    private readonly SourceHarmonyScanner _harmonyScanner;

    public ModAnalyzer(AssemblyInspector inspector, SourceHarmonyScanner harmonyScanner)
    {
        _inspector = inspector;
        _harmonyScanner = harmonyScanner;
    }

    /// <param name="modRoot">Folder containing ModInfo.xml.</param>
    /// <param name="sourceRoot">Folder to search for C# source (may contain the mod root). Defaults to modRoot.</param>
    public ModAnalysisReport Analyze(string modRoot, string? sourceRoot = null, IGameKnowledgeIndex? index = null, GameProfile? profile = null)
    {
        sourceRoot ??= modRoot;
        var report = new ModAnalysisReport { ModRootPath = modRoot, SourceRootPath = sourceRoot };
        if (!Directory.Exists(modRoot))
        {
            report.Findings.Add(new ValidationFinding("analysis", Severity.Error, $"Mod folder does not exist: {modRoot}"));
            return report;
        }

        // ModInfo
        var modInfoPath = Directory.GetFiles(modRoot).FirstOrDefault(f => Path.GetFileName(f).Equals(ModInfoFile.FileName, StringComparison.OrdinalIgnoreCase));
        if (modInfoPath is null)
        {
            report.ModInfoError = "ModInfo.xml not found in the mod folder.";
            report.Findings.Add(new ValidationFinding("analysis", Severity.Error, report.ModInfoError));
        }
        else
        {
            report.ModInfo = ModInfoFile.TryParse(modInfoPath, out var err);
            report.ModInfoError = err;
            if (err != null) report.Findings.Add(new ValidationFinding("analysis", Severity.Error, err, ModInfoFile.FileName));
        }

        var rules = new FileExclusionRules { IncludeSource = true, IncludePdb = true };
        var files = FileUtil.EnumerateRelativeFiles(modRoot, rules).ToList();
        report.TotalFiles = files.Count;

        foreach (var rel in files)
        {
            var full = Path.Combine(modRoot, rel);
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            var name = Path.GetFileName(rel);
            if (name.Equals(ModInfoFile.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            switch (ext)
            {
                case ".xml":
                    report.XmlFiles.Add(AnalyzeXml(full, rel));
                    if (rel.StartsWith("Config/XUi", StringComparison.OrdinalIgnoreCase) || rel.Contains("/XUi", StringComparison.OrdinalIgnoreCase))
                        report.XuiFiles.Add(rel);
                    break;
                case ".dll":
                    var asm = _inspector.Inspect(full);
                    report.Dlls.Add(new DllReport { RelativePath = rel, Assembly = asm });
                    if (!asm.Success)
                        report.Findings.Add(new ValidationFinding("analysis", asm.ReadError?.StartsWith("Not a managed") == true ? Severity.Info : Severity.Warning,
                            $"{rel}: {asm.ReadError}", rel));
                    break;
                // Localization.txt before V3.0, Localization.csv since.
                case ".txt" or ".csv" when name.StartsWith("Localization", StringComparison.OrdinalIgnoreCase):
                    report.LocalizationFiles.Add(rel);
                    break;
                case ".cs" or ".csproj" or ".sln":
                    break; // handled via sourceRoot scan
                default:
                    if (AssetExtensions.Contains(ext) || rel.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("UIAtlases/", StringComparison.OrdinalIgnoreCase))
                        report.AssetFiles.Add(rel);
                    else
                        report.OtherFiles.Add(rel);
                    break;
            }
        }
        foreach (var bad in report.XmlFiles.Where(x => !x.IsWellFormed))
            report.Findings.Add(new ValidationFinding("analysis", Severity.Error, $"Malformed XML: {bad.Error}", bad.RelativePath, bad.ErrorLine));

        // Source code (may live outside the mod folder, e.g. repo/src next to repo/ModName)
        var srcRules = FileExclusionRules.ForSnapshots();
        foreach (var rel in FileUtil.EnumerateRelativeFiles(sourceRoot, srcRules))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext == ".cs") report.SourceFiles.Add(rel);
            else if (ext == ".csproj") report.CsProjects.Add(CsprojReader.Read(Path.Combine(sourceRoot, rel)));
        }
        if (report.SourceFiles.Count > 0)
            report.HarmonyPatches.AddRange(_harmonyScanner.ScanFiles(report.SourceFiles.Select(f => Path.Combine(sourceRoot, f)), sourceRoot));
        foreach (var dll in report.Dlls.Where(d => d.Assembly.Success))
            report.HarmonyPatches.AddRange(dll.Assembly.HarmonyPatches.Select(p => p with { File = dll.RelativePath }));

        // Dependencies of bundled DLLs that are neither in the game, the mod, nor Harmony.
        var localNames = report.Dlls.Where(d => d.Assembly.Success).Select(d => d.Assembly.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in report.Dlls.Where(d => d.Assembly.Success))
        {
            foreach (var r in dll.Assembly.References)
            {
                if (localNames.Contains(r.Name)) continue;
                if (r.Name is "0Harmony") continue;
                if (profile?.ManagedPath != null && File.Exists(Path.Combine(profile.ManagedPath, r.Name + ".dll"))) continue;
                if (profile is null && (r.Name.StartsWith("System") || r.Name is "mscorlib" or "netstandard" || r.Name.StartsWith("Unity") || r.Name.StartsWith("Assembly-CSharp"))) continue;
                if (!report.UnresolvedDependencies.Contains(r.Name)) report.UnresolvedDependencies.Add(r.Name);
            }
        }

        var hasCode = report.Dlls.Any(d => d.Assembly.Success) || report.SourceFiles.Count > 0;
        var hasXml = report.XmlFiles.Any(x => x.IsConfigFile);
        report.ModType = hasCode && hasXml ? ModType.Hybrid : hasCode ? ModType.HarmonyCSharp : hasXml ? ModType.XmlOnly : ModType.Unknown;

        report.Side = SideRequirementAnalyzer.Analyze(report);
        report.Eac = SideRequirementAnalyzer.AnalyzeEac(report);
        return report;
    }

    public static XmlFileReport AnalyzeXml(string fullPath, string rel)
    {
        var isConfig = rel.StartsWith("Config/", StringComparison.OrdinalIgnoreCase);
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(fullPath, settings);
            string? root = null;
            var ops = 0;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                root ??= reader.LocalName;
                if (reader.GetAttribute("xpath") != null && PatchOperations.Contains(reader.LocalName.ToLowerInvariant())) ops++;
            }
            return new XmlFileReport { RelativePath = rel, IsWellFormed = true, RootElement = root, PatchOperationCount = ops, IsConfigFile = isConfig };
        }
        catch (XmlException ex)
        {
            return new XmlFileReport { RelativePath = rel, IsWellFormed = false, Error = ex.Message, ErrorLine = ex.LineNumber, IsConfigFile = isConfig };
        }
        catch (IOException ex)
        {
            return new XmlFileReport { RelativePath = rel, IsWellFormed = false, Error = ex.Message, IsConfigFile = isConfig };
        }
    }
}
