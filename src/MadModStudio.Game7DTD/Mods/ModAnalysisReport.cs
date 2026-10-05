using MadModStudio.Compiler;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.Game7DTD.Mods;

public sealed class XmlFileReport
{
    public string RelativePath { get; init; } = "";
    public bool IsWellFormed { get; init; }
    public string? Error { get; init; }
    public int? ErrorLine { get; init; }
    public string? RootElement { get; init; }
    /// <summary>Number of XPath patch operations (append/set/remove/insertAfter/...).</summary>
    public int PatchOperationCount { get; init; }
    public bool IsConfigFile { get; init; }
}

public sealed class DllReport
{
    public string RelativePath { get; init; } = "";
    public AssemblyReport Assembly { get; init; } = new();
}

public sealed record Evidence(string Text, SideRequirement Points);

public sealed class SideAnalysis
{
    public SideRequirement Result { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public sealed class EacAnalysis
{
    public EacCompatibility Result { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
}

public sealed class ModAnalysisReport
{
    public string ModRootPath { get; init; } = "";
    public string SourceRootPath { get; init; } = "";
    public ModInfoData? ModInfo { get; set; }
    public string? ModInfoError { get; set; }
    public ModType ModType { get; set; }
    public List<XmlFileReport> XmlFiles { get; } = new();
    public List<DllReport> Dlls { get; } = new();
    public List<string> SourceFiles { get; } = new();
    public List<CsprojInfo> CsProjects { get; } = new();
    public List<HarmonyPatchInfo> HarmonyPatches { get; } = new();
    public List<string> LocalizationFiles { get; } = new();
    public List<string> XuiFiles { get; } = new();
    public List<string> AssetFiles { get; } = new();
    public List<string> OtherFiles { get; } = new();
    public List<string> UnresolvedDependencies { get; } = new();
    public List<ValidationFinding> Findings { get; } = new();
    public SideAnalysis Side { get; set; } = new();
    public EacAnalysis Eac { get; set; } = new();
    public int TotalFiles { get; set; }

    public bool SourceAvailable => SourceFiles.Count > 0;
    public int Errors => Findings.Count(f => f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);
    public int ConfigXmlCount => XmlFiles.Count(x => x.IsConfigFile);

    public string Summary() =>
        $"Mod Type: {ModType}\n" +
        $"XML Files: {XmlFiles.Count}\n" +
        $"DLLs: {Dlls.Count}\n" +
        $"Source Available: {(SourceAvailable ? "Yes" : "No")}\n" +
        $"Harmony Patches Detected: {HarmonyPatches.Count}\n" +
        $"Warnings: {Warnings}\n" +
        $"Errors: {Errors}";
}
