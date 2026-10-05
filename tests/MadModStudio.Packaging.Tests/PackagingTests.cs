using System.IO.Compression;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.TestSupport;

namespace MadModStudio.Packaging.Tests;

public class PackagingTests
{
    private static string Content()
    {
        var dir = Path.Combine(FakeGame.TempDir("content"), "MadWorkingRacks");
        Directory.CreateDirectory(Path.Combine(dir, "Config"));
        File.WriteAllText(Path.Combine(dir, "ModInfo.xml"), SampleMod.ModInfoXml);
        File.WriteAllText(Path.Combine(dir, "Config", "blocks.xml"), SampleMod.BlocksPatch);
        File.WriteAllBytes(Path.Combine(dir, "MadWorkingRacks.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, "MadWorkingRacks.pdb"), new byte[] { 1 });
        foreach (var junk in new[] { ".git/HEAD", ".idea/x.xml", "obj/a.cache", "bin/Debug/x.dll", "Patch.cs", "Mod.csproj", "error.log", "madmodstudio.db", "anthropic.apikey", ".env", "Thumbs.db", "notes.txt~" })
        {
            var p = Path.Combine(dir, junk);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "junk");
        }
        return dir;
    }

    [Fact]
    public void Package_has_single_mod_folder_and_no_development_artifacts()
    {
        var content = Content();
        var output = FakeGame.TempDir("out");

        var r = new ModPackager().CreatePackage(new PackageRequest
        {
            ContentDirectory = content,
            RootFolderName = "MadWorkingRacks",
            OutputDirectory = output,
            PackageBaseName = ModPackager.PackageName("MadWorkingRacks", "1.0.9"),
        });

        Assert.True(r.Success, r.Error);
        Assert.Equal(Path.Combine(output, "MadWorkingRacks_1.0.9.zip"), r.ZipPath);
        Assert.Equal(3, r.FileCount);
        using var zip = ZipFile.OpenRead(r.ZipPath!);
        Assert.Equal(new[] { "MadWorkingRacks/Config/blocks.xml", "MadWorkingRacks/MadWorkingRacks.dll", "MadWorkingRacks/ModInfo.xml" },
            zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(12 + 1, r.ExcludedFiles.Count); // junk + pdb
    }

    [Fact]
    public void Pdb_and_source_can_be_included_explicitly()
    {
        var r = new ModPackager().CreatePackage(new PackageRequest
        {
            ContentDirectory = Content(),
            RootFolderName = "MadWorkingRacks",
            OutputDirectory = FakeGame.TempDir("out"),
            PackageBaseName = "x",
            Rules = new FileExclusionRules { IncludePdb = true, IncludeSource = true },
        });
        var names = SafeZip.ListEntries(r.ZipPath!);
        Assert.Contains("MadWorkingRacks/MadWorkingRacks.pdb", names);
        Assert.Contains("MadWorkingRacks/Patch.cs", names);
        Assert.DoesNotContain(names, n => n.Contains(".git/") || n.EndsWith(".apikey"));
    }

    [Theory]
    [InlineData("Mad Working Racks", "1.0.9", "Mad_Working_Racks_1.0.9")]
    [InlineData("Weird:Name?", "2.0", "Weird_Name__2.0")]
    public void Package_names_are_safe(string name, string version, string expected) =>
        Assert.Equal(expected, ModPackager.PackageName(name, version));

    [Fact]
    public void Empty_or_missing_content_fails_cleanly()
    {
        var missing = new ModPackager().CreatePackage(new PackageRequest { ContentDirectory = "/no/such", RootFolderName = "X", OutputDirectory = FakeGame.TempDir("o"), PackageBaseName = "X" });
        Assert.False(missing.Success);
        Assert.Contains("does not exist", missing.Error);

        var onlyJunk = FakeGame.TempDir("junk");
        File.WriteAllText(Path.Combine(onlyJunk, "a.log"), "x");
        var empty = new ModPackager().CreatePackage(new PackageRequest { ContentDirectory = onlyJunk, RootFolderName = "X", OutputDirectory = FakeGame.TempDir("o"), PackageBaseName = "X" });
        Assert.False(empty.Success);
        Assert.Contains("No files", empty.Error);
    }

    [Fact]
    public async Task Package_structure_validator_detects_bad_zips()
    {
        var dir = FakeGame.TempDir("badzip");
        var flat = Path.Combine(dir, "flat.zip");
        using (var z = ZipFile.Open(flat, ZipArchiveMode.Create))
        {
            z.CreateEntry("ModInfo.xml");
            z.CreateEntry("ModA/Config/x.xml");
            z.CreateEntry("ModB/.git/HEAD");
        }
        var findings = await new PackageStructureValidator().ValidateAsync(new ValidationContext { ModRootPath = dir, PackagePath = flat });
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("ZIP root"));
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("exactly one top-level"));
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains(".git/HEAD"));

        var notZip = Path.Combine(dir, "nope.zip");
        File.WriteAllText(notZip, "text");
        findings = await new PackageStructureValidator().ValidateAsync(new ValidationContext { ModRootPath = dir, PackagePath = notZip });
        Assert.Contains(findings, f => f.Message.Contains("not a valid ZIP"));
    }

    [Fact]
    public void Inspector_reports_structure_of_good_package()
    {
        var r = new ModPackager().CreatePackage(new PackageRequest { ContentDirectory = Content(), RootFolderName = "MadWorkingRacks", OutputDirectory = FakeGame.TempDir("o"), PackageBaseName = "ok" });
        var p = PackageInspector.Inspect(r.ZipPath!);
        Assert.True(p.IsValidZip);
        Assert.Equal(new[] { "MadWorkingRacks" }, p.TopLevelFolders);
        Assert.Equal(new[] { "MadWorkingRacks/ModInfo.xml" }, p.ModInfoLocations);
        Assert.Empty(p.ForbiddenEntries);
        Assert.Empty(p.RootFiles);
    }
}
