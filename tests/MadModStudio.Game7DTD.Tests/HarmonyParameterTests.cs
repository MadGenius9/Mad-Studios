using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;
using MadModStudio.TestSupport;
using Microsoft.CodeAnalysis;

namespace MadModStudio.Game7DTD.Tests;

/// <summary>Harmony resolves Prefix/Postfix parameters by name; names it can't resolve make the patch fail to apply.</summary>
public class HarmonyParameterTests
{
    // One patch per class so each finding can be attributed. Targets are FakeGame's API:
    // EntityDrone.depositInventory(int slot) is private and void; EntityAlive has a field Health and bool IsDead();
    // Log.Out(string s) is static.
    private const string Patches = """
        using HarmonyLib;
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class Good { public static void Prefix(EntityDrone __instance, int slot, int __0, float ___Health, bool __runOriginal, object[] __args) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class Remapped { public static void Prefix([HarmonyArgument("slot")] int whichSlot) { } }
        [HarmonyPatch(typeof(EntityAlive), "IsDead")]
        public class ResultOk { public static void Postfix(ref bool __result) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class RenamedParam { public static void Prefix(int _slot) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class MissingField { public static void Postfix(int ___inventoryCount) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class VoidResult { public static void Postfix(bool __result) { } }
        [HarmonyPatch(typeof(Log), "Out")]
        public class StaticInstance { public static void Prefix(object __instance) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class BadIndex { public static void Prefix(int __3) { } }
        [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
        public class InjectionTypo { public static void Prefix(EntityDrone __Instance) { } }
        """;

    private static async Task<IGameKnowledgeIndex> Index(TestHost host)
    {
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        return profiles.GetIndex(profile)!;
    }

    private static Dictionary<string, List<ValidationFinding>> Check(IEnumerable<HarmonyPatchInfo> patches, IGameKnowledgeIndex index)
    {
        var validator = new HarmonyTargetValidator(new AssemblyInspector());
        return patches.ToDictionary(p => p.PatchClass, p =>
        {
            var f = new List<ValidationFinding>();
            validator.Check(p, index, f);
            return f.Where(x => x.Severity >= Severity.Warning).ToList();
        });
    }

    private static void AssertFindings(Dictionary<string, List<ValidationFinding>> byClass)
    {
        Assert.Empty(byClass["Good"]);
        Assert.Empty(byClass["Remapped"]);
        Assert.Empty(byClass["ResultOk"]);
        Assert.Contains("parameter '_slot' doesn't match any parameter of EntityDrone.depositInventory", Assert.Single(byClass["RenamedParam"]).Message);
        Assert.Contains("The game's parameter names are: slot", byClass["RenamedParam"][0].Message);
        Assert.Contains("asks Harmony for the field 'inventoryCount'", Assert.Single(byClass["MissingField"]).Message);
        Assert.Contains("'__result' needs a return value", Assert.Single(byClass["VoidResult"]).Message);
        Assert.Contains("'__instance' is only available when the patched method is not static", Assert.Single(byClass["StaticInstance"]).Message);
        Assert.Contains("'__3' is argument index 3, but EntityDrone.depositInventory takes 1", Assert.Single(byClass["BadIndex"]).Message);
        Assert.Contains("not one of Harmony's injections", Assert.Single(byClass["InjectionTypo"]).Message);
        Assert.All(byClass.Values.SelectMany(f => f), f => Assert.Equal(Severity.Error, f.Severity));
    }

    [Fact]
    public async Task Patch_parameters_from_source_are_checked_against_the_game()
    {
        using var host = new TestHost();
        var index = await Index(host);

        AssertFindings(Check(new SourceHarmonyScanner().ScanText(Patches, "Patches.cs"), index));
    }

    [Fact]
    public async Task Patch_parameters_from_a_compiled_dll_are_checked_against_the_game()
    {
        using var host = new TestHost();
        var index = await Index(host);
        var refs = FakeGame.BclFiles().Concat(new[] { Path.Combine(FakeGame.ManagedPath(FakeGame.Shared), "Assembly-CSharp.dll"), FakeGame.HarmonyPath(FakeGame.Shared) })
            .Select(f => MetadataReference.CreateFromFile(f));
        var dll = Path.Combine(FakeGame.TempDir("patchdll"), "Patches.dll");
        FakeGame.Compile("Patches", Patches, refs, dll);

        var report = new AssemblyInspector().Inspect(dll);

        Assert.True(report.Success, report.ReadError);
        AssertFindings(Check(report.HarmonyPatches, index));
    }

    [Theory]
    [InlineData("int Foo(int a, Dictionary<string, int> map, ref float b)", "a,map,b")]
    [InlineData("static void Bar()", "")]
    [InlineData("Baz(int)", null)] // unnamed parameter: unknown
    public void Parameter_names_are_parsed_from_index_signatures(string signature, string? expected) =>
        Assert.Equal(expected, HarmonyTargetValidator.ParameterNames(signature) is { } n ? string.Join(",", n) : null);
}
