using MadModStudio.Core.Models;

namespace MadModStudio.Game7DTD.Mods;

/// <summary>
/// Heuristic, evidence-based assessment of whether a mod needs to be installed on clients and how it relates to EAC.
/// Static analysis cannot prove runtime behaviour, so results are always phrased as likelihoods with reasons.
/// </summary>
public static class SideRequirementAnalyzer
{
    private static readonly string[] ClientOnlyTypeHints =
    {
        "UnityEngine.UI.", "UnityEngine.GUI", "UnityEngine.Camera", "UnityEngine.Input", "UnityEngine.AudioSource", "UnityEngine.Shader",
        "UnityEngine.Material", "UnityEngine.Renderer", "UnityEngine.RenderTexture", "XUi", "XUiC_", "XUiV_", "LocalPlayerUI", "NGUI", "UILabel", "UISprite",
        "PlayerMoveController", "vp_FPCamera",
    };

    public static SideAnalysis Analyze(ModAnalysisReport report)
    {
        var reasons = new List<string>();
        var client = false; var unknown = false; var warning = false;

        if (report.AssetFiles.Count > 0)
        {
            client = true;
            reasons.Add($"CLIENT REQUIRED evidence: {report.AssetFiles.Count} asset file(s) (e.g. {report.AssetFiles[0]}). Asset bundles, icons and other resources are not transmitted by the server and must be installed on each client.");
        }
        if (report.XuiFiles.Any(x => x.Contains("XUi_Menu", StringComparison.OrdinalIgnoreCase)))
        {
            client = true;
            reasons.Add("CLIENT REQUIRED evidence: XUi_Menu changes affect the local main menu, which is loaded before connecting to a server.");
        }
        else if (report.XuiFiles.Count > 0)
        {
            warning = true;
            reasons.Add($"WARNING: {report.XuiFiles.Count} XUi file(s). XML configuration is normally sent to clients by the server, but UI changes that reference client assets or code still require client installation.");
        }

        var dlls = report.Dlls.Where(d => d.Assembly.Success).ToList();
        if (dlls.Count > 0)
        {
            var hits = dlls.SelectMany(d => d.Assembly.ExternalTypes)
                .Where(t => ClientOnlyTypeHints.Any(h => t.FullName.StartsWith(h, StringComparison.Ordinal) || t.FullName.Contains("." + h, StringComparison.Ordinal) || t.FullName.StartsWith(h)))
                .Select(t => t.FullName).Distinct().Take(8).ToList();
            var uiPatches = report.HarmonyPatches.Where(p => p.TargetType != null && ClientOnlyTypeHints.Any(h => p.TargetType.Contains(h))).Select(p => p.TargetDisplay).Distinct().Take(5).ToList();
            if (hits.Count > 0 || uiPatches.Count > 0)
            {
                client = true;
                if (hits.Count > 0) reasons.Add($"CLIENT REQUIRED evidence: DLL code references client/UI/rendering types: {string.Join(", ", hits)}.");
                if (uiPatches.Count > 0) reasons.Add($"CLIENT REQUIRED evidence: Harmony patches target UI types: {string.Join(", ", uiPatches)}.");
            }
            else
            {
                unknown = true;
                reasons.Add($"UNKNOWN: {dlls.Count} DLL(s) contain custom code. Code only runs where the DLL is installed; whether gameplay works with a server-only install depends on which side executes the patched logic, which static analysis cannot prove.");
            }
        }
        else if (report.SourceFiles.Count > 0)
        {
            unknown = true;
            reasons.Add("UNKNOWN: C# source present (not yet compiled); side requirements will be re-evaluated after a build.");
        }

        if (report.ConfigXmlCount > 0 && !client && dlls.Count == 0 && report.SourceFiles.Count == 0)
            reasons.Add($"LIKELY SERVER-SIDE evidence: {report.ConfigXmlCount} config XML patch file(s) and no code or assets. The game sends XML configuration from the server to connecting clients.");

        var result = client ? SideRequirement.ClientRequired
            : unknown ? SideRequirement.Unknown
            : warning ? SideRequirement.Warning
            : report.ConfigXmlCount > 0 ? SideRequirement.LikelyServerSide
            : SideRequirement.Unknown;
        if (reasons.Count == 0) reasons.Add("UNKNOWN: no config XML, code or assets were found to assess.");
        return new SideAnalysis { Result = result, Reasons = reasons };
    }

    public static EacAnalysis AnalyzeEac(ModAnalysisReport report)
    {
        var reasons = new List<string>();
        var dllCount = report.Dlls.Count(d => d.Assembly.Success);
        var willHaveCode = dllCount > 0 || report.SourceFiles.Count > 0;
        if (!willHaveCode)
        {
            reasons.Add("No managed DLLs or C# source: the mod does not load custom code, which is what EasyAntiCheat blocks.");
            reasons.Add("This is a static assessment, not a guarantee.");
            return new EacAnalysis { Result = EacCompatibility.LikelyCompatible, Reasons = reasons };
        }
        reasons.Add(dllCount > 0 ? $"{dllCount} custom managed DLL(s) present." : "C# source present; it will be compiled into a custom DLL.");
        if (report.Side.Result == SideRequirement.ClientRequired)
        {
            reasons.Add("Code appears to be required on clients; clients generally must run with EAC disabled to load custom DLL mods.");
            return new EacAnalysis { Result = EacCompatibility.LikelyIncompatible, Reasons = reasons };
        }
        reasons.Add("If the DLL is only installed on a dedicated server, clients do not load it; whether that works depends on the mod's behaviour, which cannot be proven statically.");
        return new EacAnalysis { Result = EacCompatibility.Unknown, Reasons = reasons };
    }
}
