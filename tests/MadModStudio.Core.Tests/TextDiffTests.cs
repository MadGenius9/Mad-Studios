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
}
