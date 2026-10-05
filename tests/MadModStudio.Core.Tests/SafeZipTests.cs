using System.IO.Compression;
using MadModStudio.Core.IO;
using MadModStudio.TestSupport;

namespace MadModStudio.Core.Tests;

public class SafeZipTests
{
    private static string MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(FakeGame.TempDir("zip"), "test.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return path;
    }

    [Fact]
    public void Extracts_nested_entries_and_does_not_modify_the_archive()
    {
        var zip = MakeZip(("Mod/ModInfo.xml", "<xml/>"), ("Mod/Config/blocks.xml", "<configs/>"));
        var hash = FileUtil.Sha256(zip);
        var dest = FakeGame.TempDir("out");

        var result = SafeZip.Extract(zip, dest);

        Assert.Equal(2, result.FilesExtracted);
        Assert.Equal("<configs/>", File.ReadAllText(Path.Combine(dest, "Mod", "Config", "blocks.xml")));
        Assert.Equal(hash, FileUtil.Sha256(zip));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("Mod/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("Mod/CON.txt")]
    public void Rejects_path_traversal_and_unsafe_names(string entry)
    {
        var zip = MakeZip(("Mod/ok.txt", "ok"), (entry, "evil"));
        var dest = FakeGame.TempDir("out");
        var parent = Path.GetDirectoryName(dest)!;

        var ex = Assert.Throws<ZipSafetyException>(() => SafeZip.Extract(zip, dest));

        Assert.Contains("Unsafe path", ex.Message);
        Assert.False(File.Exists(Path.Combine(parent, "evil.txt")));
    }

    [Fact]
    public void Corrupt_archive_produces_friendly_error()
    {
        var path = Path.Combine(FakeGame.TempDir("zip"), "corrupt.zip");
        File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8 });

        var ex = Assert.Throws<ZipSafetyException>(() => SafeZip.Extract(path, FakeGame.TempDir("out")));

        Assert.Contains("not a valid ZIP", ex.Message);
    }

    [Fact]
    public void Entry_count_limit_is_enforced()
    {
        var zip = MakeZip(("a.txt", "1"), ("b.txt", "2"), ("c.txt", "3"));
        var ex = Assert.Throws<ZipSafetyException>(() => SafeZip.Extract(zip, FakeGame.TempDir("out"), new ZipExtractionLimits { MaxEntries = 2 }));
        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public void Create_places_files_under_root_folder_and_applies_exclusions()
    {
        var src = FakeGame.TempDir("src");
        File.WriteAllText(Path.Combine(src, "ModInfo.xml"), "<xml/>");
        Directory.CreateDirectory(Path.Combine(src, ".git"));
        File.WriteAllText(Path.Combine(src, ".git", "HEAD"), "ref");
        File.WriteAllText(Path.Combine(src, "Patch.cs"), "class X{}");
        File.WriteAllText(Path.Combine(src, "debug.log"), "x");
        File.WriteAllText(Path.Combine(src, "secrets.json"), "{\"key\":\"sk-123\"}");
        var zip = Path.Combine(FakeGame.TempDir("zipout"), "Mod_1.0.zip");
        var excluded = new List<string>();

        var count = SafeZip.Create(src, zip, "MyMod", new FileExclusionRules(), excluded);

        Assert.Equal(1, count);
        Assert.Equal(new[] { "MyMod/ModInfo.xml" }, SafeZip.ListEntries(zip));
        Assert.Equal(4, excluded.Count);
        Assert.False(File.Exists(zip + ".partial"));
    }
}
