using System.IO.Compression;
using MadModStudio.Game7DTD.Install;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class GameProfileZipTests
{
    private static string ZipGame(bool wrapInFolder)
    {
        var game = Path.Combine(FakeGame.TempDir("zipgame"), "7 Days To Die");
        FakeGame.Create(game);
        var zip = Path.Combine(FakeGame.TempDir("zipout"), "game.zip");
        // includeBaseDirectory: true puts everything under a "7 Days To Die" folder inside the archive.
        ZipFile.CreateFromDirectory(game, zip, CompressionLevel.Fastest, includeBaseDirectory: wrapInFolder);
        return zip;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_from_zip_is_extracted_under_app_data_and_removed_with_the_profile(bool wrapped)
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var zip = ZipGame(wrapped);

        var result = await svc.CreateProfileFromZipAsync(zip);

        Assert.NotNull(result.Profile);
        var profile = result.Profile!;
        Assert.StartsWith(Path.GetFullPath(host.Paths.GameInstalls), Path.GetFullPath(profile.InstallPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(profile.ManagedPath!, "Assembly-CSharp.dll")));
        Assert.True(svc.IsManagedCopy(profile));
        Assert.True((await svc.ReindexAsync(profile)).Success);
        var copyRoot = Directory.GetDirectories(host.Paths.GameInstalls).Single();

        await svc.DeleteAsync(profile);

        Assert.False(Directory.Exists(copyRoot));
        Assert.True(File.Exists(zip)); // the user's archive is never touched
    }

    public enum Loose { DllsAndConfigFolders, AllAtTopLevel, NamedFolders }

    /// <summary>A ZIP holding only the game's DLLs and config files, in a non-standard layout.</summary>
    private static string ZipLoose(Loose layout)
    {
        var game = Path.Combine(FakeGame.TempDir("zipgame"), "g");
        FakeGame.Create(game);
        var stage = FakeGame.TempDir("loose");
        var managed = FakeGame.ManagedPath(game);
        var config = Path.Combine(game, "Data", "Config");
        var (dllDir, cfgDir) = layout switch
        {
            Loose.AllAtTopLevel => (stage, stage),
            Loose.NamedFolders => (Path.Combine(stage, "Managed"), Path.Combine(stage, "Config")),
            _ => (Path.Combine(stage, "dlls"), Path.Combine(stage, "xml")),
        };
        Directory.CreateDirectory(dllDir);
        Directory.CreateDirectory(cfgDir);
        foreach (var f in Directory.GetFiles(managed)) File.Copy(f, Path.Combine(dllDir, Path.GetFileName(f)));
        foreach (var f in Directory.GetFiles(config)) File.Copy(f, Path.Combine(cfgDir, Path.GetFileName(f)));
        Directory.CreateDirectory(Path.Combine(cfgDir, "XUi"));
        File.Copy(Path.Combine(config, "XUi", "windows.xml"), Path.Combine(cfgDir, "XUi", "windows.xml"));
        var zip = Path.Combine(FakeGame.TempDir("zipout"), "loose.zip");
        ZipFile.CreateFromDirectory(stage, zip);
        return zip;
    }

    [Theory]
    [InlineData(Loose.DllsAndConfigFolders)]
    [InlineData(Loose.AllAtTopLevel)]
    [InlineData(Loose.NamedFolders)]
    public async Task Zip_with_only_dlls_and_config_files_works_in_any_layout(Loose layout)
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();

        var result = await svc.CreateProfileFromZipAsync(ZipLoose(layout));

        Assert.True(result.Profile != null, string.Join("; ", result.Validation.Errors));
        var profile = result.Profile!;
        Assert.True(File.Exists(Path.Combine(profile.ManagedPath!, "Assembly-CSharp.dll")));
        Assert.True(File.Exists(Path.Combine(profile.ConfigPath!, "blocks.xml")));
        var index = await svc.ReindexAsync(profile);
        Assert.True(index.Success, index.Error);
        Assert.NotNull(svc.GetIndex(profile)!.GetType("EntityDrone"));
        Assert.Contains(svc.GetIndex(profile)!.SearchXml("cntWoodWritableCrate"), x => x.File == "blocks.xml");
    }

    [Fact]
    public async Task Separate_managed_and_config_zips_combine_into_one_profile()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var game = Path.Combine(FakeGame.TempDir("zipgame"), "g");
        FakeGame.Create(game);
        var out1 = FakeGame.TempDir("zipout");

        // Managed.zip -> Managed/*.dll
        var s1 = FakeGame.TempDir("s1");
        Directory.CreateDirectory(Path.Combine(s1, "Managed"));
        foreach (var f in Directory.GetFiles(FakeGame.ManagedPath(game))) File.Copy(f, Path.Combine(s1, "Managed", Path.GetFileName(f)));
        var managedZip = Path.Combine(out1, "Managed.zip");
        ZipFile.CreateFromDirectory(s1, managedZip);

        // Config.zip -> Config/*.xml, XUi_*/ folders and Localization.csv (as shipped in the user's real archive)
        var s2 = FakeGame.TempDir("s2");
        var cfg = Path.Combine(s2, "Config");
        Directory.CreateDirectory(Path.Combine(cfg, "XUi_Menu"));
        var src = Path.Combine(game, "Data", "Config");
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(cfg, Path.GetFileName(f) == "Localization.txt" ? "Localization.csv" : Path.GetFileName(f)));
        File.Copy(Path.Combine(src, "XUi", "windows.xml"), Path.Combine(cfg, "XUi_Menu", "windows.xml"));
        var configZip = Path.Combine(out1, "Config.zip");
        ZipFile.CreateFromDirectory(s2, configZip);

        // Config alone is not an install, and says what is missing.
        var configOnly = await svc.CreateProfileFromZipAsync(configZip);
        Assert.Null(configOnly.Profile);
        Assert.Contains(configOnly.Validation.Errors, e => e.Contains("Assembly-CSharp.dll") && e.Contains("select all of them together"));
        Assert.Empty(Directory.GetDirectories(host.Paths.GameInstalls));

        var result = await svc.CreateProfileFromZipsAsync(new[] { managedZip, configZip });

        Assert.True(result.Profile != null, string.Join("; ", result.Validation.Errors));
        var index = await svc.ReindexAsync(result.Profile!);
        Assert.True(index.Success, index.Error);
        var idx = svc.GetIndex(result.Profile!)!;
        Assert.NotNull(idx.GetType("EntityDrone"));
        Assert.Contains(idx.SearchXml("cntWoodWritableCrate"), x => x.File == "blocks.xml");
        Assert.True(idx.LocalizationKeyExists("resourceWood")); // read from Localization.csv
    }

    [Fact]
    public async Task Zip_without_a_game_reports_an_error_and_leaves_nothing_behind()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var src = FakeGame.TempDir("notagame");
        File.WriteAllText(Path.Combine(src, "readme.txt"), "not a game");
        var zip = Path.Combine(FakeGame.TempDir("zipout"), "x.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var result = await svc.CreateProfileFromZipAsync(zip);

        Assert.Null(result.Profile);
        Assert.Contains(result.Validation.Errors, e => e.Contains("No 7 Days to Die installation was found"));
        Assert.Empty(Directory.GetDirectories(host.Paths.GameInstalls));
    }

    [Fact]
    public async Task Corrupt_zip_reports_an_error_instead_of_throwing()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var zip = Path.Combine(FakeGame.TempDir("zipout"), "bad.zip");
        File.WriteAllText(zip, "this is not a zip");

        var result = await svc.CreateProfileFromZipAsync(zip);

        Assert.Null(result.Profile);
        Assert.Contains(result.Validation.Errors, e => e.Contains("not a valid ZIP"));
        Assert.Empty(Directory.GetDirectories(host.Paths.GameInstalls));
    }

    [Fact]
    public async Task Folder_profiles_are_not_treated_as_managed_copies()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var profile = (await svc.CreateProfileAsync(FakeGame.Shared)).Profile!;

        Assert.False(svc.IsManagedCopy(profile));
    }
}
