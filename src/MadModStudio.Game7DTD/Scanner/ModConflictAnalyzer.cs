using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Index;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.Game7DTD.Scanner;

public enum ConflictKind { XmlSameValue, XmlRemovedTarget, XmlDuplicateDefinition, HarmonySameMethod, DuplicateAssembly, DuplicateModName, LocalizationKey }

/// <summary>A clash between two or more installed mods. <see cref="Mods"/> is in load (folder-name) order.</summary>
public sealed record ModConflict(ConflictKind Kind, Severity Severity, string Summary, IReadOnlyList<string> Mods, string? File, string Detail);

/// <summary>Input for conflict analysis: one mod root and (optionally) its analysis report.</summary>
public sealed record ConflictInput(string FolderPath, string Name, ModAnalysisReport? Analysis);

/// <summary>
/// Finds mods that fight over the same game data. XML patches are evaluated against the installed game's own config
/// files, so two XPaths that are written differently but select the same node are still recognised. Everything is
/// read-only and deterministic: no AI, nothing executed.
/// </summary>
public static class ModConflictAnalyzer
{
    private static readonly HashSet<string> WriteOps = new(StringComparer.OrdinalIgnoreCase) { "set", "setattribute", "removeattribute" };
    private static readonly HashSet<string> AddOps = new(StringComparer.OrdinalIgnoreCase) { "append", "prepend", "insertafter", "insertbefore" };

    private sealed record Touch(string Mod, string File, string Op, object Target, string Key, string Display, string? Value, int? Line);

    public static IReadOnlyList<ModConflict> Analyze(IReadOnlyList<ConflictInput> mods, string? gameConfigPath, CancellationToken ct = default)
    {
        // Mods load in folder-name order; report every conflict in that order so "applies last" is meaningful.
        var ordered = mods.OrderBy(m => Path.GetFileName(m.FolderPath), StringComparer.OrdinalIgnoreCase).ToList();
        var conflicts = new List<ModConflict>();
        XmlConflicts(ordered, gameConfigPath, conflicts, ct);
        HarmonyConflicts(ordered, conflicts);
        AssemblyConflicts(ordered, conflicts);
        NameConflicts(ordered, conflicts);
        LocalizationConflicts(ordered, conflicts);
        return conflicts.OrderBy(c => c.Severity == Severity.Error ? 0 : c.Severity == Severity.Warning ? 1 : 2).ThenBy(c => c.Kind).ThenBy(c => c.Summary, StringComparer.Ordinal).ToList();
    }

    private static string Folder(ConflictInput m) => Path.GetFileName(m.FolderPath);

    // ---------------- XML ----------------

    private static void XmlConflicts(List<ConflictInput> mods, string? gameConfig, List<ModConflict> conflicts, CancellationToken ct)
    {
        var haveGame = gameConfig != null && Directory.Exists(gameConfig);
        var gameDocs = new Dictionary<string, XDocument?>(StringComparer.OrdinalIgnoreCase);
        var touches = new List<Touch>();
        var additions = new List<(string Mod, string File, string Key, string Display)>();

        foreach (var mod in mods)
        {
            var configDir = Path.Combine(mod.FolderPath, "Config");
            if (!Directory.Exists(configDir)) continue;
            foreach (var file in Directory.EnumerateFiles(configDir, "*.xml", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(configDir, file).Replace('\\', '/');
                XDocument modDoc;
                try { modDoc = XDocument.Load(file, LoadOptions.SetLineInfo); }
                catch (XmlException) { continue; } // reported by validation

                XDocument? gameDoc = null;
                if (haveGame && !gameDocs.TryGetValue(rel, out gameDoc))
                {
                    var gamePath = Path.Combine(gameConfig!, rel.Replace('/', Path.DirectorySeparatorChar));
                    try { gameDoc = File.Exists(gamePath) ? XDocument.Load(gamePath) : null; }
                    catch (XmlException) { gameDoc = null; }
                    gameDocs[rel] = gameDoc;
                }

                foreach (var op in modDoc.Descendants().Where(e => e.Attribute("xpath") != null))
                {
                    var kind = op.Name.LocalName.ToLowerInvariant();
                    var xpath = op.Attribute("xpath")!.Value;
                    var line = ((IXmlLineInfo)op).HasLineInfo() ? ((IXmlLineInfo)op).LineNumber : (int?)null;
                    var value = WriteOps.Contains(kind) && kind != "removeattribute" ? op.Value.Trim() : null;

                    if (AddOps.Contains(kind))
                        foreach (var added in op.Elements().Where(e => e.Attribute("name") != null))
                            additions.Add((Folder(mod), rel, $"{added.Name.LocalName}|{added.Attribute("name")!.Value}",
                                $"<{added.Name.LocalName} name=\"{added.Attribute("name")!.Value}\">"));

                    if (gameDoc is null)
                    {
                        // No game XML: only identical XPaths can be compared.
                        if (WriteOps.Contains(kind) || kind == "remove")
                        {
                            var attr = kind == "setattribute" ? op.Attribute("name")?.Value : null;
                            touches.Add(new Touch(Folder(mod), rel, kind, xpath + "|" + attr, "xpath:" + xpath + "|" + attr,
                                xpath + (attr != null ? $" @{attr}" : ""), value, line));
                        }
                        continue;
                    }

                    IEnumerable<object> selected;
                    try { selected = gameDoc.XPathEvaluate(xpath) is IEnumerable<object> seq ? seq.ToList() : Array.Empty<object>(); }
                    catch (XPathException) { continue; } // reported by validation
                    foreach (var node in selected)
                    {
                        switch (node)
                        {
                            case XAttribute a:
                                touches.Add(new Touch(Folder(mod), rel, kind, a, KeyOf(a.Parent!, a.Name.LocalName), Describe(a.Parent!) + $" @{a.Name.LocalName}", value, line));
                                break;
                            case XElement e when kind == "setattribute" && op.Attribute("name")?.Value is { } attrName:
                                touches.Add(new Touch(Folder(mod), rel, kind, e, KeyOf(e, attrName), Describe(e) + $" @{attrName}", value, line));
                                break;
                            case XElement e:
                                touches.Add(new Touch(Folder(mod), rel, kind, e, KeyOf(e, kind == "set" ? "#text" : "#node"), Describe(e), value, line));
                                break;
                        }
                    }
                }
            }
        }

        // 1. Two mods writing the same value: the one loaded last wins.
        foreach (var g in touches.Where(t => WriteOps.Contains(t.Op)).GroupBy(t => (t.File, t.Key)))
        {
            var byMod = g.GroupBy(t => t.Mod).Select(x => x.Last()).ToList();
            if (byMod.Count < 2) continue;
            var sameValue = byMod.Select(t => t.Op == "removeattribute" ? "\u0000removed" : t.Value).Distinct().Count() == 1;
            var winner = byMod[^1];
            conflicts.Add(new ModConflict(ConflictKind.XmlSameValue, sameValue ? Severity.Info : Severity.Warning,
                $"{Subject(byMod.Select(t => t.Mod).ToList(), "all")} change {winner.Display} in {g.Key.File}",
                byMod.Select(t => t.Mod).ToList(), "Config/" + g.Key.File,
                (sameValue ? "They set the same value, so the result is the same either way. " : $"Mods load in folder-name order, so {winner.Mod}'s value applies last and wins. ")
                + string.Join("; ", byMod.Select(t => $"{t.Mod}: {(t.Op == "removeattribute" ? "removes it" : $"'{Truncate(t.Value)}'")}{(t.Line != null ? $" (line {t.Line})" : "")}"))));
        }

        // 2. One mod removes a node that another mod patches.
        var removals = touches.Where(t => t.Op == "remove" && t.Target is XElement).ToList();
        foreach (var rem in removals)
        {
            var removed = (XElement)rem.Target;
            var victims = touches.Where(t => t.Mod != rem.Mod && t.File == rem.File && IsInside(t.Target, removed))
                .GroupBy(t => t.Mod).Select(g => g.First()).ToList();
            foreach (var v in victims)
                conflicts.Add(new ModConflict(ConflictKind.XmlRemovedTarget, Severity.Warning,
                    $"{rem.Mod} removes {Describe(removed)}, which {v.Mod} patches ({v.Op})",
                    Ordered(mods, rem.Mod, v.Mod), "Config/" + rem.File,
                    $"If {rem.Mod} loads first, {v.Mod}'s patch matches nothing; if it loads later, it removes {v.Mod}'s change. {rem.Mod} line {rem.Line?.ToString() ?? "?"}, {v.Mod} line {v.Line?.ToString() ?? "?"}."));
        }
        // Textual fallback for removals when game XML is unavailable.
        foreach (var g in touches.Where(t => t.Target is string).GroupBy(t => (t.File, t.Key)))
            if (g.Any(t => t.Op == "remove") && g.Select(t => t.Mod).Distinct().Count() > 1 && !g.All(t => WriteOps.Contains(t.Op)))
            {
                var names = g.Select(t => t.Mod).Distinct().ToList();
                conflicts.Add(new ModConflict(ConflictKind.XmlRemovedTarget, Severity.Warning, $"{Subject(names, "all")} patch or remove the same XPath in {g.Key.File}",
                    names, "Config/" + g.Key.File, $"Identical XPath (game XML not available to compare nodes): {g.First().Display}"));
            }

        // 3. Two mods adding a definition with the same name.
        foreach (var g in additions.GroupBy(a => (a.File, a.Key)))
        {
            var names = g.Select(a => a.Mod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count < 2) continue;
            conflicts.Add(new ModConflict(ConflictKind.XmlDuplicateDefinition, Severity.Warning,
                $"{Subject(names, "each")} add {g.First().Display} to {g.Key.File}",
                names, "Config/" + g.Key.File, "The game ends up with duplicate definitions of the same name; usually only one works as intended."));
        }
    }

    private static string KeyOf(XElement e, string part) => $"{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(e)}#{part}";

    private static bool IsInside(object target, XElement removed) => target switch
    {
        XAttribute a => a.Parent != null && (a.Parent == removed || a.Parent.Ancestors().Contains(removed)),
        XElement e => e == removed || e.Ancestors().Contains(removed),
        _ => false,
    };

    /// <summary>Readable path such as /blocks/block[@name='cntWoodWritableCrate']/property[@name='Class'].</summary>
    private static string Describe(XElement e)
    {
        var parts = new List<string>();
        for (var cur = e; cur != null; cur = cur.Parent)
        {
            var name = cur.Attribute("name")?.Value;
            parts.Add(name != null ? $"{cur.Name.LocalName}[@name='{name}']" : cur.Name.LocalName);
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    /// <summary>"A and B both" / "A, B and C all".</summary>
    private static string Subject(IReadOnlyList<string> names, string many) => names.Count == 2
        ? $"{names[0]} and {names[1]} both"
        : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} {many}";

    private static string Truncate(string? s) => s is null ? "" : s.Length <= 60 ? s : s[..57] + "...";

    private static List<string> Ordered(List<ConflictInput> mods, params string[] names) =>
        mods.Select(Folder).Where(f => names.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

    // ---------------- Harmony ----------------

    private static void HarmonyConflicts(List<ConflictInput> mods, List<ModConflict> conflicts)
    {
        var patches = mods.SelectMany(m => (m.Analysis?.HarmonyPatches ?? new List<HarmonyPatchInfo>())
                .Where(p => !p.IsDynamicTarget && p.TargetType != null && p.TargetMethod != null)
                .Select(p => (Mod: Folder(m), Patch: p)))
            .ToList();
        foreach (var g in patches.GroupBy(x => $"{SimpleType(x.Patch.TargetType!)}.{x.Patch.TargetMethod}|{x.Patch.MethodType}|{string.Join(",", x.Patch.ArgumentTypes ?? Array.Empty<string>())}", StringComparer.Ordinal))
        {
            var byMod = g.GroupBy(x => x.Mod).ToList();
            if (byMod.Count < 2) continue;
            var kinds = g.Select(x => x.Patch.PatchKind).Distinct().ToList();
            var risky = kinds.Any(k => k is "Prefix" or "Transpiler");
            var target = g.First().Patch.TargetDisplay;
            conflicts.Add(new ModConflict(ConflictKind.HarmonySameMethod, risky ? Severity.Warning : Severity.Info,
                $"{Subject(byMod.Select(x => x.Key).ToList(), "all")} patch {target}",
                byMod.Select(x => x.Key).ToList(), null,
                string.Join("; ", byMod.Select(x => $"{x.Key}: {string.Join("+", x.Select(p => p.Patch.PatchKind).Distinct())}"))
                + (risky ? ". A prefix that skips the original or a transpiler can stop the other mods' patches from working; test these together."
                         : ". Postfixes on the same method usually coexist.")));
        }
    }

    private static string SimpleType(string type)
    {
        var t = HarmonyPatchMerger.StripAssemblyQualifier(type) ?? type;
        var i = t.LastIndexOf('.');
        return i >= 0 ? t[(i + 1)..] : t;
    }

    // ---------------- DLLs, names, localization ----------------

    private static void AssemblyConflicts(List<ConflictInput> mods, List<ModConflict> conflicts)
    {
        var dlls = mods.SelectMany(m => (m.Analysis?.Dlls ?? new List<DllReport>())
                .Where(d => !string.IsNullOrEmpty(d.Assembly.Name))
                .Select(d => (Mod: Folder(m), d.Assembly.Name, d.Assembly.Version)))
            .ToList();
        foreach (var g in dlls.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var byMod = g.GroupBy(d => d.Mod).Select(x => x.First()).ToList();
            if (byMod.Count < 2) continue;
            var versions = byMod.Select(d => d.Version).Distinct().ToList();
            conflicts.Add(new ModConflict(ConflictKind.DuplicateAssembly, versions.Count > 1 ? Severity.Warning : Severity.Info,
                $"Assembly {g.Key} is shipped by {string.Join(", ", byMod.Select(d => d.Mod))}",
                byMod.Select(d => d.Mod).ToList(), null,
                versions.Count > 1
                    ? $"Different versions ({string.Join(", ", byMod.Select(d => $"{d.Mod}: {d.Version}"))}). Only one copy of an assembly name is used at runtime, so some mods run against a version they were not built for."
                    : $"Same version ({versions[0]}) in each; only one copy is loaded."));
        }
    }

    private static void NameConflicts(List<ConflictInput> mods, List<ModConflict> conflicts)
    {
        foreach (var g in mods.Where(m => m.Analysis?.ModInfo?.Name != null).GroupBy(m => m.Analysis!.ModInfo!.Name!, StringComparer.OrdinalIgnoreCase))
        {
            var folders = g.Select(Folder).ToList();
            if (folders.Count < 2) continue;
            conflicts.Add(new ModConflict(ConflictKind.DuplicateModName, Severity.Error,
                $"{folders.Count} folders declare the same mod Name '{g.Key}': {string.Join(", ", folders)}",
                folders, "ModInfo.xml", "The game identifies mods by their ModInfo Name. Keep one of these folders (often an old copy was left behind)."));
        }
    }

    private static void LocalizationConflicts(List<ConflictInput> mods, List<ModConflict> conflicts)
    {
        var rows = new List<(string Mod, string Key, string? English)>();
        foreach (var m in mods)
        {
            foreach (var name in new[] { "Localization.txt", "Localization.csv" })
            {
                var path = Path.Combine(m.FolderPath, "Config", name);
                if (!File.Exists(path)) continue;
                try { rows.AddRange(LocalizationFile.ReadRows(path).Select(r => (Folder(m), r.Key, r.English))); }
                catch (IOException) { }
            }
        }
        var clashes = rows.GroupBy(r => r.Key, StringComparer.Ordinal)
            .Select(g => g.GroupBy(r => r.Mod).Select(x => x.Last()).ToList())
            .Where(byMod => byMod.Count > 1 && byMod.Select(r => r.English).Distinct().Count() > 1)
            .ToList();
        foreach (var byMod in clashes.Take(50))
            conflicts.Add(new ModConflict(ConflictKind.LocalizationKey, Severity.Info,
                $"Localization key '{byMod[0].Key}' has different text in {string.Join(", ", byMod.Select(r => r.Mod))}",
                byMod.Select(r => r.Mod).ToList(), "Config/Localization.txt",
                string.Join("; ", byMod.Select(r => $"{r.Mod}: \"{Truncate(r.English)}\""))));
        if (clashes.Count > 50)
            conflicts.Add(new ModConflict(ConflictKind.LocalizationKey, Severity.Info, $"{clashes.Count - 50} more localization keys differ between mods.",
                Array.Empty<string>(), "Config/Localization.txt", ""));
    }
}
