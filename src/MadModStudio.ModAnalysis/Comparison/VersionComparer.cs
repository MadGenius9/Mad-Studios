using MadModStudio.Core.IO;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.ModAnalysis.Comparison;

public enum FileChangeKind { Added, Removed, Modified }

public enum FileCategory { ModInfo, Xml, CSharp, Dll, Localization, Project, Asset, Other }

public sealed class DllMetadataDiff
{
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    public List<string> AddedTypes { get; } = new();
    public List<string> RemovedTypes { get; } = new();
    public List<string> AddedMembers { get; } = new();
    public List<string> RemovedMembers { get; } = new();
    public List<string> AddedReferences { get; } = new();
    public List<string> RemovedReferences { get; } = new();
    public List<string> HarmonyPatchChanges { get; } = new();
    public string? Error { get; init; }
}

public sealed class FileDiff
{
    public string RelativePath { get; init; } = "";
    public FileChangeKind Kind { get; init; }
    public FileCategory Category { get; init; }
    public string? UnifiedDiff { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public DllMetadataDiff? Dll { get; init; }
    public bool IsBinary { get; init; }
}

public sealed class VersionComparison
{
    public string LeftRoot { get; init; } = "";
    public string RightRoot { get; init; } = "";
    public List<FileDiff> Files { get; } = new();
    public int UnchangedCount { get; set; }

    public IEnumerable<FileDiff> Added => Files.Where(f => f.Kind == FileChangeKind.Added);
    public IEnumerable<FileDiff> Removed => Files.Where(f => f.Kind == FileChangeKind.Removed);
    public IEnumerable<FileDiff> Modified => Files.Where(f => f.Kind == FileChangeKind.Modified);
    public bool HasChanges => Files.Count > 0;

    /// <summary>Compact text summary, suitable for display and for AI context.</summary>
    public string Summarize(int maxDiffChars = 20_000)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Added: {Added.Count()}  Removed: {Removed.Count()}  Modified: {Modified.Count()}  Unchanged: {UnchangedCount}");
        foreach (var f in Files.OrderBy(f => f.Kind).ThenBy(f => f.RelativePath))
            sb.AppendLine($"  [{f.Kind}] ({f.Category}) {f.RelativePath}{(f.Kind == FileChangeKind.Modified && !f.IsBinary ? $"  +{f.LinesAdded}/-{f.LinesRemoved}" : "")}");
        var budget = maxDiffChars;
        foreach (var f in Files.Where(f => f.UnifiedDiff != null))
        {
            if (budget <= 0) { sb.AppendLine("... (further diffs truncated)"); break; }
            var d = f.UnifiedDiff!;
            if (d.Length > budget) d = d[..budget] + "\n... (truncated)";
            sb.AppendLine().AppendLine(d);
            budget -= d.Length;
        }
        foreach (var f in Files.Where(f => f.Dll != null))
        {
            var d = f.Dll!;
            sb.AppendLine($"DLL {f.RelativePath}: version {d.OldVersion} -> {d.NewVersion}");
            foreach (var t in d.RemovedTypes.Take(50)) sb.AppendLine($"  - type {t}");
            foreach (var t in d.AddedTypes.Take(50)) sb.AppendLine($"  + type {t}");
            foreach (var m in d.RemovedMembers.Take(100)) sb.AppendLine($"  - {m}");
            foreach (var m in d.AddedMembers.Take(100)) sb.AppendLine($"  + {m}");
            foreach (var r in d.RemovedReferences) sb.AppendLine($"  - reference {r}");
            foreach (var r in d.AddedReferences) sb.AppendLine($"  + reference {r}");
            foreach (var h in d.HarmonyPatchChanges) sb.AppendLine($"  ~ harmony {h}");
        }
        return sb.ToString();
    }
}

/// <summary>Compares two versions of a mod folder: files, text diffs, and DLL metadata.</summary>
public sealed class VersionComparer
{
    private const long MaxTextDiffBytes = 4 * 1024 * 1024;
    private readonly AssemblyInspector _inspector;

    public VersionComparer(AssemblyInspector inspector) => _inspector = inspector;

    public VersionComparison Compare(string leftRoot, string rightRoot)
    {
        var rules = FileExclusionRules.ForSnapshots();
        var left = FileUtil.EnumerateRelativeFiles(leftRoot, rules).ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        var right = FileUtil.EnumerateRelativeFiles(rightRoot, rules).ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);
        var cmp = new VersionComparison { LeftRoot = leftRoot, RightRoot = rightRoot };

        foreach (var rel in right.Keys.Except(left.Keys, StringComparer.OrdinalIgnoreCase))
            cmp.Files.Add(new FileDiff { RelativePath = rel, Kind = FileChangeKind.Added, Category = Categorize(rel) });
        foreach (var rel in left.Keys.Except(right.Keys, StringComparer.OrdinalIgnoreCase))
            cmp.Files.Add(new FileDiff { RelativePath = rel, Kind = FileChangeKind.Removed, Category = Categorize(rel) });

        foreach (var rel in left.Keys.Intersect(right.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var a = Path.Combine(leftRoot, left[rel]);
            var b = Path.Combine(rightRoot, right[rel]);
            if (SameContent(a, b)) { cmp.UnchangedCount++; continue; }
            cmp.Files.Add(DiffFile(rel, a, b));
        }
        cmp.Files.Sort((x, y) => string.Compare(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase));
        return cmp;
    }

    private FileDiff DiffFile(string rel, string a, string b)
    {
        var category = Categorize(rel);
        if (category == FileCategory.Dll)
            return new FileDiff { RelativePath = rel, Kind = FileChangeKind.Modified, Category = category, IsBinary = true, Dll = DiffDll(a, b) };

        if (FileUtil.LooksBinary(a) || FileUtil.LooksBinary(b) || new FileInfo(a).Length > MaxTextDiffBytes || new FileInfo(b).Length > MaxTextDiffBytes)
            return new FileDiff { RelativePath = rel, Kind = FileChangeKind.Modified, Category = category, IsBinary = true };

        var (diff, added, removed) = TextDiff.Unified(File.ReadAllText(a), File.ReadAllText(b), "a/" + rel, "b/" + rel);
        return new FileDiff
        {
            RelativePath = rel, Kind = FileChangeKind.Modified, Category = category,
            UnifiedDiff = diff, LinesAdded = added, LinesRemoved = removed,
        };
    }

    public DllMetadataDiff DiffDll(string oldPath, string newPath)
    {
        var a = _inspector.Inspect(oldPath);
        var b = _inspector.Inspect(newPath);
        if (!a.Success || !b.Success)
            return new DllMetadataDiff { Error = a.ReadError ?? b.ReadError, OldVersion = a.Version, NewVersion = b.Version };

        var diff = new DllMetadataDiff { OldVersion = a.Version, NewVersion = b.Version };
        var ta = a.Types.Select(t => t.FullName).ToHashSet();
        var tb = b.Types.Select(t => t.FullName).ToHashSet();
        diff.AddedTypes.AddRange(tb.Except(ta).OrderBy(x => x));
        diff.RemovedTypes.AddRange(ta.Except(tb).OrderBy(x => x));

        static IEnumerable<string> Members(AssemblyReport r) =>
            r.Types.SelectMany(t => t.Members.Select(m => $"{t.FullName}::{m.Signature}"));
        var ma = Members(a).ToHashSet();
        var mb = Members(b).ToHashSet();
        diff.AddedMembers.AddRange(mb.Except(ma).OrderBy(x => x));
        diff.RemovedMembers.AddRange(ma.Except(mb).OrderBy(x => x));

        var ra = a.References.Select(r => r.ToString()).ToHashSet();
        var rb = b.References.Select(r => r.ToString()).ToHashSet();
        diff.AddedReferences.AddRange(rb.Except(ra));
        diff.RemovedReferences.AddRange(ra.Except(rb));

        var ha = a.HarmonyPatches.Select(p => $"{p.PatchKind} {p.TargetDisplay}").ToHashSet();
        var hb = b.HarmonyPatches.Select(p => $"{p.PatchKind} {p.TargetDisplay}").ToHashSet();
        diff.HarmonyPatchChanges.AddRange(hb.Except(ha).Select(x => "+ " + x));
        diff.HarmonyPatchChanges.AddRange(ha.Except(hb).Select(x => "- " + x));
        return diff;
    }

    public static FileCategory Categorize(string rel)
    {
        var name = Path.GetFileName(rel);
        var ext = Path.GetExtension(rel).ToLowerInvariant();
        if (name.Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)) return FileCategory.ModInfo;
        if (name.StartsWith("Localization", StringComparison.OrdinalIgnoreCase)) return FileCategory.Localization;
        return ext switch
        {
            ".xml" => FileCategory.Xml,
            ".cs" => FileCategory.CSharp,
            ".dll" => FileCategory.Dll,
            ".csproj" or ".sln" or ".props" or ".targets" => FileCategory.Project,
            ".png" or ".jpg" or ".unity3d" or ".bundle" or ".wav" or ".ogg" or ".mesh" or ".asset" => FileCategory.Asset,
            _ => FileCategory.Other,
        };
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        return FileUtil.Sha256(a) == FileUtil.Sha256(b);
    }
}
