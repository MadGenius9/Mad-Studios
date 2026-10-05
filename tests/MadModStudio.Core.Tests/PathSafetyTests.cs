using MadModStudio.Core.IO;

namespace MadModStudio.Core.Tests;

public class PathSafetyTests
{
    [Theory]
    [InlineData("Config/blocks.xml", true)]
    [InlineData("a/b/c.txt", true)]
    [InlineData("../x", false)]
    [InlineData("a/../../x", false)]
    [InlineData("/abs", false)]
    [InlineData("C:\\abs", false)]
    [InlineData("NUL", false)]
    [InlineData("dir/aux.xml", false)]
    [InlineData("trailingdot.", false)]
    [InlineData("", false)]
    public void ResolveUnderRoot_accepts_only_safe_relative_paths(string rel, bool ok)
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        var result = PathSafety.ResolveUnderRoot(root, rel);
        Assert.Equal(ok, result != null);
        if (ok) Assert.StartsWith(Path.GetFullPath(root), result);
    }

    [Theory]
    [InlineData("My Mod: v2?", "My Mod_ v2_")]
    [InlineData("  ", "Mod")]
    [InlineData("CON", "Mod")]
    public void SanitizeFileName_removes_invalid_characters(string input, string expected) =>
        Assert.Equal(expected, PathSafety.SanitizeFileName(input));

    [Theory]
    [InlineData("Config/blocks.xml", false)]
    [InlineData(".git/config", true)]
    [InlineData("src/obj/Debug/x.dll", true)]
    [InlineData(".vs/settings.json", true)]
    [InlineData("Harmony/Patch.cs", true)]
    [InlineData("build.log", true)]
    [InlineData("madmodstudio.db", true)]
    [InlineData("keys.apikey", true)]
    [InlineData("MyMod.pdb", true)]
    [InlineData("MyMod.dll", false)]
    public void Package_exclusion_rules(string path, bool excluded) =>
        Assert.Equal(excluded, new FileExclusionRules().IsExcluded(path, out _));

    [Fact]
    public void Snapshot_rules_keep_source_but_drop_build_output()
    {
        var rules = FileExclusionRules.ForSnapshots();
        Assert.False(rules.IsExcluded("Harmony/Patch.cs", out _));
        Assert.False(rules.IsExcluded("Mod.csproj", out _));
        Assert.True(rules.IsExcluded("bin/Release/Mod.dll", out _));
    }
}
