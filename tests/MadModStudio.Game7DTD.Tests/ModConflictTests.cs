using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Scanner;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class ModConflictTests
{
    private static string GameConfig => Path.Combine(FakeGame.Shared, "Data", "Config");

    private static ConflictInput Mod(string mods, string folder, string? blocksXml = null, string? localization = null, ModAnalysisReport? analysis = null)
    {
        var root = Path.Combine(mods, folder);
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        if (blocksXml != null) File.WriteAllText(Path.Combine(root, "Config", "blocks.xml"), blocksXml);
        if (localization != null) File.WriteAllText(Path.Combine(root, "Config", "Localization.txt"), localization);
        return new ConflictInput(root, folder, analysis);
    }

    [Fact]
    public void Differently_written_xpaths_to_the_same_game_value_conflict_and_the_later_folder_wins()
    {
        var mods = FakeGame.TempDir("mods");
        var a = Mod(mods, "A_Loot", """<configs><set xpath="/blocks/block[@name='cntWoodWritableCrate']/property[@name='LootList']/@value">bigLoot</set></configs>""");
        var b = Mod(mods, "B_Loot", """<configs><set xpath="//block[@name='cntWoodWritableCrate']/property[@name='LootList']/@value">tinyLoot</set></configs>""");

        var c = Assert.Single(ModConflictAnalyzer.Analyze(new[] { b, a }, GameConfig));
        Assert.Equal(ConflictKind.XmlSameValue, c.Kind);
        Assert.Equal(Severity.Warning, c.Severity);
        Assert.Equal(new[] { "A_Loot", "B_Loot" }, c.Mods);
        Assert.Contains("B_Loot's value applies last", c.Detail);
        Assert.Contains("property[@name='LootList'] @value", c.Summary);
    }

    [Fact]
    public void Setattribute_and_set_on_the_same_attribute_are_the_same_target()
    {
        var mods = FakeGame.TempDir("mods");
        var a = Mod(mods, "A", """<configs><setattribute xpath="/blocks/block[@name='woodShapes']/property[@name='Material']" name="value">Mstone</setattribute></configs>""");
        var b = Mod(mods, "B", """<configs><set xpath="/blocks/block[@name='woodShapes']/property[@name='Material']/@value">Mstone</set></configs>""");

        var c = Assert.Single(ModConflictAnalyzer.Analyze(new[] { a, b }, GameConfig));
        Assert.Equal(Severity.Info, c.Severity); // same value either way
    }

    [Fact]
    public void Removing_a_node_another_mod_patches_is_reported()
    {
        var mods = FakeGame.TempDir("mods");
        var remover = Mod(mods, "Cleanup", """<configs><remove xpath="/blocks/block[@name='cntStorageGeneric']" /></configs>""");
        var patcher = Mod(mods, "Tweaks", """<configs><set xpath="/blocks/block[@name='cntStorageGeneric']/property[@name='Class']/@value">Other</set></configs>""");

        var c = Assert.Single(ModConflictAnalyzer.Analyze(new[] { remover, patcher }, GameConfig));
        Assert.Equal(ConflictKind.XmlRemovedTarget, c.Kind);
        Assert.Contains("Cleanup removes /blocks/block[@name='cntStorageGeneric']", c.Summary);
        Assert.Contains("Tweaks", c.Summary);
    }

    [Fact]
    public void Two_mods_adding_the_same_named_block_conflict_but_different_names_do_not()
    {
        var mods = FakeGame.TempDir("mods");
        const string addRack = """<configs><append xpath="/blocks"><block name="rack"><property name="Extends" value="cntWoodWritableCrate"/></block></append></configs>""";
        var a = Mod(mods, "A", addRack);
        var b = Mod(mods, "B", addRack.Replace("Extends", "CreativeMode"));
        var c = Mod(mods, "C", addRack.Replace("\"rack\"", "\"otherRack\""));

        var conflict = Assert.Single(ModConflictAnalyzer.Analyze(new[] { a, b, c }, GameConfig));
        Assert.Equal(ConflictKind.XmlDuplicateDefinition, conflict.Kind);
        Assert.Equal(new[] { "A", "B" }, conflict.Mods);
    }

    [Fact]
    public void Independent_mods_have_no_conflicts()
    {
        var mods = FakeGame.TempDir("mods");
        var a = Mod(mods, "A", """<configs><set xpath="/blocks/block[@name='woodShapes']/property[@name='Material']/@value">Mstone</set></configs>""");
        var b = Mod(mods, "B", """<configs><set xpath="/blocks/block[@name='cntStorageGeneric']/property[@name='Class']/@value">Other</set></configs>""");
        Assert.Empty(ModConflictAnalyzer.Analyze(new[] { a, b }, GameConfig));
    }

    [Fact]
    public void Without_game_xml_only_identical_xpaths_are_compared()
    {
        var mods = FakeGame.TempDir("mods");
        const string xp = "/blocks/block[@name='woodShapes']/property[@name='Material']/@value";
        var a = Mod(mods, "A", $"""<configs><set xpath="{xp}">Mstone</set></configs>""");
        var b = Mod(mods, "B", $"""<configs><set xpath="{xp}">Mmetal</set></configs>""");
        var c = Mod(mods, "C", """<configs><set xpath="//block[@name='woodShapes']/property[@name='Material']/@value">Mglass</set></configs>""");

        var conflict = Assert.Single(ModConflictAnalyzer.Analyze(new[] { a, b, c }, gameConfigPath: null));
        Assert.Equal(new[] { "A", "B" }, conflict.Mods);
    }

    [Fact]
    public void Harmony_dll_name_and_localization_clashes()
    {
        var mods = FakeGame.TempDir("mods");
        ModAnalysisReport Report(string name, string patchKind, string libVersion)
        {
            var r = new ModAnalysisReport { ModInfo = new ModInfoData { Name = name } };
            r.HarmonyPatches.Add(new HarmonyPatchInfo { PatchClass = "P", PatchKind = patchKind, TargetType = "EntityDrone", TargetMethod = "SendHome" });
            r.Dlls.Add(new DllReport { RelativePath = "Shared.dll", Assembly = new AssemblyReport { Name = "SharedLib", Version = libVersion } });
            return r;
        }
        var a = Mod(mods, "A", localization: "Key,english\nmyKey,Hello\n", analysis: Report("SameName", "Prefix", "1.0.0.0"));
        var b = Mod(mods, "B", localization: "Key,english\nmyKey,Goodbye\n", analysis: Report("SameName", "Postfix", "2.0.0.0"));

        var conflicts = ModConflictAnalyzer.Analyze(new[] { a, b }, GameConfig);
        Assert.Equal(ConflictKind.DuplicateModName, conflicts[0].Kind); // errors sort first
        Assert.Equal(Severity.Error, conflicts[0].Severity);
        var harmony = Assert.Single(conflicts, c => c.Kind == ConflictKind.HarmonySameMethod);
        Assert.Equal(Severity.Warning, harmony.Severity); // a prefix is involved
        Assert.Contains("EntityDrone", harmony.Summary);
        var dll = Assert.Single(conflicts, c => c.Kind == ConflictKind.DuplicateAssembly);
        Assert.Equal(Severity.Warning, dll.Severity); // different versions
        Assert.Single(conflicts, c => c.Kind == ConflictKind.LocalizationKey && c.Summary.Contains("myKey"));
    }

    [Fact]
    public void Postfix_only_overlap_is_informational()
    {
        var mods = FakeGame.TempDir("mods");
        ModAnalysisReport Report()
        {
            var r = new ModAnalysisReport();
            r.HarmonyPatches.Add(new HarmonyPatchInfo { PatchClass = "P", PatchKind = "Postfix", TargetType = "Some.Namespace.EntityDrone", TargetMethod = "SendHome" });
            return r;
        }
        var c = Assert.Single(ModConflictAnalyzer.Analyze(new[] { Mod(mods, "A", analysis: Report()), Mod(mods, "B", analysis: Report()) }, null));
        Assert.Equal(Severity.Info, c.Severity);
    }
}
