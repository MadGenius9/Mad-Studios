using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.ModAnalysis.Comparison;

namespace MadModStudio.AI.Tools;

public sealed record ProposedChanges(string Summary, IReadOnlyList<FileEdit> Edits);

public sealed record AIPlan(string Classification, string Reasoning, IReadOnlyList<string> Steps, IReadOnlyList<string> GameApis, IReadOnlyList<string> Risks, string? SuggestedName);

/// <summary>
/// Local context-retrieval tools exposed to the model. Each returns only the specific data requested, so the game
/// installation and project are never bulk-uploaded. Proposals are recorded, not applied: applying happens afterwards
/// through ProjectService so a revision is always created first.
/// </summary>
public sealed class ModToolbox : IAIToolExecutor
{
    private readonly ModProject? _project;
    private readonly ProjectService? _projects;
    private readonly IGameKnowledgeIndex? _index;
    private readonly GameProfile? _profile;
    private readonly List<AIToolDefinition> _tools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<AIToolResult>>> _handlers = new();

    public IReadOnlyList<ModDiagnostic> Diagnostics { get; set; } = Array.Empty<ModDiagnostic>();
    public IReadOnlyList<LogReport> Logs { get; set; } = Array.Empty<LogReport>();
    public VersionComparison? Comparison { get; set; }
    public string? DiagnosisReport { get; set; }

    public ProposedChanges? Proposal { get; private set; }
    public AIPlan? Plan { get; private set; }
    /// <summary>Human-readable record of what was sent to the provider (tool names and sizes).</summary>
    public List<string> EgressLog { get; } = new();

    public IReadOnlyList<AIToolDefinition> Tools => _tools;

    public ModToolbox(ModProject? project, ProjectService? projects, IGameKnowledgeIndex? index, GameProfile? profile, bool allowProposals, bool allowPlan)
    {
        _project = project;
        _projects = projects;
        _index = index;
        _profile = profile;

        if (index != null)
        {
            Add("search_game_api", "Search the user's INSTALLED game assemblies (Assembly-CSharp, Unity, Harmony) for types and members by name. Use before referencing any game type or method.",
                Schema(("query", "string", "Type or member name fragment, e.g. 'EntityDrone' or 'AddItem'")), SearchGameApi);
            Add("get_game_type", "Get one game type's base type and all its members (fields, properties, methods with full signatures, including private ones of Assembly-CSharp).",
                Schema(("type_name", "string", "Full or simple type name, e.g. 'EntityDrone' or 'XUiC_LootWindow'")), GetGameType);
            Add("search_game_xml", "Search the installed game's config XML entries (blocks, items, buffs, entity classes, loot, XUi windows...) by name.",
                Schema(("query", "string", "Name fragment, e.g. 'cntWoodWritableCrate'")), SearchGameXml);
            Add("read_game_xml", "Read the full XML of one named element from an installed game config file (to see the exact properties and structure before writing an XPath patch).",
                Schema(("file", "string", "Config file path relative to Data/Config, e.g. 'blocks.xml'"), ("name", "string", "Value of the element's name attribute")), ReadGameXml);
            Add("search_localization", "Search the installed game's localization keys and English text.",
                Schema(("query", "string", "Key or text fragment")), SearchLocalization);
        }
        if (project != null)
        {
            Add("list_project_files", "List all files in the mod project (paths relative to the project source folder).", Schema(), ListFiles);
            Add("read_project_file", "Read a text file from the mod project, optionally a line range.",
                Schema(("path", "string", "Relative path"), ("start_line", "integer?", "First line (1-based, optional)"), ("end_line", "integer?", "Last line (optional)")), ReadProjectFile);
            Add("get_compiler_diagnostics", "Get the current compiler errors and warnings with file and line.", Schema(), (_, _) => Task.FromResult(new AIToolResult(FormatDiagnostics())));
        }
        if (allowProposals && project != null)
        {
            Add("propose_file_changes",
                "Propose the complete new contents of files to create or change (and files to delete) in the mod project. Call it once with every file needed. Changes are applied by Mad Mod Studio after a revision snapshot; the user can restore it.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        summary = new { type = "string", description = "One paragraph explaining what changed and why, citing the game APIs/XML you verified." },
                        files = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new { path = new { type = "string" }, content = new { type = "string", description = "Entire new file content" } },
                                required = new[] { "path", "content" },
                            },
                        },
                        delete = new { type = "array", items = new { type = "string" } },
                    },
                    required = new[] { "summary", "files" },
                }), ProposeChanges);
        }
        if (allowPlan)
        {
            Add("submit_plan", "Submit the classification and implementation plan for the user's mod request. Call exactly once when your investigation is done.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        classification = new { type = "string", @enum = new[] { "XML-only", "C#/Harmony required", "Hybrid", "Needs client assets", "Unknown / requires investigation" } },
                        reasoning = new { type = "string" },
                        steps = new { type = "array", items = new { type = "string" } },
                        game_apis = new { type = "array", items = new { type = "string" }, description = "Game types/methods/XML entries you VERIFIED exist via the tools" },
                        risks = new { type = "array", items = new { type = "string" } },
                        suggested_mod_name = new { type = "string" },
                    },
                    required = new[] { "classification", "reasoning", "steps", "game_apis", "risks" },
                }), SubmitPlan);
        }
        if (project != null)
        {
            Add("get_log_problems", "Get the grouped errors/warnings parsed from attached game logs.", Schema(), (_, _) => Task.FromResult(new AIToolResult(FormatLogs())));
            Add("get_version_diff", "Get the differences between the last known working version and this version.", Schema(),
                (_, _) => Task.FromResult(Comparison is null ? AIToolResult.Error("No working version was provided.") : new AIToolResult(Comparison.Summarize(30_000))));
            Add("get_diagnosis", "Get Mad Mod Studio's static diagnosis report (log correlation, game compatibility checks).", Schema(),
                (_, _) => Task.FromResult(DiagnosisReport is null ? AIToolResult.Error("No diagnosis available.") : new AIToolResult(DiagnosisReport)));
        }
    }

    public async Task<AIToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(toolName, out var h)) return AIToolResult.Error($"Unknown tool '{toolName}'.");
        var r = await h(input, ct).ConfigureAwait(false);
        EgressLog.Add($"{toolName}: {r.Content.Length:N0} chars");
        return r;
    }

    private void Add(string name, string description, JsonElement schema, Func<JsonElement, CancellationToken, Task<AIToolResult>> handler)
    {
        _tools.Add(new AIToolDefinition(name, description, schema));
        _handlers[name] = handler;
    }

    private static JsonElement Schema(params (string Name, string Type, string Description)[] props)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var (n, t, d) in props)
        {
            var optional = t.EndsWith('?');
            properties[n] = new { type = t.TrimEnd('?'), description = d };
            if (!optional) required.Add(n);
        }
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required });
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private Task<AIToolResult> SearchGameApi(JsonElement input, CancellationToken ct)
    {
        var q = Str(input, "query");
        if (string.IsNullOrWhiteSpace(q)) return Task.FromResult(AIToolResult.Error("query is required"));
        var sb = new StringBuilder();
        sb.AppendLine($"Game: {_profile?.GameName} {_profile?.GameVersion ?? "(version unknown)"}");
        var types = _index!.SearchTypes(q, 25);
        sb.AppendLine($"TYPES ({types.Count}):");
        foreach (var t in types) sb.AppendLine($"  {t.Kind} {t.FullName} : {t.BaseType}  [{t.Assembly}]{(t.IsPublic ? "" : " (non-public)")}");
        var members = _index.SearchMembers(q, 40);
        sb.AppendLine($"MEMBERS ({members.Count}):");
        foreach (var m in members) sb.AppendLine($"  {m.DeclaringType}: {(m.IsPublic ? "public " : "")}{m.Signature}");
        if (types.Count == 0 && members.Count == 0) sb.AppendLine("No matches. Do NOT assume this API exists in the installed game.");
        return Task.FromResult(new AIToolResult(sb.ToString()));
    }

    private Task<AIToolResult> GetGameType(JsonElement input, CancellationToken ct)
    {
        var name = Str(input, "type_name");
        var t = name is null ? null : _index!.GetType(name);
        if (t is null) return Task.FromResult(AIToolResult.Error($"Type '{name}' does not exist in the installed game's indexed assemblies."));
        var sb = new StringBuilder();
        sb.AppendLine($"{t.Kind} {t.FullName} : {t.BaseType}  [{t.Assembly}]");
        var chain = new List<string>();
        var b = t.BaseType;
        for (var i = 0; i < 10 && b != null && b is not ("System.Object" or "object"); i++)
        {
            chain.Add(b);
            b = _index!.GetType(b)?.BaseType;
        }
        if (chain.Count > 0) sb.AppendLine("Inheritance: " + string.Join(" → ", chain));
        foreach (var m in _index!.GetMembers(t.FullName, limit: 1500))
            sb.AppendLine($"  {(m.IsPublic ? "public " : "private/internal ")}{m.Signature}");
        return Task.FromResult(new AIToolResult(sb.ToString()));
    }

    private Task<AIToolResult> SearchGameXml(JsonElement input, CancellationToken ct)
    {
        var q = Str(input, "query") ?? "";
        var hits = _index!.SearchXml(q, 40);
        return Task.FromResult(new AIToolResult(hits.Count == 0 ? "No matches in installed game XML."
            : string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}  {h.Path}"))));
    }

    private Task<AIToolResult> ReadGameXml(JsonElement input, CancellationToken ct)
    {
        var file = Str(input, "file");
        var name = Str(input, "name");
        if (_profile?.ConfigPath is null || file is null || name is null) return Task.FromResult(AIToolResult.Error("file and name are required, and the Game Profile must have a Config folder."));
        var path = PathSafety.ResolveUnderRoot(_profile.ConfigPath, file);
        if (path is null || !File.Exists(path)) return Task.FromResult(AIToolResult.Error($"'{file}' does not exist in the game's Config folder."));
        try
        {
            var doc = XDocument.Load(path);
            var el = doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("name") == name);
            if (el is null) return Task.FromResult(AIToolResult.Error($"No element with name='{name}' in {file}."));
            var text = el.ToString();
            return Task.FromResult(new AIToolResult(text.Length > 12_000 ? text[..12_000] + "\n…(truncated)" : text));
        }
        catch (XmlException ex) { return Task.FromResult(AIToolResult.Error($"Could not parse {file}: {ex.Message}")); }
    }

    private Task<AIToolResult> SearchLocalization(JsonElement input, CancellationToken ct)
    {
        var hits = _index!.SearchLocalization(Str(input, "query") ?? "", 40);
        return Task.FromResult(new AIToolResult(hits.Count == 0 ? "No matches." : string.Join("\n", hits.Select(h => $"{h.Key} = {h.English}  ({h.File})"))));
    }

    private Task<AIToolResult> ListFiles(JsonElement input, CancellationToken ct)
    {
        var files = _projects!.ListFiles(_project!).ToList();
        var root = string.IsNullOrEmpty(_project!.ModRootRelativePath) ? "(source root)" : _project.ModRootRelativePath;
        return Task.FromResult(new AIToolResult($"Mod folder (contains ModInfo.xml): {root}\n" + string.Join("\n", files)));
    }

    private Task<AIToolResult> ReadProjectFile(JsonElement input, CancellationToken ct)
    {
        var rel = Str(input, "path");
        if (rel is null) return Task.FromResult(AIToolResult.Error("path is required"));
        var full = PathSafety.ResolveUnderRoot(_project!.SourcePath, rel);
        if (full is null || !File.Exists(full)) return Task.FromResult(AIToolResult.Error($"File not found: {rel}"));
        if (FileUtil.LooksBinary(full)) return Task.FromResult(AIToolResult.Error($"{rel} is a binary file."));
        var lines = File.ReadAllLines(full);
        var start = Math.Max(1, Int(input, "start_line") ?? 1);
        var end = Math.Min(lines.Length, Int(input, "end_line") ?? lines.Length);
        var sb = new StringBuilder();
        for (var i = start; i <= end; i++) sb.Append(i).Append(": ").AppendLine(lines[i - 1]);
        return Task.FromResult(new AIToolResult(sb.ToString()));
    }

    private Task<AIToolResult> ProposeChanges(JsonElement input, CancellationToken ct)
    {
        var summary = Str(input, "summary") ?? "";
        var edits = new List<FileEdit>();
        var problems = new List<string>();
        if (input.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                var path = Str(f, "path");
                var content = Str(f, "content");
                if (path is null || content is null) { problems.Add("Each file needs path and content."); continue; }
                path = path.Replace('\\', '/').TrimStart('/');
                if (PathSafety.ResolveUnderRoot(_project!.SourcePath, path) is null) { problems.Add($"Unsafe path rejected: {path}"); continue; }
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { problems.Add($"Binary files cannot be written: {path}"); continue; }
                edits.Add(new FileEdit(path, content));
            }
        }
        if (input.TryGetProperty("delete", out var del) && del.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in del.EnumerateArray().Select(x => x.GetString()).Where(x => x != null))
            {
                if (PathSafety.ResolveUnderRoot(_project!.SourcePath, d!) is null) { problems.Add($"Unsafe path rejected: {d}"); continue; }
                edits.Add(new FileEdit(d!, null));
            }
        }
        if (problems.Count > 0 && edits.Count == 0) return Task.FromResult(AIToolResult.Error(string.Join("\n", problems)));
        Proposal = new ProposedChanges(summary, edits);
        return Task.FromResult(new AIToolResult($"Recorded {edits.Count} file change(s). They will be applied after a revision snapshot and then compiled and validated.{(problems.Count > 0 ? " Rejected: " + string.Join("; ", problems) : "")}"));
    }

    private Task<AIToolResult> SubmitPlan(JsonElement input, CancellationToken ct)
    {
        static List<string> Arr(JsonElement e, string n) => e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ToString()).ToList() : new List<string>();
        var cls = Str(input, "classification");
        if (cls is null) return Task.FromResult(AIToolResult.Error("classification is required"));
        Plan = new AIPlan(cls, Str(input, "reasoning") ?? "", Arr(input, "steps"), Arr(input, "game_apis"), Arr(input, "risks"), Str(input, "suggested_mod_name"));
        return Task.FromResult(new AIToolResult("Plan recorded."));
    }

    private string FormatDiagnostics()
    {
        if (Diagnostics.Count == 0) return "No compiler diagnostics.";
        return string.Join("\n", Diagnostics.Where(d => d.Severity >= Severity.Warning).Take(200).Select(d => d.ToString()));
    }

    private string FormatLogs()
    {
        if (Logs.Count == 0) return "No logs attached.";
        var sb = new StringBuilder();
        foreach (var l in Logs)
        {
            sb.AppendLine($"{Path.GetFileName(l.FilePath)} ({l.Kind}): {l.ErrorCount} errors, {l.WarningCount} warnings");
            foreach (var g in l.Groups.Take(40))
            {
                sb.AppendLine($"[{g.Category}] x{g.Count} line {g.First.LineNumber}: {g.First.Message}");
                foreach (var f in g.First.StackTrace.Take(6)) sb.AppendLine($"    at {f.Type}.{f.Method}{(f.File != null ? $" ({f.File}:{f.Line})" : "")}");
            }
        }
        return sb.ToString();
    }
}
