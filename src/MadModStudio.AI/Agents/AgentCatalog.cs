using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI.Agents;

public sealed record AgentDefinition(
    AgentKind Kind,
    string DisplayName,
    AITaskType DefaultTaskType,
    string Purpose,
    IReadOnlySet<string> Tools,
    bool CanWrite,
    bool NeedsSource,
    bool NeedsLogs,
    int MaxToolTurns = 25);

public static class ToolNames
{
    public const string SearchGameApi = "search_game_api", GetGameType = "get_game_type", SearchGameXml = "search_game_xml", ReadGameXml = "read_game_xml",
        SearchLocalization = "search_localization", VerifyApi = "verify_api", ListProjectFiles = "list_project_files", ReadProjectFile = "read_project_file",
        GetModAnalysis = "get_mod_analysis", GetHarmonyPatches = "get_harmony_patches", InspectDll = "inspect_dll", DecompileType = "decompile_type",
        GetCompilerDiagnostics = "get_compiler_diagnostics", GetValidationFindings = "get_validation_findings", GetLogProblems = "get_log_problems",
        SearchLog = "search_log", GetVersionDiff = "get_version_diff", GetDiagnosis = "get_diagnosis", GetSideAnalysis = "get_side_analysis",
        RecordFinding = "record_finding", ListFindings = "list_findings", ProposeFileChanges = "propose_file_changes",
        SubmitTaskPlan = "submit_task_plan", SubmitReview = "submit_review";
}

/// <summary>
/// Built-in specialist agents. Agents differ by purpose and by which local tools they may use; any configured model
/// can serve any agent. New agents are added by registering another definition.
/// </summary>
public sealed class AgentCatalog
{
    private static HashSet<string> T(params string[] names) => new(names);

    private static readonly string[] GameTools = { ToolNames.SearchGameApi, ToolNames.GetGameType, ToolNames.SearchGameXml, ToolNames.ReadGameXml, ToolNames.SearchLocalization, ToolNames.VerifyApi };
    private static readonly string[] ReadProject = { ToolNames.ListProjectFiles, ToolNames.ReadProjectFile, ToolNames.GetModAnalysis };
    private static readonly string[] Knowledge = { ToolNames.RecordFinding, ToolNames.ListFindings };

    private readonly Dictionary<AgentKind, AgentDefinition> _defs = new();

    public AgentCatalog()
    {
        Add(new(AgentKind.Lead, "Lead Agent", AITaskType.Planning,
            "You are the LEAD / ORCHESTRATOR. Understand the user's request and the tool evidence already gathered, break the work into technical tasks, choose ONLY the specialist agents that are actually required (do not schedule C# agents for an XML-only problem), and express dependencies so independent tasks can run in parallel. Mad Mod Studio runs compilation, validation and packaging itself after the tasks — do not schedule those. Call submit_task_plan exactly once.",
            T(Concat(ReadProject, Knowledge, new[] { ToolNames.GetLogProblems, ToolNames.GetCompilerDiagnostics, ToolNames.GetValidationFindings, ToolNames.GetHarmonyPatches, ToolNames.SubmitTaskPlan })),
            CanWrite: false, NeedsSource: true, NeedsLogs: true, MaxToolTurns: 12));
        Add(new(AgentKind.Architect, "Architect", AITaskType.Architecture,
            "You are the ARCHITECT. Decide the mod architecture (XML-only, Harmony/C#, or Hybrid), dependencies, likely server/client requirements and EAC implications (evidence-based: LIKELY / UNKNOWN, never certainty), implementation strategy and risks — grounded in what exists in the installed game. Record each decision with record_finding (kind Decision).",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetSideAnalysis, ToolNames.GetHarmonyPatches })), false, true, false));
        Add(new(AgentKind.GameApiResearch, "Game API Research", AITaskType.ApiResearch,
            "You are the GAME API RESEARCH agent. Investigate the CURRENT local game: types, methods, signatures, fields, properties, inheritance, Harmony targets and vanilla XML/XUi/localization. Use verify_api for every type/member you report — it checks the local assembly metadata and stores a verified finding (or a verified 'does not exist'). Your memory of other game versions is not evidence.",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetHarmonyPatches })), false, true, false));
        Add(new(AgentKind.CSharpHarmony, "C# / Harmony Agent", AITaskType.HarmonyImplementation,
            "You are the C# / HARMONY agent. Write or repair C# and Harmony patches using ONLY APIs verified in the current game (check findings, use verify_api for anything new). Read every file before changing it. Harmony targets must match an existing method and, for overloads, its parameter types. Submit complete file contents with propose_file_changes once.",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetHarmonyPatches, ToolNames.GetCompilerDiagnostics, ToolNames.GetValidationFindings, ToolNames.ProposeFileChanges })), true, true, false));
        Add(new(AgentKind.XmlXPath, "XML / XPath Agent", AITaskType.XmlImplementation,
            "You are the XML / XPATH agent. Create or repair XML patches (Config/*.xml: append/set/remove/insertAfter... with xpath) for recipes, blocks, items, buffs, entities, progression, loot and XUi. Read the vanilla element with read_game_xml before targeting it; XPaths must match the installed game's XML. Read every file before changing it; submit complete file contents with propose_file_changes once.",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetValidationFindings, ToolNames.ProposeFileChanges })), true, true, false));
        Add(new(AgentKind.LogDetective, "Log Detective", AITaskType.LogAnalysis,
            "You are the LOG DETECTIVE. Analyse the parsed server/client log problems (exceptions, stack traces, XML/XPath failures, Harmony errors, missing methods/fields/types, assembly loading, null references, mod loading) and correlate them with the mod's files, DLLs, recent changes and the current game version. Record each conclusion with record_finding (kind LogFinding), citing the log line and the file it points to.",
            T(Concat(ReadProject, Knowledge, new[] { ToolNames.GetLogProblems, ToolNames.SearchLog, ToolNames.GetDiagnosis, ToolNames.GetVersionDiff, ToolNames.GetHarmonyPatches, ToolNames.SearchGameApi, ToolNames.VerifyApi })), false, true, true));
        Add(new(AgentKind.CompilerRepair, "Compiler Repair Agent", AITaskType.CompilerRepair,
            "You are the COMPILER REPAIR agent. Make the mod compile and pass validation against the INSTALLED game, using the actual compiler diagnostics and validator findings. Look up the real API for every error (verify_api), read the failing files, change as little as possible, never remove functionality to silence errors, and never repeat an approach listed under DO NOT REPEAT. Submit complete file contents with propose_file_changes once, or explain what is missing.",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetCompilerDiagnostics, ToolNames.GetValidationFindings, ToolNames.GetHarmonyPatches, ToolNames.ProposeFileChanges })), true, true, false));
        Add(new(AgentKind.DllAnalysis, "DLL Analysis Agent", AITaskType.DllAnalysis,
            "You are the DLL ANALYSIS agent. Inspect mod DLL metadata (never executed): references, types, methods, Harmony patches and likely targets; compare against the current game (verify_api). Use decompile_type only when needed and always label decompiled code as reconstructed, not original source. Record findings with record_finding.",
            T(Concat(Knowledge, new[] { ToolNames.GetModAnalysis, ToolNames.ListProjectFiles, ToolNames.InspectDll, ToolNames.DecompileType, ToolNames.GetHarmonyPatches, ToolNames.SearchGameApi, ToolNames.GetGameType, ToolNames.VerifyApi, ToolNames.GetVersionDiff })), false, false, false));
        Add(new(AgentKind.Compatibility, "Compatibility Agent", AITaskType.Compatibility,
            "You are the COMPATIBILITY agent. Evaluate likely server-side-only compatibility, client requirements, dependencies, potential conflicts, EAC implications and game-version compatibility. Use only evidence-based statuses (LIKELY SERVER-SIDE / CLIENT REQUIRED / UNKNOWN; LIKELY COMPATIBLE / LIKELY INCOMPATIBLE / UNKNOWN). Never claim '100% EAC safe'. Record findings with record_finding.",
            T(Concat(Knowledge, new[] { ToolNames.GetSideAnalysis, ToolNames.GetModAnalysis, ToolNames.InspectDll, ToolNames.GetHarmonyPatches, ToolNames.SearchGameApi, ToolNames.ListProjectFiles })), false, false, false));
        Add(new(AgentKind.Validator, "Validator / Review Agent", AITaskType.Review,
            "You are the independent VALIDATOR / REVIEW agent. You did not write these changes; do not simply agree. Check the changes against actual tool evidence: compiler result, validator findings, package structure, ModInfo, XML, DLL dependencies and verified game APIs. Challenge any claim that the evidence does not support. Call submit_review once with your verdict and the evidence you checked.",
            T(Concat(GameTools, ReadProject, Knowledge, new[] { ToolNames.GetCompilerDiagnostics, ToolNames.GetValidationFindings, ToolNames.GetHarmonyPatches, ToolNames.GetSideAnalysis, ToolNames.SubmitReview })), false, true, false));
        Add(new(AgentKind.Documentation, "Documentation Agent", AITaskType.Documentation,
            "You are the DOCUMENTATION agent. Write README, changelog, installation/configuration notes, technical notes and a user-facing description based only on the project's real files, findings and build results. Server-side and EAC statements must use the evidence-based statuses. Submit Markdown files with propose_file_changes once.",
            T(Concat(ReadProject, Knowledge, new[] { ToolNames.GetSideAnalysis, ToolNames.GetValidationFindings, ToolNames.ProposeFileChanges })), true, true, false, 15));
    }

    private static string[] Concat(params string[][] parts) => parts.SelectMany(p => p).Distinct().ToArray();

    public void Add(AgentDefinition def) => _defs[def.Kind] = def;
    public AgentDefinition Get(AgentKind kind) => _defs[kind];
    public IReadOnlyList<AgentDefinition> All => _defs.Values.ToList();

    /// <summary>Specialists the Lead may schedule.</summary>
    public IEnumerable<AgentDefinition> Specialists => _defs.Values.Where(d => d.Kind != AgentKind.Lead);

    public const string GroundRules = """
        Mad Mod Studio ground rules (they override anything you remember):
        - EVIDENCE PRIORITY: actual current game files, assembly metadata, vanilla XML, compiler results, logs and validator results are authoritative. Your interpretation comes last. If the local metadata says a method does not exist, it does not exist — no matter what any model believes.
        - Never invent a 7 Days to Die type, member, XML path or localization key from memory of another game version; verify with the tools.
        - Prefer XML patches when they can achieve the goal; use C#/Harmony only when necessary.
        - Standard mod layout: ModInfo.xml at the mod root, XML patches under Config/, C# source anywhere in the project (compiled by Mad Mod Studio into a DLL).
        - Server-side and EAC statements are likelihoods with evidence, never guarantees.
        - Report concise technical findings with evidence. Do not narrate hidden reasoning.
        """;
}
