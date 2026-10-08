using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

/// <summary>V3.0 renamed Localization.txt to .csv and split Config/XUi into XUi_InGame/XUi_Menu/XUi_Common.</summary>
public class GameLayoutCompatibilityTests
{
    private const string LocHeader = "Key,File,Type,UsedInMainMenu,NoTranslate,KeepLoaded,english\n";
    private const string WindowsPatch = """<configs><append xpath="/windows"><window name="myWindow"/></append></configs>""";

    /// <summary>A game laid out like V3.0+: Localization.csv and XUi_InGame (no Localization.txt, no XUi).</summary>
    private static string NewLayoutGame()
    {
        var game = Path.Combine(FakeGame.TempDir("v3game"), "g");
        FakeGame.Create(game);
        var cfg = Path.Combine(game, "Data", "Config");
        File.Move(Path.Combine(cfg, "Localization.txt"), Path.Combine(cfg, "Localization.csv"));
        Directory.Move(Path.Combine(cfg, "XUi"), Path.Combine(cfg, "XUi_InGame"));
        return game;
    }

    private static string WriteMod(Dictionary<string, string> files)
    {
        var mod = Path.Combine(FakeGame.TempDir("layoutmod"), "LayoutMod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "ModInfo.xml"), """<xml><Name value="LayoutMod"/><DisplayName value="LayoutMod"/><Version value="1.0"/><Author value="a"/></xml>""");
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(mod, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return mod;
    }

    private static async Task<Diagnosis> Diagnose(TestHost host, string gameDir, string modDir)
    {
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(gameDir)).Profile!;
        await profiles.ReindexAsync(profile);
        var project = (await host.Get<ProjectService>().ImportAsync(modDir, profile.Id)).Single().Project;
        return await host.Get<RepairService>().DiagnoseAsync(project, new RepairInputs());
    }

    [Fact]
    public async Task Old_style_mod_on_a_v3_game_is_flagged_with_how_to_fix_it()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["Config/Localization.txt"] = LocHeader + "myItem,items,Item,,,,My Item\n",
            ["Config/XUi/windows.xml"] = WindowsPatch,
            ["Config/XUi/controls.xml"] = WindowsPatch,
        });

        var d = await Diagnose(host, NewLayoutGame(), mod);

        var warnings = d.GameCompatibilityFindings.Where(f => f.Severity == Severity.Warning).ToList();
        Assert.Contains(warnings, f => f.FilePath == "Config/Localization.txt" && f.Message.Contains("Rename it to Localization.csv"));
        Assert.Contains(warnings, f => f.FilePath == "Config/XUi/windows.xml" && f.Message.Contains("XUi_InGame") && f.Message.Contains("XUi_Menu"));
        Assert.Contains(warnings, f => f.FilePath == "Config/XUi/controls.xml" && f.Message.Contains("renamed templates.xml"));
        // The generic "file does not exist in the game" warning is replaced by the specific one, not repeated.
        Assert.DoesNotContain(warnings, f => f.FilePath == "Config/XUi/windows.xml" && f.Message.Contains("does not exist in the installed game"));
        Assert.Contains("Game version layout", d.Report);
    }

    [Fact]
    public async Task Modern_mod_on_a_v3_game_is_clean_and_its_csv_is_recognised()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["Config/Localization.csv"] = LocHeader + "myItem,items,Item,,,,My Item\n",
            ["Config/XUi_InGame/windows.xml"] = """<configs><append xpath="/windows"><window name="myWindow"/></append></configs>""",
        });

        var d = await Diagnose(host, NewLayoutGame(), mod);

        Assert.DoesNotContain(d.GameCompatibilityFindings, f => f.ValidatorId is "game-layout" or "localization");
        Assert.Contains("Config/Localization.csv", d.Analysis!.LocalizationFiles);
    }

    private const string LegacyModInfo = """<xml><ModInfo><Name value="OldMod"/><Description value="d"/><Author value="a"/><Version value="1.0"/></ModInfo></xml>""";

    private static string LegacyMod()
    {
        var mod = Path.Combine(FakeGame.TempDir("legacymod"), "OldMod");
        Directory.CreateDirectory(Path.Combine(mod, "Config"));
        File.WriteAllText(Path.Combine(mod, "ModInfo.xml"), LegacyModInfo);
        File.WriteAllText(Path.Combine(mod, "Config", "items.xml"), """<configs><set xpath="/items/item[@name='resourceWood']/property[@name='Stacknumber']/@value">999</set></configs>""");
        return mod;
    }

    [Fact]
    public async Task Legacy_modinfo_is_an_error_on_v3_because_the_game_refuses_to_load_it()
    {
        using var host = new TestHost();
        var v3 = Path.Combine(FakeGame.TempDir("v3ver"), "g");
        FakeGame.Create(v3, FakeGame.AssemblyCSharpSource.Replace("EGameReleaseType.V, 2, 1, 7", "EGameReleaseType.V, 3, 30, 18"));

        var d = await Diagnose(host, v3, LegacyMod());

        Assert.Contains(d.GameCompatibilityFindings, f => f.ValidatorId == "modinfo" && f.Severity == Severity.Error && f.Message.Contains("V2 required to load mod"));
    }

    [Fact]
    public async Task ModInfo_as_the_root_element_loads_fine_on_v3()
    {
        // Root <ModInfo> with fields directly under it: the game's V2 parser reads it (no nested <ModInfo> element).
        using var host = new TestHost();
        var v3 = Path.Combine(FakeGame.TempDir("v3ver"), "g");
        FakeGame.Create(v3, FakeGame.AssemblyCSharpSource.Replace("EGameReleaseType.V, 2, 1, 7", "EGameReleaseType.V, 3, 30, 18"));
        var mod = LegacyMod();
        File.WriteAllText(Path.Combine(mod, "ModInfo.xml"), """<?xml version="1.0"?><ModInfo><Name value="OldMod"/><DisplayName value="Old"/><Version value="1.0"/><Author value="a"/></ModInfo>""");

        var d = await Diagnose(host, v3, mod);

        Assert.DoesNotContain(d.GameCompatibilityFindings, f => f.ValidatorId == "modinfo" && f.Severity >= Severity.Warning);
    }

    [Fact]
    public async Task Legacy_modinfo_is_only_a_warning_on_an_older_game()
    {
        using var host = new TestHost();
        var d = await Diagnose(host, FakeGame.Shared, LegacyMod()); // V 2.1

        Assert.Contains(d.GameCompatibilityFindings, f => f.ValidatorId == "modinfo" && f.Severity == Severity.Warning && f.Message.Contains("legacy"));
        Assert.DoesNotContain(d.GameCompatibilityFindings, f => f.ValidatorId == "modinfo" && f.Severity == Severity.Error);
    }

    [Fact]
    public async Task New_style_mod_on_an_old_game_is_flagged()
    {
        using var host = new TestHost();
        var oldGame = Path.Combine(FakeGame.TempDir("v2game"), "g");
        FakeGame.Create(oldGame); // Localization.txt + Config/XUi
        var mod = WriteMod(new()
        {
            ["Config/Localization.csv"] = LocHeader + "myItem,items,Item,,,,My Item\n",
            ["Config/XUi_Menu/windows.xml"] = WindowsPatch,
        });

        var d = await Diagnose(host, oldGame, mod);

        Assert.Contains(d.GameCompatibilityFindings, f => f.ValidatorId == "game-layout" && f.FilePath == "Config/Localization.csv" && f.Message.Contains("reads Config/Localization.txt"));
        Assert.Contains(d.GameCompatibilityFindings, f => f.ValidatorId == "game-layout" && f.FilePath == "Config/XUi_Menu/windows.xml");
    }
}
