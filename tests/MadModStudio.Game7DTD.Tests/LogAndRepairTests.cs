using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.Game7DTD.Scanner;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class LogAndRepairTests
{
    public const string SampleLog = """
        2026-10-01T20:11:02 12.345 INF [MODS] Loading from mods folder
        2026-10-01T20:11:02 12.401 INF [MODS] Loaded Mod: MadWorkingRacks (1.0.9)
        2026-10-01T20:11:03 13.002 ERR [MODS] Failed patching mod 'MadWorkingRacks': HarmonyLib.HarmonyException: Patching exception ---> System.ArgumentException: Undefined target method for patch method static System.Boolean MadWorkingRacks.EntityDrone_Deposit_Patch::Prefix(System.Int32 slot)
          at HarmonyLib.PatchClassProcessor.PatchWithAttributes (System.Reflection.MethodBase& lastOriginal) [0x00000] in <abc>:0
          at MadWorkingRacks.ModInit.InitMod (Mod _modInstance) [0x00012] in <def>:0
        2026-10-01T20:11:04 14.100 WRN XML patch for "blocks.xml" from mod "MadWorkingRacks" did not apply: <set xpath="/blocks/block[@name='cntStorageGenericRemoved']/property[@name='Class']/@value"> (line 8 at pos 4)
        2026-10-01T20:11:09 19.555 EXC Object reference not set to an instance of an object
        NullReferenceException: Object reference not set to an instance of an object
          at OtherMod.Thing.Update () [0x00000] in <x>:0
        2026-10-01T20:11:10 20.000 EXC MissingMethodException: Method not found: void EntityDrone.Recall(int)
        2026-10-01T20:11:11 20.000 EXC MissingMethodException: Method not found: void EntityDrone.Recall(int)
        2026-10-01T20:11:12 21.000 ERR Could not load file or assembly 'SomeLib, Version=1.0.0.0' or one of its dependencies.
        """;

    private static string WriteLog(string text = SampleLog)
    {
        var p = Path.Combine(FakeGame.TempDir("log"), "output_log_client.txt");
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void Parses_categories_stacks_and_groups()
    {
        var r = new LogParser().Parse(WriteLog());
        Assert.Equal("MadWorkingRacks (1.0.9)", Assert.Single(r.LoadedMods));
        var harmony = r.Entries.Single(e => e.Message.Contains("Undefined target method"));
        Assert.Equal(LogCategory.MissingMethod, harmony.Category);
        Assert.Equal(2, harmony.StackTrace.Count);
        Assert.Equal("MadWorkingRacks.ModInit", harmony.StackTrace[1].Type);
        Assert.Contains("MadWorkingRacks", harmony.ModNames);

        var xpath = r.Entries.Single(e => e.Category == LogCategory.XPath);
        Assert.Equal("/blocks/block[@name='cntStorageGenericRemoved']/property[@name='Class']/@value", xpath.XPath);
        Assert.Contains(xpath.FileReferences, f => f.File == "blocks.xml");

        Assert.Contains(r.Entries, e => e.Category == LogCategory.NullReference && e.StackTrace.Any(s => s.Method == "Update"));
        Assert.Contains(r.Entries, e => e.Category == LogCategory.AssemblyLoad);
        var missing = r.Groups.Single(g => g.Category == LogCategory.MissingMethod && g.First.Message.Contains("Recall"));
        Assert.Equal(2, missing.Count);
        Assert.Equal(r.Groups.Count, r.Groups.Select(g => g.Signature).Distinct().Count());
    }

    [Fact]
    public void Huge_log_is_streamed_with_entry_limits()
    {
        var path = Path.Combine(FakeGame.TempDir("biglog"), "big.txt");
        using (var w = new StreamWriter(path))
        {
            for (var i = 0; i < 200_000; i++)
                w.WriteLine($"2026-10-01T20:11:02 {i}.0 {(i % 50 == 0 ? "ERR" : "INF")} message number {i}");
        }
        var r = new LogParser { MaxEntries = 1000 }.Parse(path);
        Assert.Equal(200_000, r.TotalLines);
        Assert.Equal(1000, r.Entries.Count);
        Assert.True(r.Truncated);
        Assert.Single(r.Groups); // all "message number #" errors collapse into one group
        var hit = Assert.Single(LogParser.Search(path, "message number 123456", 10));
        Assert.Equal(123457, hit.Line);
        Assert.Equal(10, LogParser.Search(path, "message number 1", 10).Count); // result limit honoured
    }

    [Fact]
    public async Task Repair_diagnosis_correlates_log_with_files_and_detects_regression()
    {
        using var host = new TestHost();
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        var inbox = FakeGame.TempDir("inbox");
        var working = SampleMod.WriteZip(inbox, "1.0.8");
        var broken = SampleMod.WriteZip(Path.Combine(inbox, "b"), "1.0.9",
            patchSource: SampleMod.PatchSource.Replace("\"depositInventory\"", "\"depositAllInventory\""),
            blocksPatch: SampleMod.BlocksPatch.Replace("cntStorageGeneric", "cntStorageGenericRemoved"));
        var project = (await host.Get<ProjectService>().ImportAsync(broken, profile.Id)).Single().Project;

        var inputs = new RepairInputs { WorkingVersionPath = working };
        inputs.Logs.Add((WriteLog(), "client"));
        var d = await host.Get<RepairService>().DiagnoseAsync(project, inputs);

        Assert.Contains(d.LikelyCauses, c => c.StartsWith("LIKELY REGRESSION: Harmony/Patches.cs"));
        Assert.Contains(d.LikelyCauses, c => c.StartsWith("LIKELY REGRESSION: Config/blocks.xml"));
        Assert.Contains(d.GameCompatibilityFindings, f => f.Message.Contains("depositAllInventory") && f.Message.Contains("Similar methods: depositInventory"));
        Assert.Contains(d.Correlations, c => c.ProjectFile == "Config/blocks.xml" && c.Line == 7);
        Assert.Contains(d.Correlations, c => c.ProjectFile == "Harmony/Patches.cs");
        Assert.DoesNotContain(d.RelevantGroups, g => g.Category == Logs.LogCategory.NullReference); // other mod's error is not attributed
        Assert.Equal(3, d.Comparison!.Modified.Count());
        Assert.Contains("PROPOSED REPAIRS", d.Report);
    }

    [Fact]
    public async Task Batch_scanner_classifies_mods_without_modifying_them()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        var mods = FakeGame.TempDir("mods");
        var good = Path.Combine(mods, "GoodXml");
        Directory.CreateDirectory(Path.Combine(good, "Config"));
        File.WriteAllText(Path.Combine(good, "ModInfo.xml"), """<xml><Name value="GoodXml"/><DisplayName value="Good"/><Version value="1.0"/><Author value="a"/></xml>""");
        File.WriteAllText(Path.Combine(good, "Config", "items.xml"), """<configs><set xpath="/items/item[@name='resourceWood']/property[@name='Stacknumber']/@value">999</set></configs>""");
        var bad = Path.Combine(mods, "BadXml");
        Directory.CreateDirectory(Path.Combine(bad, "Config"));
        File.WriteAllText(Path.Combine(bad, "ModInfo.xml"), """<xml><Name value="BadXml"/><Version value="1.0"/></xml>""");
        File.WriteAllText(Path.Combine(bad, "Config", "items.xml"), "<configs><set>");
        var icons = Path.Combine(mods, "Icons");
        Directory.CreateDirectory(Path.Combine(icons, "UIAtlases", "ItemIconAtlas"));
        File.WriteAllText(Path.Combine(icons, "ModInfo.xml"), """<xml><Name value="Icons"/><Version value="1.0"/><Author value="a"/></xml>""");
        File.WriteAllBytes(Path.Combine(icons, "UIAtlases", "ItemIconAtlas", "x.png"), new byte[] { 1, 2, 3 });
        var before = Directory.GetFiles(mods, "*", SearchOption.AllDirectories).ToDictionary(f => f, MadModStudio.Core.IO.FileUtil.Sha256);

        var rows = await host.Get<BatchModScanner>().ScanAsync(mods, profile);

        Assert.Equal(ScanStatus.Compatible, rows.Single(r => r.Name == "Good").Status);
        Assert.Equal(ScanStatus.Broken, rows.Single(r => r.Name == "BadXml").Status);
        Assert.Equal(ScanStatus.ClientRequirementDetected, rows.Single(r => r.Name == "Icons").Status);
        Assert.Equal(before, Directory.GetFiles(mods, "*", SearchOption.AllDirectories).ToDictionary(f => f, MadModStudio.Core.IO.FileUtil.Sha256));
    }
}
