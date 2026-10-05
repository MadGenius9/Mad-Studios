using System.Text;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;

namespace MadModStudio.Game7DTD.Repair;

public sealed record Correlation(LogGroup Group, string ProjectFile, int? Line, string Reason);

public sealed class RepairInputs
{
    public List<(string Path, string Kind)> Logs { get; } = new();
    /// <summary>Folder or ZIP of the last known working version.</summary>
    public string? WorkingVersionPath { get; set; }
    public List<string> DependencyMods { get; } = new();
}

public sealed class Diagnosis
{
    public ModAnalysisReport? Analysis { get; set; }
    public List<LogReport> Logs { get; } = new();
    /// <summary>Log groups that relate to this mod (by correlation or mod name).</summary>
    public List<LogGroup> RelevantGroups { get; } = new();
    public List<Correlation> Correlations { get; } = new();
    public VersionComparison? Comparison { get; set; }
    public List<ValidationFinding> GameCompatibilityFindings { get; } = new();
    public List<string> LikelyCauses { get; } = new();
    public List<string> ProposedRepairs { get; } = new();
    public string Report { get; set; } = "";
}

/// <summary>
/// Repair Mode diagnosis: correlates log errors, version differences and static checks against the installed game.
/// It proposes actions as text; edits are applied by the user or the AI layer through ProjectService (with revisions).
/// </summary>
public sealed class RepairService
{
    private readonly ModAnalyzer _analyzer;
    private readonly LogParser _logParser;
    private readonly VersionComparer _comparer;
    private readonly GameProfileService _profiles;
    private readonly AssemblyInspector _inspector;
    private readonly Core.AppPaths _paths;

    public RepairService(ModAnalyzer analyzer, LogParser logParser, VersionComparer comparer, GameProfileService profiles, AssemblyInspector inspector, Core.AppPaths paths)
    {
        _analyzer = analyzer;
        _logParser = logParser;
        _comparer = comparer;
        _profiles = profiles;
        _inspector = inspector;
        _paths = paths;
    }

    public async Task<Diagnosis> DiagnoseAsync(ModProject project, RepairInputs inputs, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var d = new Diagnosis();
        var profile = project.GameProfileId is { } id ? await _profiles.GetAsync(id, ct).ConfigureAwait(false) : null;
        var index = _profiles.GetIndex(profile);

        progress?.Report("Analyzing mod...");
        d.Analysis = await Task.Run(() => _analyzer.Analyze(project.ModRootPath, project.SourcePath, index, profile), ct).ConfigureAwait(false);

        foreach (var (path, kind) in inputs.Logs)
        {
            progress?.Report($"Analyzing log {Path.GetFileName(path)}...");
            try { d.Logs.Add(await Task.Run(() => _logParser.Parse(path, kind, ct), ct).ConfigureAwait(false)); }
            catch (IOException ex) { d.LikelyCauses.Add($"Log {Path.GetFileName(path)} could not be read: {ex.Message}"); }
        }

        progress?.Report("Correlating errors with project files...");
        Correlate(project, d);

        if (inputs.WorkingVersionPath != null)
        {
            progress?.Report("Comparing with working version...");
            var working = PrepareComparisonRoot(inputs.WorkingVersionPath);
            if (working != null) d.Comparison = await Task.Run(() => _comparer.Compare(working, project.ModRootPath), ct).ConfigureAwait(false);
            else d.LikelyCauses.Add("The working version could not be read (no ModInfo.xml found).");
        }

        progress?.Report("Checking against current Game Profile...");
        if (index != null)
        {
            var ctx = new ValidationContext { ModRootPath = project.ModRootPath, Project = project, GameProfile = profile, GameIndex = index };
            ctx.Items[HarmonyTargetValidator.SourcePatchesKey] = d.Analysis.HarmonyPatches.Where(p => p.Origin == "Source").ToList();
            var checks = new IModValidator[] { new HarmonyTargetValidator(_inspector), new GameApiReferenceValidator(_inspector), new XmlPatchTargetValidator(), new XmlWellFormedValidator() };
            var vr = await new ValidationRunner(checks).RunAsync(ctx, ct).ConfigureAwait(false);
            d.GameCompatibilityFindings.AddRange(vr.Findings.Where(f => f.Severity >= Severity.Warning));
        }
        else d.LikelyCauses.Add("No indexed Game Profile: the mod could not be compared against the installed game's API and XML. Assign and index a Game Profile for a full diagnosis.");

        BuildConclusions(project, d, index);
        d.Report = Render(project, d);
        return d;
    }

    private string? PrepareComparisonRoot(string path)
    {
        var root = path;
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.Combine(_paths.Scratch, "compare-" + Guid.NewGuid().ToString("N"));
            SafeZip.Extract(path, root);
        }
        if (!Directory.Exists(root)) return null;
        var mi = ModInfoFile.FindAll(root).FirstOrDefault();
        return mi is null ? null : Path.GetDirectoryName(mi);
    }

    private static void Correlate(ModProject project, Diagnosis d)
    {
        var files = FileUtil.EnumerateRelativeFiles(project.SourcePath, FileExclusionRules.ForSnapshots()).ToList();
        var textFiles = files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
        var contents = new Dictionary<string, string[]>();
        foreach (var f in textFiles)
        {
            try
            {
                var full = Path.Combine(project.SourcePath, f);
                if (new FileInfo(full).Length < 2 * 1024 * 1024) contents[f] = File.ReadAllLines(full);
            }
            catch (IOException) { }
        }
        var modNames = new[] { d.Analysis?.ModInfo?.Name, d.Analysis?.ModInfo?.DisplayName, project.ModFolderName, project.Name }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var patchTargets = d.Analysis?.HarmonyPatches ?? new();
        var ownTypes = d.Analysis?.Dlls.Where(x => x.Assembly.Success).SelectMany(x => x.Assembly.Types.Select(t => t.FullName)).ToHashSet() ?? new HashSet<string>();
        var ownAssemblies = d.Analysis?.Dlls.Select(x => x.Assembly.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

        foreach (var group in d.Logs.SelectMany(l => l.Groups))
        {
            var e = group.First;
            var related = false;
            if (e.ModNames.Any(modNames.Contains)) related = true;
            if (ownAssemblies.Any(a => e.Message.Contains(a, StringComparison.OrdinalIgnoreCase))) related = true;
            if (e.StackTrace.Any(f => ownTypes.Contains(f.Type) || ownTypes.Any(t => f.Type.StartsWith(t + ".")))) related = true;

            // Files referenced by name (e.g. blocks.xml, MyPatch.cs:42)
            foreach (var (file, line) in e.FileReferences)
            {
                foreach (var pf in files.Where(pf => Path.GetFileName(pf).Equals(file, StringComparison.OrdinalIgnoreCase)))
                {
                    d.Correlations.Add(new Correlation(group, pf, line, $"Log references '{file}'{(line != null ? $" line {line}" : "")}"));
                    related = true;
                }
            }
            // XPath strings
            if (e.XPath != null)
            {
                foreach (var (f, lines) in contents.Where(c => c.Key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                {
                    var idx = Array.FindIndex(lines, l => l.Contains(e.XPath, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        d.Correlations.Add(new Correlation(group, f, idx + 1, $"Contains the XPath from the log: {e.XPath}"));
                        related = true;
                    }
                }
            }
            // Harmony targets / identifiers mentioned in the error
            foreach (var p in patchTargets)
            {
                var patchSimple = p.PatchClass.Split('.', '+').Last();
                if (e.Message.Contains(p.PatchClass, StringComparison.Ordinal) || (patchSimple.Length > 6 && e.Message.Contains(patchSimple, StringComparison.Ordinal)))
                {
                    d.Correlations.Add(new Correlation(group, p.File ?? p.PatchClass, p.Line, $"Error names Harmony patch class {p.PatchClass} (target {p.TargetDisplay})"));
                    related = true;
                }
                if (p.TargetType is null) continue;
                var simpleType = p.TargetType.Split('.', '+').Last();
                var mentioned = e.ReferencedIdentifiers.Any(i => i.Contains(simpleType) && (p.TargetMethod is null || i.Contains(p.TargetMethod)))
                    || (p.TargetMethod != null && e.Message.Contains(simpleType) && e.Message.Contains(p.TargetMethod));
                if (mentioned && group.Category is LogCategory.Harmony or LogCategory.MissingMethod or LogCategory.MissingField or LogCategory.MissingType or LogCategory.Exception or LogCategory.NullReference)
                {
                    d.Correlations.Add(new Correlation(group, p.File ?? p.PatchClass, p.Line, $"Harmony patch {p.PatchClass} targets {p.TargetDisplay}, which appears in this error"));
                    related = true;
                }
            }
            // Identifiers declared in source (class names from stack frames)
            foreach (var frame in e.StackTrace.Take(6))
            {
                var simple = frame.Type.Split('.', '+').Last();
                foreach (var (f, lines) in contents.Where(c => c.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
                {
                    var idx = Array.FindIndex(lines, l => l.Contains("class " + simple, StringComparison.Ordinal));
                    if (idx >= 0)
                    {
                        d.Correlations.Add(new Correlation(group, f, idx + 1, $"Declares {simple}, which is in the stack trace ({frame.Type}.{frame.Method})"));
                        related = true;
                    }
                }
            }
            if (related) d.RelevantGroups.Add(group);
        }
        var distinct = d.Correlations.DistinctBy(c => (c.Group.Signature, c.ProjectFile, c.Line)).ToList();
        d.Correlations.Clear();
        d.Correlations.AddRange(distinct);
    }

    private static void BuildConclusions(ModProject project, Diagnosis d, IGameKnowledgeIndex? index)
    {
        var correlatedFiles = d.Correlations.Select(c => c.ProjectFile.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (d.Comparison != null)
        {
            var prefix = string.IsNullOrEmpty(project.ModRootRelativePath) ? "" : project.ModRootRelativePath.TrimEnd('/') + "/";
            var changedAndCorrelated = d.Comparison.Files
                .Where(f => correlatedFiles.Contains(prefix + f.RelativePath) || correlatedFiles.Contains(f.RelativePath)).ToList();
            foreach (var f in changedAndCorrelated)
                d.LikelyCauses.Add($"LIKELY REGRESSION: {f.RelativePath} was {f.Kind.ToString().ToLowerInvariant()} since the working version and is referenced by a log error.");
            if (changedAndCorrelated.Count == 0 && d.Comparison.HasChanges)
                d.LikelyCauses.Add($"{d.Comparison.Files.Count} file(s) changed since the working version; none were directly referenced by log errors.");
        }
        foreach (var f in d.GameCompatibilityFindings.Where(f => f.Severity == Severity.Error).Take(15))
            d.LikelyCauses.Add($"Incompatible with installed game: {f.Message}{(f.FilePath != null ? $" ({f.FilePath}{(f.Line != null ? ":" + f.Line : "")})" : "")}");
        foreach (var g in d.RelevantGroups.Take(10))
        {
            var e = g.First;
            var action = g.Category switch
            {
                LogCategory.MissingMethod or LogCategory.MissingField or LogCategory.MissingType =>
                    "The mod uses game code that no longer exists in this game version. Update the code to the current API (search the Game Profile index for the replacement) and recompile.",
                LogCategory.Harmony => "A Harmony patch failed to apply. Check the patch target's name and parameter list against the installed game.",
                LogCategory.XPath => "An XML patch did not apply. Update the XPath to match the current game XML.",
                LogCategory.XmlError => "Fix the malformed or invalid XML at the referenced location.",
                LogCategory.AssemblyLoad => "A DLL failed to load. Recompile it against the current game assemblies and check its dependencies.",
                LogCategory.NullReference => "A null reference occurred in code related to this mod. Inspect the correlated source location.",
                _ => "Inspect the correlated files.",
            };
            d.ProposedRepairs.Add($"[{g.Category}] x{g.Count} line {e.LineNumber}: {Trim(e.Message, 200)}\n    → {action}");
        }
        if (d.RelevantGroups.Count == 0 && d.Logs.Count > 0)
            d.LikelyCauses.Add("No log errors could be linked to this mod's files, names or assemblies.");
    }

    private static string Render(ModProject project, Diagnosis d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DIAGNOSIS — {project.Name} {project.Version}");
        sb.AppendLine(new string('=', 60));
        if (d.Analysis != null) sb.AppendLine(d.Analysis.Summary());
        sb.AppendLine();
        if (d.LikelyCauses.Count > 0)
        {
            sb.AppendLine("LIKELY CAUSES");
            foreach (var c in d.LikelyCauses) sb.AppendLine("  • " + c);
            sb.AppendLine();
        }
        if (d.Comparison != null)
        {
            sb.AppendLine("CHANGES SINCE WORKING VERSION");
            foreach (var f in d.Comparison.Files) sb.AppendLine($"  [{f.Kind}] {f.RelativePath}");
            sb.AppendLine();
        }
        foreach (var log in d.Logs)
            sb.AppendLine($"LOG {Path.GetFileName(log.FilePath)} ({log.Kind}): {log.TotalLines:N0} lines, {log.ErrorCount} errors, {log.WarningCount} warnings, {log.Groups.Count} distinct problems{(log.Truncated ? " (entry limit reached)" : "")}");
        if (d.RelevantGroups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("RELEVANT LOG FAILURES");
            foreach (var g in d.RelevantGroups.Take(20))
            {
                sb.AppendLine($"  [{g.Category}] x{g.Count} (first at line {g.First.LineNumber}): {Trim(g.First.Message, 300)}");
                foreach (var f in g.First.StackTrace.Take(3)) sb.AppendLine($"      at {f.Type}.{f.Method}");
            }
        }
        if (d.Correlations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("CORRELATED PROJECT FILES");
            foreach (var c in d.Correlations.Take(40)) sb.AppendLine($"  {c.ProjectFile}{(c.Line != null ? ":" + c.Line : "")} — {c.Reason}");
        }
        if (d.ProposedRepairs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("PROPOSED REPAIRS");
            foreach (var r in d.ProposedRepairs) sb.AppendLine("  " + r);
        }
        return sb.ToString();
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
