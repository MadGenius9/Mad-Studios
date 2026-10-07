using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;
using MadModStudio.TestSupport;

namespace MadModStudio.Core.Tests;

public class VersionComparisonTests
{
    [Fact]
    public void Unified_diff_reports_changed_lines()
    {
        var (diff, added, removed) = TextDiff.Unified("a\nb\nc\n", "a\nB\nc\nd\n", "a/x", "b/x");
        Assert.Equal(2, added);
        Assert.Equal(1, removed);
        Assert.Contains("-b", diff);
        Assert.Contains("+B", diff);
        Assert.Contains("@@ -1,3 +1,4 @@", diff);
    }

    [Fact]
    public void Compares_two_mod_versions()
    {
        var a = FakeGame.TempDir("va");
        var b = FakeGame.TempDir("vb");
        File.WriteAllText(Path.Combine(a, "ModInfo.xml"), "<xml><Version value=\"1.0.7\"/></xml>");
        File.WriteAllText(Path.Combine(b, "ModInfo.xml"), "<xml><Version value=\"1.0.8\"/></xml>");
        File.WriteAllText(Path.Combine(a, "same.xml"), "<same/>");
        File.WriteAllText(Path.Combine(b, "same.xml"), "<same/>");
        File.WriteAllText(Path.Combine(a, "removed.xml"), "<r/>");
        File.WriteAllText(Path.Combine(b, "Patch.cs"), "class P {}");

        var cmp = new VersionComparer(new AssemblyInspector()).Compare(a, b);

        Assert.Equal(1, cmp.UnchangedCount);
        Assert.Equal("Patch.cs", Assert.Single(cmp.Added).RelativePath);
        Assert.Equal("removed.xml", Assert.Single(cmp.Removed).RelativePath);
        var mod = Assert.Single(cmp.Modified);
        Assert.Equal(FileCategory.ModInfo, mod.Category);
        Assert.Contains("+<xml><Version value=\"1.0.8\"/></xml>", mod.UnifiedDiff);
        Assert.Contains("Added: 1  Removed: 1  Modified: 1", cmp.Summarize());
    }

    [Fact]
    public void Side_by_side_aligns_changes_and_collapses_unchanged_runs()
    {
        var old = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToList();
        lines[9] = "line 10 changed";       // modified
        lines.Insert(15, "brand new line");  // added after old line 15
        lines.RemoveAt(2);                   // old line 3 removed
        var rows = TextDiff.SideBySide(old, string.Join("\n", lines) + "\n", context: 1);

        Assert.Contains(rows, r => r.Kind == DiffRowKind.Removed && r.OldLine == 3 && r.OldText == "line 3" && r.NewLine == null);
        Assert.Contains(rows, r => r.Kind == DiffRowKind.Modified && r.OldText == "line 10" && r.NewText == "line 10 changed");
        Assert.Contains(rows, r => r.Kind == DiffRowKind.Added && r.NewText == "brand new line" && r.OldLine == null);
        Assert.Contains(rows, r => r.Kind == DiffRowKind.Gap && r.OldText.Contains("unchanged"));
        Assert.DoesNotContain(rows, r => r.OldText == "line 7"); // far from any change: collapsed
        Assert.Contains(rows, r => r.Kind == DiffRowKind.Unchanged && r.OldText == "line 9"); // context line kept
    }

    [Fact]
    public void Side_by_side_of_a_new_file_is_all_additions()
    {
        var rows = TextDiff.SideBySide("", "a\nb\n");
        Assert.Equal(new[] { DiffRowKind.Added, DiffRowKind.Added }, rows.Select(r => r.Kind));
        Assert.Equal(new int?[] { 1, 2 }, rows.Select(r => r.NewLine));
    }
}
