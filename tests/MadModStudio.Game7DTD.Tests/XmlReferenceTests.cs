using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class XmlReferenceTests
{
    private static string WriteMod(Dictionary<string, string> configFiles)
    {
        var mod = Path.Combine(FakeGame.TempDir("refmod"), "RefMod");
        Directory.CreateDirectory(Path.Combine(mod, "Config"));
        File.WriteAllText(Path.Combine(mod, "ModInfo.xml"), """<xml><Name value="RefMod"/><DisplayName value="RefMod"/><Version value="1.0"/><Author value="a"/></xml>""");
        foreach (var (name, content) in configFiles) File.WriteAllText(Path.Combine(mod, "Config", name), content);
        return mod;
    }

    private static async Task<List<ValidationFinding>> Findings(TestHost host, string mod)
    {
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        var project = (await host.Get<ProjectService>().ImportAsync(mod, profile.Id)).Single().Project;
        return (await host.Get<RepairService>().DiagnoseAsync(project, new RepairInputs())).GameCompatibilityFindings;
    }

    [Fact]
    public async Task Recipes_must_reference_existing_items_and_names_are_case_sensitive()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["items.xml"] = """<configs><append xpath="/items"><item name="myNewTool"/></append></configs>""",
            ["recipes.xml"] = """
                <configs><append xpath="/recipes">
                  <recipe name="myNewTool" count="1"><ingredient name="resourceWood" count="5"/><ingredient name="resourceWoodd" count="1"/></recipe>
                  <recipe name="noSuchOutput" count="1"><ingredient name="ResourceWood" count="2"/></recipe>
                </append></configs>
                """,
        });

        var f = (await Findings(host, mod)).Where(x => x.ValidatorId == "xml-references").ToList();

        Assert.Contains(f, x => x.Message.StartsWith("Ingredient 'resourceWoodd'") && x.Line == 2);
        Assert.Contains(f, x => x.Message.StartsWith("Recipe output 'noSuchOutput'"));
        Assert.Contains(f, x => x.Message.StartsWith("Ingredient 'ResourceWood'") && x.Message.Contains("did you mean 'resourceWood'"));
        Assert.DoesNotContain(f, x => x.Message.Contains("'myNewTool'") || x.Message.Contains("'resourceWood' is")); // own item + game item resolve
        Assert.Equal(3, f.Count);
    }

    [Fact]
    public async Task Variant_helper_names_resolve_to_the_block_before_the_colon()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["items.xml"] = """<configs><append xpath="/items"><item name="questItem_x:VariantHelper"/></append></configs>""",
            ["recipes.xml"] = """<configs><append xpath="/recipes"><recipe name="cntStorageGeneric:VariantHelper" count="1"><ingredient name="resourceWood" count="1"/></recipe><recipe name="questItem_x:VariantHelper" count="1"/><recipe name="noBlock:VariantHelper" count="1"/></append></configs>""",
        });

        var f = (await Findings(host, mod)).Where(x => x.ValidatorId == "xml-references").ToList();

        Assert.Equal("Recipe output 'noBlock:VariantHelper' is not an item, block or item modifier defined by the game or this mod.", Assert.Single(f).Message.Split(" It may")[0]);
    }

    [Fact]
    public async Task Batch_scan_treats_names_from_sibling_mods_as_a_dependency_not_a_problem()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        var mods = FakeGame.TempDir("mods");
        void Mod(string name, string file, string xml)
        {
            Directory.CreateDirectory(Path.Combine(mods, name, "Config"));
            File.WriteAllText(Path.Combine(mods, name, "ModInfo.xml"), $"""<xml><Name value="{name}"/><DisplayName value="{name}"/><Version value="1.0"/><Author value="a"/></xml>""");
            File.WriteAllText(Path.Combine(mods, name, "Config", file), xml);
        }
        Mod("AmmoPack", "items.xml", """<configs><append xpath="/items"><item name="madAmmoFire"/></append></configs>""");
        Mod("SupplyFlare", "loot.xml", """<configs><append xpath="/lootcontainers"><lootgroup name="flare"><item name="madAmmoFire"/><item name="resourceHardenedSteel"/></lootgroup></append></configs>""");

        var rows = await host.Get<MadModStudio.Game7DTD.Scanner.BatchModScanner>().ScanAsync(mods, profile);

        var flare = rows.Single(r => r.Name == "SupplyFlare").Validation!.Findings.Where(x => x.ValidatorId == "xml-references").ToList();
        Assert.Contains(flare, x => x.Severity == Severity.Info && x.Message.StartsWith("Depends on mod 'AmmoPack'") && x.Message.Contains("madAmmoFire"));
        Assert.Contains(flare, x => x.Severity == Severity.Warning && x.Message.StartsWith("Loot item 'resourceHardenedSteel'"));
        Assert.DoesNotContain(flare, x => x.Severity == Severity.Warning && x.Message.Contains("madAmmoFire"));
    }

    [Fact]
    public async Task Loot_items_and_groups_must_exist()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["loot.xml"] = """
                <configs><append xpath="/lootcontainers">
                  <lootgroup name="myGroup"><item name="resourceWood" count="1"/><item name="cntWoodWritableCrate"/></lootgroup>
                  <lootgroup name="other"><item group="myGroup"/><item group="noSuchGroup"/><item name="missingThing"/></lootgroup>
                </append></configs>
                """,
        });

        var f = (await Findings(host, mod)).Where(x => x.ValidatorId == "xml-references").ToList();

        Assert.Equal(2, f.Count);
        Assert.Contains(f, x => x.Message.StartsWith("Loot group 'noSuchGroup' is not a lootgroup"));
        Assert.Contains(f, x => x.Message.StartsWith("Loot item 'missingThing'"));
    }

    [Fact]
    public async Task Unmatched_xpaths_explain_the_usual_mistake()
    {
        using var host = new TestHost();
        var mod = WriteMod(new()
        {
            ["items.xml"] = """
                <configs>
                  <set xpath="/items/item[@name='resourceWood']/@NoSuchAttribute">5</set>
                  <set xpath="/items/item[@name='ResourceWood']/property[@name='Stacknumber']/@value">9</set>
                  <remove xpath="/items/item[ends-with(@name,'Wood')]"/>
                </configs>
                """,
        });

        var f = (await Findings(host, mod)).Where(x => x.ValidatorId == "xml-xpath").ToList();

        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Message.Contains("<set> only changes existing attributes") && x.Message.Contains("<setattribute xpath=\"/items/item[@name='resourceWood']\" name=\"NoSuchAttribute\">"));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Message.Contains("the game names it 'resourceWood', not 'ResourceWood'"));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("XPath 1.0"));
    }
}
