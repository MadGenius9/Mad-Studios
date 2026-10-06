using System.Text.RegularExpressions;
using System.Xml.Linq;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.Game7DTD.Repair;

/// <summary>A deterministic, behaviour-preserving fix. Anything that needs judgement is never a safe fix.</summary>
public sealed record SafeFix(string Id, string Description, IReadOnlyList<string> Files);

/// <summary>Outcome of fixing one mod from a Mods folder: imported copy, fixes, and the real build/validation result.</summary>
public sealed class SafeFixCopyResult
{
    public string SourceFolder { get; init; } = "";
    public ModProject? Project { get; init; }
    public IReadOnlyList<SafeFix> Applied { get; init; } = Array.Empty<SafeFix>();
    public BuildResult? Build { get; init; }
    public string? Error { get; init; }
    /// <summary>True when an earlier fixed copy of the unchanged folder was reused instead of importing again.</summary>
    public bool Reused { get; init; }
    public bool Packaged => Build?.Package?.Success == true && Build.Succeeded;
}

public sealed class SafeFixResult
{
    public IReadOnlyList<SafeFix> Applied { get; init; } = Array.Empty<SafeFix>();
    public RevisionRecord? Before { get; init; }
    public RevisionRecord? After { get; init; }
}

/// <summary>
/// Plans and applies "safe fixes": mechanical corrections with exactly one correct outcome (folder-name case, legacy
/// ModInfo layout, missing ModInfo identity fields). Fixes are only ever applied to a project's working copy, between
/// two revisions, so they can be reviewed and restored. Planning is read-only and can run on any folder.
/// </summary>
public sealed partial class SafeFixService
{
    public const string ConfigFolderCase = "config-folder-case";
    public const string ModInfoLegacyLayout = "modinfo-legacy-layout";
    public const string ModInfoMissingName = "modinfo-missing-name";
    public const string ModInfoMissingVersion = "modinfo-missing-version";
    public const string ModInfoMissing = "modinfo-missing";
    public const string PlaceholderVersion = "1.0.0";

    private readonly ProjectService _projects;
    private readonly ModBuildPipeline _pipeline;

    public SafeFixService(ProjectService projects, ModBuildPipeline pipeline)
    {
        _projects = projects;
        _pipeline = pipeline;
    }

    /// <summary>
    /// Imports a copy of the mod folder as a Repair project (the folder itself is never modified), applies its safe
    /// fixes and runs the real build/validation pipeline. A clean result has a package that Deploy to Game can install.
    /// </summary>
    public async Task<SafeFixCopyResult> FixCopyAsync(string modFolder, Guid? gameProfileId, CancellationToken ct = default)
    {
        var plan = Plan(modFolder);
        if (plan.Count == 0) return new SafeFixCopyResult { SourceFolder = modFolder, Error = "No safe fixes apply." };
        try
        {
            if (await FindPreviousFixAsync(modFolder, ct).ConfigureAwait(false) is { } previous)
            {
                var rebuilt = await _pipeline.RunAsync(previous, new BuildOptions(), ct: ct).ConfigureAwait(false);
                return new SafeFixCopyResult { SourceFolder = modFolder, Project = previous, Applied = plan, Build = rebuilt, Reused = true };
            }
            var imported = await _projects.ImportAsync(modFolder, gameProfileId, ProjectOrigin.Repair, ct).ConfigureAwait(false);
            if (imported.Count != 1)
                return new SafeFixCopyResult { SourceFolder = modFolder, Error = $"Expected one mod in the folder, found {imported.Count}." };
            var project = imported[0].Project;
            var fixes = await ApplyAsync(project, ct).ConfigureAwait(false);
            var build = await _pipeline.RunAsync(project, new BuildOptions(), ct: ct).ConfigureAwait(false);
            return new SafeFixCopyResult { SourceFolder = modFolder, Project = project, Applied = fixes.Applied, Build = build };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportException or InvalidOperationException)
        {
            return new SafeFixCopyResult { SourceFolder = modFolder, Error = ex.Message };
        }
    }

    /// <summary>Detects which safe fixes apply to a mod folder. Never modifies anything.</summary>
    public static IReadOnlyList<SafeFix> Plan(string modRoot)
    {
        var fixes = new List<SafeFix>();
        if (!Directory.Exists(modRoot)) return fixes;
        var folderName = Path.GetFileName(Path.GetFullPath(modRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var config = Directory.GetDirectories(modRoot).Select(Path.GetFileName)
            .Where(n => n != null && n.Equals("Config", StringComparison.OrdinalIgnoreCase)).ToList();
        if (config.Count == 1 && config[0] != "Config")
            fixes.Add(new SafeFix(ConfigFolderCase, $"Rename folder '{config[0]}' to 'Config' (Linux dedicated servers use case-sensitive paths).", new[] { config[0]! }));

        var modInfoPath = Path.Combine(modRoot, ModInfoFile.FileName);
        if (!File.Exists(modInfoPath))
        {
            var hasContent = config.Count > 0 || Directory.EnumerateFiles(modRoot, "*.dll", SearchOption.AllDirectories).Any();
            // A nested ModInfo means a wrong folder hierarchy: that needs a human decision, so it is not a safe fix.
            if (hasContent && !ModInfoFile.FindAll(modRoot, 3).Any() && ValidIdentifier(folderName))
                fixes.Add(new SafeFix(ModInfoMissing, $"Create ModInfo.xml (Name '{folderName}' from the folder name, placeholder Version {PlaceholderVersion}).", new[] { ModInfoFile.FileName }));
            return fixes;
        }

        var info = ModInfoFile.TryParse(modInfoPath, out _);
        if (info is null || info.Format == ModInfoFormat.Unknown) return fixes; // malformed XML is not mechanically fixable
        if (info.Format == ModInfoFormat.V1)
            fixes.Add(new SafeFix(ModInfoLegacyLayout, "Convert ModInfo.xml from the legacy <ModInfo> wrapper to the current layout (same fields and values).", new[] { ModInfoFile.FileName }));
        if (info.Name is null && ValidIdentifier(folderName))
            fixes.Add(new SafeFix(ModInfoMissingName, $"Set the missing ModInfo Name to the folder name '{folderName}'.", new[] { ModInfoFile.FileName }));
        if (info.Version is null)
            fixes.Add(new SafeFix(ModInfoMissingVersion, $"Set the missing ModInfo Version to the placeholder {PlaceholderVersion}.", new[] { ModInfoFile.FileName }));
        return fixes;
    }

    /// <summary>An earlier safe-fix project imported from this exact folder, if the folder has not changed since.</summary>
    private async Task<ModProject?> FindPreviousFixAsync(string modFolder, CancellationToken ct)
    {
        var full = Path.GetFullPath(modFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = (await _projects.ListAsync(ct).ConfigureAwait(false))
            .Where(p => p.Origin == ProjectOrigin.Repair && p.ImportedFrom != null
                && string.Equals(Path.GetFullPath(p.ImportedFrom).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), full, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.CreatedUtc);
        Dictionary<string, string>? current = null;
        foreach (var p in candidates)
        {
            var revisions = await _projects.History.ListAsync(p, ct).ConfigureAwait(false);
            if (!revisions.Any(r => r.Action == "Safe fixes applied")) continue;
            var original = Path.Combine(p.Workspace.Original, Path.GetFileName(full));
            if (!Directory.Exists(original)) continue;
            current ??= HashTree(full);
            var snapshot = HashTree(original);
            if (snapshot.Count == current.Count && snapshot.All(kv => current.TryGetValue(kv.Key, out var h) && h == kv.Value)) return p;
        }
        return null;
    }

    private static Dictionary<string, string> HashTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), MadModStudio.Core.IO.FileUtil.Sha256, StringComparer.Ordinal);

    /// <summary>Applies the planned safe fixes to the project's working copy, with a revision before and after.</summary>
    public async Task<SafeFixResult> ApplyAsync(ModProject project, CancellationToken ct = default)
    {
        var plan = Plan(project.ModRootPath);
        if (plan.Count == 0) return new SafeFixResult();
        var meta = new Dictionary<string, string> { ["kind"] = "safe-fix", ["fixes"] = string.Join(",", plan.Select(f => f.Id)) };
        var before = await _projects.History.CreateRevisionAsync(project, "Before safe fixes", string.Join(" ", plan.Select(f => f.Description)),
            force: true, ct: ct, metadata: meta).ConfigureAwait(false);
        var root = project.ModRootPath;
        var folderName = string.IsNullOrEmpty(project.ModFolderName) ? Path.GetFileName(root) : project.ModFolderName;
        foreach (var fix in plan)
        {
            ct.ThrowIfCancellationRequested();
            var modInfo = Path.Combine(root, ModInfoFile.FileName);
            switch (fix.Id)
            {
                case ConfigFolderCase:
                    // Two-step rename so it also works on case-insensitive file systems.
                    var src = Path.Combine(root, fix.Files[0]);
                    var tmp = Path.Combine(root, "Config.mms-rename-" + Guid.NewGuid().ToString("N")[..8]);
                    Directory.Move(src, tmp);
                    Directory.Move(tmp, Path.Combine(root, "Config"));
                    break;
                case ModInfoMissing:
                    ModInfoFile.Create(modInfo, folderName, folderName, PlaceholderVersion, null, null);
                    break;
                case ModInfoLegacyLayout:
                    ConvertLegacyLayout(modInfo);
                    break;
                case ModInfoMissingName:
                    SetField(modInfo, "Name", folderName);
                    break;
                case ModInfoMissingVersion:
                    ModInfoFile.SetVersion(modInfo, PlaceholderVersion);
                    break;
            }
        }
        var after = await _projects.History.CreateRevisionAsync(project, "Safe fixes applied", string.Join(" ", plan.Select(f => f.Description)),
            ct: ct, metadata: meta).ConfigureAwait(false);
        return new SafeFixResult { Applied = plan, Before = before, After = after };
    }

    private static void ConvertLegacyLayout(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root!;
        var legacy = root.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase)
            ? root
            : root.Elements().First(e => e.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase));
        var fields = legacy.Elements().Select(e => new XElement(e.Name.LocalName, new XAttribute("value", e.Attribute("value")?.Value ?? e.Value.Trim())));
        var others = legacy == root ? Enumerable.Empty<XElement>() : root.Elements().Where(e => e != legacy).Select(e => new XElement(e));
        var converted = new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("xml", fields.Concat(others)));
        File.WriteAllText(path, converted.Declaration + Environment.NewLine + converted.Root);
    }

    private static void SetField(string path, string field, string value)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var container = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase)) ?? doc.Root;
        var el = container.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (el is null) container.AddFirst(el = new XElement(field));
        el.SetAttributeValue("value", value);
        File.WriteAllText(path, (doc.Declaration != null ? doc.Declaration + Environment.NewLine : "") + doc.Root);
    }

    private static bool ValidIdentifier(string? name) => name != null && IdentifierRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.]+$")]
    private static partial Regex IdentifierRegex();
}
