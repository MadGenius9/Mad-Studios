using MadModStudio.Game7DTD.Install;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class GameInstallTests
{
    private readonly GameInstallLocator _locator = new();

    [Fact]
    public void Valid_install_is_detected_without_fixed_path_assumptions()
    {
        var v = _locator.Validate(FakeGame.Shared);
        Assert.True(v.IsValid, string.Join("; ", v.Errors));
        Assert.Equal(FakeGame.ManagedPath(FakeGame.Shared), v.Install!.ManagedPath);
        Assert.EndsWith(Path.Combine("Data", "Config"), v.Install.ConfigPath);
        Assert.True(v.Install.ModsFolderExists);
        Assert.False(v.Install.IsDedicatedServer);
    }

    [Fact]
    public void Selecting_the_managed_folder_resolves_the_install_root()
    {
        var v = _locator.Validate(FakeGame.ManagedPath(FakeGame.Shared));
        Assert.True(v.IsValid);
        Assert.Equal(Path.GetFullPath(FakeGame.Shared), v.Install!.InstallPath);
        Assert.Contains(v.Notes, n => n.Contains("detected from the selected folder"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/definitely/not/a/real/folder/7dtd")]
    public void Missing_paths_fail_gracefully(string path)
    {
        var v = _locator.Validate(path);
        Assert.False(v.IsValid);
        Assert.NotEmpty(v.Errors);
    }

    [Fact]
    public void Folder_without_game_files_is_rejected()
    {
        var dir = FakeGame.TempDir("notgame");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "hi");
        var v = _locator.Validate(dir);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("Assembly-CSharp.dll"));
    }

    [Fact]
    public void Corrupt_assembly_is_reported()
    {
        var root = FakeGame.TempDir("corruptgame");
        var managed = FakeGame.ManagedPath(root);
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "not a PE file");
        Directory.CreateDirectory(Path.Combine(root, "Data", "Config"));
        File.WriteAllText(Path.Combine(root, "Data", "Config", "blocks.xml"), "<blocks/>");
        var v = _locator.Validate(root);
        Assert.False(v.IsValid);
        Assert.Contains(v.Errors, e => e.Contains("could not be read as a managed assembly"));
    }

    [Fact]
    public void Version_is_read_from_game_constants_and_steam_manifest()
    {
        var install = _locator.Validate(FakeGame.Shared).Install!;
        var info = new GameVersionDetector(new DecompilerService()).Detect(install);
        Assert.Equal("V 2.1 (b7)", info.Version);
        Assert.Contains("Constants.cVersionInformation", info.Source);
    }

    [Theory]
    [InlineData("public static readonly VersionInformation cVersionInformation = new VersionInformation(VersionInformation.EGameReleaseType.V, 1, 4, 8);", "V 1.4 (b8)")]
    [InlineData("cVersionInformation = new VersionInformation(VersionInformation.EGameReleaseType.Alpha, 21, 2, 30);", "Alpha 21.2 (b30)")]
    [InlineData("public static string Something = \"x\";", null)]
    public void Constants_parser(string text, string? expected) =>
        Assert.Equal(expected, GameVersionDetector.ParseConstants(text));
}
