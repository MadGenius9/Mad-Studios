using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using MadModStudio.AI.Knowledge;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Knowledge;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;
using MadModStudio.ModAnalysis.Decompilation;

namespace MadModStudio.AI.Agents;

public sealed record ProposedChanges(string Summary, IReadOnlyList<FileEdit> Edits);

public sealed record PlannedTask(string Id, AgentKind Agent, string Title, string Instructions, IReadOnlyList<string> DependsOn);

public sealed record TaskPlan(string Objective, string Summary, IReadOnlyList<PlannedTask> Tasks);

public sealed record ReviewVerdict(string Verdict, IReadOnlyList<string> Issues, IReadOnlyList<string> EvidenceChecked, string Summary);

/// <summary>Shared, read-mostly state for the tools of one agent task.</summary>
public sealed class ToolContext
{
    public required ModProject Project { get; init; }
    public GameProfile? Profile { get; init; }
    public IGameKnowledgeIndex? Index { get; init; }
    public required ProjectService Projects { get; init; }
    public required ProjectKnowledgeService Knowledge { get; init; }
    public required AIPolicy Policy { get; init; }
    public required AgentTaskRecord Task { get; init; }
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public ModAnalysisReport? Analysis { get; set; }
    public IReadOnlyList<LogReport> Logs { get; init; } = Array.Empty<LogReport>();
    public VersionComparison? Comparison { get; init; }
    public string? DiagnosisReport { get; init; }
    public AssemblyInspector Inspector { get; init; } = new();
    public DecompilerService Decompiler { get; init; } = new();
    public IReadOnlySet<AgentKind> PlannableAgents { get; init; } = new HashSet<AgentKind>();
}

/// <summary>
/// Local tools exposed to one agent. Each returns only what is asked for. Writes are proposals (applied later through
/// the change service with revisions and file ownership); API claims are verified against the local index.
/// </summary>
public sealed class AgentToolbox : IAIToolExecutor
{
    private readonly ToolContext _c;
    private readonly List<AIToolDefinition> _tools = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<AIToolResult>>> _handlers = new();
    private readonly Dictionary<string, string> _readHashes = new(StringComparer.OrdinalIgnoreCase);

    public AgentToolbox(ToolContext context, IReadOnlySet<string> allowedTools)
    {
        _c = context;
        void Add(string name, string description, JsonElement schema, Func<JsonElement, CancellationToken, Task<AIToolResult>> handler)
        {
            if (!allowedTools.Contains(name)) return;
            _tools.Add(new AIToolDefinition(name, description, schema));
            _handlers[name] = handler;
        }

        Add(ToolNames.SearchGameApi, "Search the user's INSTALLED game assemblies for types and members by name.", Schema(("query", "string", "Type or member name fragment")), SearchGameApi);
        Add(ToolNames.GetGameType, "Get one installed game type's base types and all members with signatures (private members of Assembly-CSharp included).", Schema(("type_name", "string", "Full or simple type name")), GetGameType);
        Add(ToolNames.SearchGameXml, "Search the installed game's config XML entries (blocks, items, buffs, entity classes, loot, recipes, XUi...) by name.", Schema(("query", "string", "Name fragment")), SearchGameXml);
        Add(ToolNames.ReadGameXml, "Read the XML of one named element from an installed game config file.", Schema(("file", "string", "Path relative to Data/Config, e.g. blocks.xml"), ("name", "string", "The element's name attribute")), ReadGameXml);
        Add(ToolNames.SearchLocalization, "Search the installed game's localization keys and English text.", Schema(("query", "string", "Key or text fragment")), SearchLocalization);
        Add(ToolNames.VerifyApi, "Verify a type (and optionally a member) against the installed game's assembly metadata and store the verified result as a shared finding. Returns the real signatures, or states that it does not exist.",
            Schema(("type_name", "string", "Type name"), ("member_name", "string?", "Method/field/property name (optional)"), ("note", "string?", "Why it matters (optional)")), VerifyApi);
        Add(ToolNames.ListProjectFiles, "List all files in the mod project.", Schema(), ListFiles);
        Add(ToolNames.ReadProjectFile, "Read a text file from the mod project (optionally a line range). You must read a file before proposing changes to it.",
            Schema(("path", "string", "Relative path"), ("start_line", "integer?", "First line (optional)"), ("end_line", "integer?", "Last line (optional)")), ReadProjectFile);
        Add(ToolNames.GetModAnalysis, "Get Mad Mod Studio's static analysis of the mod (ModInfo, type, XML/DLL/source files, findings).", Schema(), GetModAnalysis);
        Add(ToolNames.GetHarmonyPatches, "List Harmony patches found in the mod's source and DLLs with their targets.", Schema(), GetHarmonyPatches);
        Add(ToolNames.InspectDll, "Inspect a DLL in the mod by metadata only (never executed): references, types, members, Harmony patches.", Schema(("path", "string", "DLL path relative to the project")), InspectDll);
        Add(ToolNames.DecompileType, "Decompile one type from a mod DLL. Output is RECONSTRUCTED source, not the original.", Schema(("path", "string", "DLL path relative to the project"), ("type_name", "string", "Full type name")), DecompileType);
        Add(ToolNames.GetCompilerDiagnostics, "Get the latest ACTUAL compiler result and diagnostics (file/line).", Schema(), (_, _) => Latest(ArtifactKind.BuildResult, "No build has run yet."));
        Add(ToolNames.GetValidationFindings, "Get the latest ACTUAL validator findings.", Schema(), (_, _) => Latest(ArtifactKind.ValidationResult, "No validation has run yet."));
        Add(ToolNames.GetLogProblems, "Get the grouped errors/warnings parsed from attached game logs.", Schema(), GetLogProblems);
        Add(ToolNames.SearchLog, "Search the raw attached log files for a text.", Schema(("query", "string", "Text to search for")), SearchLog);
        Add(ToolNames.GetVersionDiff, "Get the differences between the last known working version and this version.", Schema(),
            (_, _) => Task.FromResult(_c.Comparison is null ? AIToolResult.Error("No working version was provided.") : new AIToolResult(_c.Comparison.Summarize(30_000))));
        Add(ToolNames.GetDiagnosis, "Get Mad Mod Studio's static repair diagnosis (log correlation, regressions, game compatibility checks).", Schema(),
            (_, _) => Task.FromResult(_c.DiagnosisReport is null ? AIToolResult.Error("No diagnosis available.") : new AIToolResult(_c.DiagnosisReport)));
        Add(ToolNames.GetSideAnalysis, "Get the evidence-based server-side and EAC assessment of the mod.", Schema(), GetSideAnalysis);
        Add(ToolNames.RecordFinding, "Record a concise finding for the other agents (shared project knowledge). Include the evidence.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "Finding", "Evidence", "XmlFinding", "LogFinding", "Decision" } },
                    title = new { type = "string" },
                    content = new { type = "string", description = "Finding plus the evidence (file/line, log line, tool output)" },
                },
                required = new[] { "kind", "title", "content" },
            }), RecordFinding);
        Add(ToolNames.ListFindings, "List the shared findings recorded so far by tools and other agents.", Schema(), ListFindings);
        Add(ToolNames.ProposeFileChanges,
            "Propose complete new contents for files to create/change (and files to delete). Call once with every file. Mad Mod Studio applies it after a revision snapshot (and user approval in Guided mode).",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string", description = "What changed and why, citing the verified APIs/XML used." },
                    files = new
                    {
                        type = "array",
                        items = new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[] { "path", "content" } },
                    },
                    delete = new { type = "array", items = new { type = "string" } },
                },
                required = new[] { "summary", "files" },
            }), ProposeChanges);
        Add(ToolNames.SubmitTaskPlan, "Submit the task plan. Each task names one specialist agent; depends_on lists task ids that must finish first.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    objective = new { type = "string" },
                    summary = new { type = "string", description = "What the evidence shows so far" },
                    tasks = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                id = new { type = "string" },
                                agent = new { type = "string", @enum = context.PlannableAgents.Select(a => a.ToString()).ToArray() },
                                title = new { type = "string" },
                                instructions = new { type = "string" },
                                depends_on = new { type = "array", items = new { type = "string" } },
                            },
                            required = new[] { "id", "agent", "title", "instructions" },
                        },
                    },
                },
                required = new[] { "objective", "summary", "tasks" },
            }), SubmitPlan);
        Add(ToolNames.SubmitReview, "Submit your independent review verdict.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    verdict = new { type = "string", @enum = new[] { "approve", "concerns", "reject" } },
                    summary = new { type = "string" },
                    issues = new { type = "array", items = new { type = "string" } },
                    evidence_checked = new { type = "array", items = new { type = "string" } },
                },
                required = new[] { "verdict", "summary", "issues", "evidence_checked" },
            }), SubmitReview);
    }

    public IReadOnlyList<AIToolDefinition> Tools => _tools;
    public ProposedChanges? Proposal { get; private set; }
    public TaskPlan? Plan { get; private set; }
    public ReviewVerdict? Review { get; private set; }
    public List<KnowledgeArtifact> Recorded { get; } = new();
    public string? PlanError { get; private set; }
    /// <summary>Content hashes of files as this agent read them (for conflict detection).</summary>
    public IReadOnlyDictionary<string, string> ReadHashes => _readHashes;

    public async Task<AIToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(toolName, out var h)) return AIToolResult.Error($"Tool '{toolName}' is not available to this agent.");
        var r = await h(input, ct).ConfigureAwait(false);
        lock (_c.Task) _c.Task.RecentActions.Add($"{DateTime.Now:HH:mm:ss} {toolName} → {(r.IsError ? "error" : $"{r.Content.Length:N0} chars")}");
        return r;
    }

    // ---------------- helpers ----------------

    private static JsonElement Schema(params (string Name, string Type, string Description)[] props)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var (n, t, d) in props)
        {
            properties[n] = new { type = t.TrimEnd('?'), description = d };
            if (!t.EndsWith('?')) required.Add(n);
        }
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required });
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static List<string> Arr(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ToString()).ToList() : new();
    private static Task<AIToolResult> Ok(string s) => Task.FromResult(new AIToolResult(s));
    private static Task<AIToolResult> Err(string s) => Task.FromResult(AIToolResult.Error(s));

    private AIToolResult? IndexMissing() => _c.Index is null
        ? AIToolResult.Error("The Game Profile is not indexed, so the installed game cannot be searched. Do not assume any API exists; report this.")
        : null;

    private AIToolResult? SourceBlocked() => _c.Policy.AllowSourceToExternal ? null
        : AIToolResult.Error("Sending project source to external AI providers is disabled (Settings → AI → Privacy).");

    private AIToolResult? LogsBlocked() => _c.Policy.AllowLogsToExternal ? null
        : AIToolResult.Error("Sending logs to external AI providers is disabled (Settings → AI → Privacy).");

    private void TrackRead(string rel)
    {
        lock (_c.Task) if (!_c.Task.FilesRead.Contains(rel)) _c.Task.FilesRead.Add(rel);
    }

    // ---------------- game ----------------

    private Task<AIToolResult> SearchGameApi(JsonElement input, CancellationToken ct)
    {
        if (IndexMissing() is { } e) return Task.FromResult(e);
        var q = Str(input, "query");
        if (string.IsNullOrWhiteSpace(q)) return Err("query is required");
        var sb = new StringBuilder($"Installed game: {_c.Profile?.GameVersion ?? "(version unknown)"}\n");
        var types = _c.Index!.SearchTypes(q, 25);
        sb.AppendLine($"TYPES ({types.Count}):");
        foreach (var t in types) sb.AppendLine($"  {t.Kind} {t.FullName} : {t.BaseType}  [{t.Assembly}]{(t.IsPublic ? "" : " (non-public)")}");
        var members = _c.Index.SearchMembers(q, 40);
        sb.AppendLine($"MEMBERS ({members.Count}):");
        foreach (var m in members) sb.AppendLine($"  {m.DeclaringType}: {(m.IsPublic ? "public " : "")}{m.Signature}");
        if (types.Count == 0 && members.Count == 0) sb.AppendLine("No matches. This API does not exist in the installed game.");
        return Ok(sb.ToString());
    }

    private Task<AIToolResult> GetGameType(JsonElement input, CancellationToken ct)
    {
        if (IndexMissing() is { } e) return Task.FromResult(e);
        var name = Str(input, "type_name");
        var t = name is null ? null : _c.Index!.GetType(name);
        if (t is null) return Err($"Type '{name}' does not exist in the installed game's indexed assemblies.");
        var sb = new StringBuilder($"{t.Kind} {t.FullName} : {t.BaseType}  [{t.Assembly}]\n");
        var b = t.BaseType;
        var chain = new List<string>();
        for (var i = 0; i < 10 && b != null && b is not ("System.Object" or "object"); i++) { chain.Add(b); b = _c.Index!.GetType(b)?.BaseType; }
        if (chain.Count > 0) sb.AppendLine("Inheritance: " + string.Join(" → ", chain));
        foreach (var m in _c.Index!.GetMembers(t.FullName, limit: 1500)) sb.AppendLine($"  {(m.IsPublic ? "public " : "non-public ")}{m.Signature}");
        return Ok(sb.ToString());
    }

    private Task<AIToolResult> SearchGameXml(JsonElement input, CancellationToken ct)
    {
        if (IndexMissing() is { } e) return Task.FromResult(e);
        var hits = _c.Index!.SearchXml(Str(input, "query") ?? "", 40);
        return Ok(hits.Count == 0 ? "No matches in the installed game's XML." : string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}  {h.Path}")));
    }

    private Task<AIToolResult> ReadGameXml(JsonElement input, CancellationToken ct)
    {
        var file = Str(input, "file");
        var name = Str(input, "name");
        if (_c.Profile?.ConfigPath is null || file is null || name is null) return Err("file and name are required, and the Game Profile must have a Config folder.");
        var path = PathSafety.ResolveUnderRoot(_c.Profile.ConfigPath, file);
        if (path is null || !File.Exists(path)) return Err($"'{file}' does not exist in the installed game's Config folder.");
        try
        {
            var el = XDocument.Load(path).Descendants().FirstOrDefault(x => (string?)x.Attribute("name") == name);
            if (el is null) return Err($"No element with name='{name}' in {file}.");
            var text = el.ToString();
            return Ok(text.Length > 12_000 ? text[..12_000] + "\n…(truncated)" : text);
        }
        catch (XmlException ex) { return Err($"Could not parse {file}: {ex.Message}"); }
    }

    private Task<AIToolResult> SearchLocalization(JsonElement input, CancellationToken ct)
    {
        if (IndexMissing() is { } e) return Task.FromResult(e);
        var hits = _c.Index!.SearchLocalization(Str(input, "query") ?? "", 40);
        return Ok(hits.Count == 0 ? "No matches." : string.Join("\n", hits.Select(h => $"{h.Key} = {h.English}  ({h.File})")));
    }

    private async Task<AIToolResult> VerifyApi(JsonElement input, CancellationToken ct)
    {
        var type = Str(input, "type_name");
        if (string.IsNullOrWhiteSpace(type)) return AIToolResult.Error("type_name is required");
        var a = await _c.Knowledge.RecordApiClaimAsync(_c.Project, _c.Profile, type!, Str(input, "member_name"), Str(input, "note"),
            _c.Task.Agent.ToString(), _c.Provider, _c.Model, _c.Task.Id, _c.Task.RunId, ct).ConfigureAwait(false);
        Recorded.Add(a);
        return a.Kind == ArtifactKind.NegativeApiFinding
            ? AIToolResult.Error($"VERIFIED: does NOT exist. {a.Content}")
            : new AIToolResult($"{a.Title}\n{a.Content}");
    }

    // ---------------- project ----------------

    private Task<AIToolResult> ListFiles(JsonElement input, CancellationToken ct)
    {
        var files = _c.Projects.ListFiles(_c.Project).ToList();
        var root = string.IsNullOrEmpty(_c.Project.ModRootRelativePath) ? "(source root)" : _c.Project.ModRootRelativePath;
        return Ok($"Mod folder (contains ModInfo.xml): {root}\n" + string.Join("\n", files));
    }

    private async Task<AIToolResult> ReadProjectFile(JsonElement input, CancellationToken ct)
    {
        if (SourceBlocked() is { } b) return b;
        var rel = Str(input, "path")?.Replace('\\', '/').TrimStart('/');
        if (rel is null) return AIToolResult.Error("path is required");
        var full = PathSafety.ResolveUnderRoot(_c.Project.SourcePath, rel);
        if (full is null || !File.Exists(full)) return AIToolResult.Error($"File not found: {rel}");
        if (FileUtil.LooksBinary(full)) return AIToolResult.Error($"{rel} is binary; use inspect_dll for DLLs.");
        var text = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        lock (_readHashes) _readHashes[rel] = FileOwnershipManager.Hash(text);
        TrackRead(rel);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var start = Math.Max(1, Int(input, "start_line") ?? 1);
        var end = Math.Min(lines.Length, Int(input, "end_line") ?? lines.Length);
        var sb = new StringBuilder();
        for (var i = start; i <= end; i++) sb.Append(i).Append(": ").AppendLine(lines[i - 1]);
        return new AIToolResult(sb.ToString());
    }

    private Task<AIToolResult> GetModAnalysis(JsonElement input, CancellationToken ct)
    {
        var a = _c.Analysis;
        if (a is null) return Err("No analysis available.");
        var sb = new StringBuilder(a.Summary()).AppendLine();
        if (a.ModInfo is { } mi) sb.AppendLine($"ModInfo ({mi.Format}): Name={mi.Name} DisplayName={mi.DisplayName} Version={mi.Version}");
        sb.AppendLine("XML: " + string.Join(", ", a.XmlFiles.Select(x => x.RelativePath + (x.IsWellFormed ? "" : " (MALFORMED)"))));
        sb.AppendLine("DLLs: " + string.Join(", ", a.Dlls.Select(d => $"{d.RelativePath} ({d.Assembly.Name} {d.Assembly.Version})")));
        sb.AppendLine("Source: " + string.Join(", ", a.SourceFiles));
        foreach (var f in a.Findings) sb.AppendLine($"{f.Severity}: {f.Message} {f.FilePath}");
        return Ok(sb.ToString());
    }

    private Task<AIToolResult> GetHarmonyPatches(JsonElement input, CancellationToken ct)
    {
        var a = _c.Analysis;
        if (a is null || a.HarmonyPatches.Count == 0) return Ok("No Harmony patches detected.");
        return Ok(string.Join("\n", a.HarmonyPatches.Select(h => $"[{h.Origin}] {h.PatchKind} {h.TargetDisplay}  ({h.PatchClass}) {h.File}:{h.Line}")));
    }

    private Task<AIToolResult> InspectDll(JsonElement input, CancellationToken ct)
    {
        var rel = Str(input, "path");
        var full = rel is null ? null : PathSafety.ResolveUnderRoot(_c.Project.SourcePath, rel);
        if (full is null || !File.Exists(full)) return Err($"DLL not found: {rel}");
        var r = _c.Inspector.Inspect(full);
        if (!r.Success) return Err(r.ReadError ?? "Unreadable assembly.");
        var sb = new StringBuilder($"{r.Name} {r.Version} TFM={r.TargetFramework ?? "(none)"} (metadata only — not executed)\n");
        sb.AppendLine("References: " + string.Join(", ", r.References));
        foreach (var t in r.Types.Take(80))
        {
            sb.AppendLine($"  {t.Kind} {t.FullName} : {t.BaseType}");
            foreach (var m in t.Members.Take(40)) sb.AppendLine($"      {m.Signature}");
        }
        foreach (var h in r.HarmonyPatches) sb.AppendLine($"Harmony: {h.PatchKind} {h.TargetDisplay} ({h.PatchClass})");
        return Ok(sb.ToString());
    }

    private Task<AIToolResult> DecompileType(JsonElement input, CancellationToken ct)
    {
        var rel = Str(input, "path");
        var type = Str(input, "type_name");
        var full = rel is null ? null : PathSafety.ResolveUnderRoot(_c.Project.SourcePath, rel);
        if (full is null || !File.Exists(full) || type is null) return Err("path (existing DLL) and type_name are required.");
        var d = _c.Decompiler.DecompileType(full, type, _c.Profile?.ManagedPath is { } m ? new[] { m } : null);
        return d.Success ? Ok(d.Text) : Err(d.Error ?? "Decompilation failed.");
    }

    private async Task<AIToolResult> Latest(ArtifactKind kind, string none)
    {
        var a = (await _c.Knowledge.ListAsync(_c.Project.Id, kind, 1).ConfigureAwait(false)).FirstOrDefault();
        return new AIToolResult(a is null ? none : $"{a.Title}\n{a.Content}");
    }

    private Task<AIToolResult> GetLogProblems(JsonElement input, CancellationToken ct)
    {
        if (LogsBlocked() is { } b) return Task.FromResult(b);
        if (_c.Logs.Count == 0) return Ok("No logs attached.");
        var sb = new StringBuilder();
        foreach (var l in _c.Logs)
        {
            sb.AppendLine($"{Path.GetFileName(l.FilePath)} ({l.Kind}): {l.ErrorCount} errors, {l.WarningCount} warnings");
            foreach (var g in l.Groups.Take(40))
            {
                sb.AppendLine($"[{g.Category}] x{g.Count} line {g.First.LineNumber}: {g.First.Message}");
                foreach (var f in g.First.StackTrace.Take(6)) sb.AppendLine($"    at {f.Type}.{f.Method}{(f.File != null ? $" ({f.File}:{f.Line})" : "")}");
            }
        }
        return Ok(sb.ToString());
    }

    private Task<AIToolResult> SearchLog(JsonElement input, CancellationToken ct)
    {
        if (LogsBlocked() is { } b) return Task.FromResult(b);
        var q = Str(input, "query");
        if (string.IsNullOrWhiteSpace(q) || _c.Logs.Count == 0) return Err("A query and at least one attached log are required.");
        var sb = new StringBuilder();
        foreach (var l in _c.Logs)
            foreach (var (line, text) in LogParser.Search(l.FilePath, q!, 40, ct))
                sb.AppendLine($"{Path.GetFileName(l.FilePath)}:{line}: {text}");
        return Ok(sb.Length == 0 ? "No matches." : sb.ToString());
    }

    private Task<AIToolResult> GetSideAnalysis(JsonElement input, CancellationToken ct)
    {
        var a = _c.Analysis;
        if (a is null) return Err("No analysis available.");
        return Ok($"Server-side: {a.Side.Result}\n{string.Join("\n", a.Side.Reasons)}\nEAC: {a.Eac.Result}\n{string.Join("\n", a.Eac.Reasons)}\nProject Server-Side Only setting: {_c.Project.ServerSideOnly}");
    }

    // ---------------- knowledge ----------------

    private async Task<AIToolResult> RecordFinding(JsonElement input, CancellationToken ct)
    {
        var title = Str(input, "title");
        var content = Str(input, "content");
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content)) return AIToolResult.Error("title and content are required");
        var kind = Enum.TryParse<ArtifactKind>(Str(input, "kind"), out var k) && k is ArtifactKind.Finding or ArtifactKind.Evidence or ArtifactKind.XmlFinding or ArtifactKind.LogFinding or ArtifactKind.Decision ? k : ArtifactKind.Finding;
        var a = await _c.Knowledge.AddAsync(new KnowledgeArtifact
        {
            ProjectId = _c.Project.Id, Kind = kind, Title = title!, Content = content!,
            Agent = _c.Task.Agent.ToString(), Provider = _c.Provider, Model = _c.Model, TaskId = _c.Task.Id, RunId = _c.Task.RunId,
            GameProfileId = _c.Profile?.Id, GameFingerprint = _c.Profile?.AssemblyFingerprint,
        }, ct).ConfigureAwait(false);
        Recorded.Add(a);
        return new AIToolResult("Recorded.");
    }

    private async Task<AIToolResult> ListFindings(JsonElement input, CancellationToken ct)
    {
        var items = await _c.Knowledge.ListAsync(_c.Project.Id, null, 60, ct).ConfigureAwait(false);
        if (items.Count == 0) return new AIToolResult("No findings yet.");
        return new AIToolResult(string.Join("\n", items.Select(a => $"[{a.Kind}{(a.Verified ? ", verified" : "")}{(a.Stale ? ", STALE" : "")}] {a.Title} — {a.Agent ?? "tool"}{(a.Model != null ? " / " + a.Model : "")}\n    {ProviderLine(a.Content)}")));
    }

    private static string ProviderLine(string s) => s.Length > 300 ? s[..300].Replace("\n", " ") + "…" : s.Replace("\n", " ");

    // ---------------- outputs ----------------

    private Task<AIToolResult> ProposeChanges(JsonElement input, CancellationToken ct)
    {
        var summary = Str(input, "summary") ?? "";
        var edits = new List<FileEdit>();
        var problems = new List<string>();
        if (input.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in files.EnumerateArray())
            {
                var path = Str(f, "path")?.Replace('\\', '/').TrimStart('/');
                var content = Str(f, "content");
                if (path is null || content is null) { problems.Add("Each file needs path and content."); continue; }
                if (PathSafety.ResolveUnderRoot(_c.Project.SourcePath, path) is null) { problems.Add($"Unsafe path rejected: {path}"); continue; }
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { problems.Add($"Binary files cannot be written: {path}"); continue; }
                if (_c.Task.Agent == Core.Knowledge.AgentKind.Documentation && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                { problems.Add($"The documentation agent may only write .md/.txt files: {path}"); continue; }
                edits.Add(new FileEdit(path, content));
            }
        }
        foreach (var d in Arr(input, "delete"))
        {
            if (PathSafety.ResolveUnderRoot(_c.Project.SourcePath, d) is null) { problems.Add($"Unsafe path rejected: {d}"); continue; }
            edits.Add(new FileEdit(d.Replace('\\', '/').TrimStart('/'), null));
        }
        // Agents must read existing files before overwriting them (prevents blind overwrites of other agents' work).
        foreach (var e in edits.Where(e => File.Exists(Path.Combine(_c.Project.SourcePath, e.RelativePath))).ToList())
        {
            if (!_readHashes.ContainsKey(e.RelativePath))
            {
                problems.Add($"Read {e.RelativePath} with read_project_file before changing it.");
                edits.Remove(e);
            }
        }
        if (edits.Count == 0) return Err(problems.Count > 0 ? string.Join("\n", problems) : "No files were proposed.");
        Proposal = new ProposedChanges(summary, edits);
        return Ok($"Recorded {edits.Count} file change(s); Mad Mod Studio will apply them after a revision snapshot, then compile and validate.{(problems.Count > 0 ? " Rejected: " + string.Join("; ", problems) : "")}");
    }

    private Task<AIToolResult> SubmitPlan(JsonElement input, CancellationToken ct)
    {
        var tasks = new List<PlannedTask>();
        var problems = new List<string>();
        if (input.TryGetProperty("tasks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var id = Str(t, "id");
                var agentName = Str(t, "agent");
                if (id is null || !Enum.TryParse<AgentKind>(agentName, out var agent) || !_c.PlannableAgents.Contains(agent))
                { problems.Add($"Task '{id}' has an unknown or unavailable agent '{agentName}'."); continue; }
                tasks.Add(new PlannedTask(id, agent, Str(t, "title") ?? agent.ToString(), Str(t, "instructions") ?? "", Arr(t, "depends_on")));
            }
        }
        var ids = tasks.Select(t => t.Id).ToHashSet();
        foreach (var t in tasks) foreach (var d in t.DependsOn.Where(d => !ids.Contains(d))) problems.Add($"Task '{t.Id}' depends on unknown task '{d}'.");
        if (tasks.Select(t => t.Id).Distinct().Count() != tasks.Count) problems.Add("Task ids must be unique.");
        if (problems.Count == 0 && TaskGraph.HasCycle(tasks)) problems.Add("The dependencies contain a cycle.");
        if (tasks.Count == 0) problems.Add("The plan contains no tasks.");
        if (problems.Count > 0)
        {
            PlanError = string.Join(" ", problems);
            return Err("Plan rejected: " + PlanError + " Fix it and call submit_task_plan again.");
        }
        Plan = new TaskPlan(Str(input, "objective") ?? "", Str(input, "summary") ?? "", tasks);
        PlanError = null;
        return Ok($"Plan accepted with {tasks.Count} task(s).");
    }

    private Task<AIToolResult> SubmitReview(JsonElement input, CancellationToken ct)
    {
        var verdict = Str(input, "verdict");
        if (verdict is not ("approve" or "concerns" or "reject")) return Err("verdict must be approve, concerns or reject");
        Review = new ReviewVerdict(verdict, Arr(input, "issues"), Arr(input, "evidence_checked"), Str(input, "summary") ?? "");
        return Ok("Review recorded.");
    }
}

public static class TaskGraph
{
    public static bool HasCycle(IReadOnlyList<PlannedTask> tasks)
    {
        var map = tasks.ToDictionary(t => t.Id);
        var state = new Dictionary<string, int>();
        bool Visit(string id)
        {
            if (state.TryGetValue(id, out var s)) return s == 1;
            state[id] = 1;
            foreach (var d in map[id].DependsOn) if (map.ContainsKey(d) && Visit(d)) return true;
            state[id] = 2;
            return false;
        }
        return tasks.Any(t => Visit(t.Id));
    }
}
