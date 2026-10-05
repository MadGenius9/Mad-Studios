using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;
using MadModStudio.TestSupport;
using Microsoft.CodeAnalysis;

namespace MadModStudio.Game7DTD.Tests;

public class AssemblyMetadataTests
{
    private static string CompileMod(params string[] sources)
    {
        var refs = FakeGame.BclFiles()
            .Append(Path.Combine(FakeGame.ManagedPath(FakeGame.Shared), "Assembly-CSharp.dll"))
            .Append(FakeGame.HarmonyPath(FakeGame.Shared))
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f));
        var path = Path.Combine(FakeGame.TempDir("dll"), "TestMod.dll");
        FakeGame.Compile("TestMod", sources, refs, path);
        return path;
    }

    [Fact]
    public void Reads_metadata_and_harmony_patches_without_loading()
    {
        var dll = CompileMod(SampleMod.InitSource, SampleMod.PatchSource, """

            namespace MadWorkingRacks
            {
                [HarmonyLib.HarmonyPatch(typeof(EntityDrone), "IsHome", HarmonyLib.MethodType.Getter)]
                public class Getter_Patch { public static void Postfix(ref bool __result) { } }
                public class NotAPatch { public static void Prefix() { } }
            }
            """);

        var report = new AssemblyInspector().Inspect(dll);

        Assert.True(report.Success, report.ReadError);
        Assert.Equal("TestMod", report.Name);
        Assert.Equal("0.0.0.0", report.Version);
        Assert.Contains(report.References, r => r.Name == "Assembly-CSharp");
        Assert.True(report.ReferencesHarmony);
        Assert.Contains("MadWorkingRacks", report.Namespaces);
        var init = report.Types.Single(t => t.FullName == "MadWorkingRacks.ModInit");
        Assert.Contains(init.Members, m => m.Signature == "void InitMod(Mod _modInstance)");
        Assert.Contains("MadWorkingRacks.ModInit", report.ModApiEntryPoints);

        Assert.Equal(3, report.HarmonyPatches.Count);
        var send = report.HarmonyPatches.Single(p => p.TargetMethod == "SendHome");
        Assert.Equal("EntityDrone", send.TargetType);
        Assert.Equal("Postfix", send.PatchKind);
        Assert.Equal(new[] { "System.Boolean" }, send.ArgumentTypes);
        var getter = report.HarmonyPatches.Single(p => p.PatchClass.EndsWith("Getter_Patch"));
        Assert.Equal("Getter", getter.MethodType);
        Assert.Equal("EntityDrone.get_IsHome", getter.TargetDisplay);
        Assert.DoesNotContain(report.HarmonyPatches, p => p.PatchClass.EndsWith("NotAPatch"));

        Assert.Contains(report.ExternalMembers, m => m.Assembly == "Assembly-CSharp" && m.DeclaringType == "Log" && m.Name == "Out");

        // The file was only read as data: it can be deleted immediately (not locked by a load context).
        File.Delete(dll);
    }

    [Fact]
    public void Unreadable_files_produce_errors_not_exceptions()
    {
        var dir = FakeGame.TempDir("bad");
        var garbage = Path.Combine(dir, "garbage.dll");
        File.WriteAllBytes(garbage, new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02 });
        var r = new AssemblyInspector().Inspect(garbage);
        Assert.False(r.Success);
        Assert.NotNull(r.ReadError);

        var missing = new AssemblyInspector().Inspect(Path.Combine(dir, "nope.dll"));
        Assert.Equal("File not found.", missing.ReadError);
    }

    [Fact]
    public void Source_scanner_finds_patches_with_file_and_line()
    {
        var patches = new SourceHarmonyScanner().ScanText(SampleMod.PatchSource, "Harmony/Patches.cs");
        Assert.Equal(2, patches.Count);
        var deposit = patches.Single(p => p.TargetMethod == "depositInventory");
        Assert.Equal("EntityDrone", deposit.TargetType);
        Assert.Equal("Prefix", deposit.PatchKind);
        Assert.Equal("Source", deposit.Origin);
        Assert.Equal(17, deposit.Line);
        var send = patches.Single(p => p.TargetMethod == "SendHome");
        Assert.Equal(new[] { "bool" }, send.ArgumentTypes);
    }
}
