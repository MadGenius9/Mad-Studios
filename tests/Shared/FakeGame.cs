using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MadModStudio.TestSupport;

/// <summary>
/// Builds a synthetic "7 Days to Die" installation for tests: stub game/Harmony assemblies compiled from source, real
/// config XML and localization. No proprietary files are used. The BCL assemblies come from the test runtime.
/// </summary>
public static class FakeGame
{
    public const string AssemblyCSharpSource = """
        using System;
        using System.Collections.Generic;

        public class VersionInformation
        {
            public enum EGameReleaseType { Alpha, V }
            public VersionInformation(EGameReleaseType type, int major, int minor, int build) { }
        }

        public static class Constants
        {
            public static readonly VersionInformation cVersionInformation = new VersionInformation(VersionInformation.EGameReleaseType.V, 2, 1, 7);
            public const string cVersion = "unused";
        }

        public class Mod { public string Name; }
        public interface IModApi { void InitMod(Mod _modInstance); }

        public class ItemStack { public int count; }

        public class Entity
        {
            public int entityId;
            public virtual void OnUpdateLive() { }
        }

        public class EntityAlive : Entity
        {
            public float Health;
            public bool IsDead() { return Health <= 0; }
        }

        public class EntityDrone : EntityAlive
        {
            public bool IsHome { get; set; }
            public void SendHome() { }
            public void SendHome(bool force) { }
            private void depositInventory(int slot) { }
            public ItemStack[] GetInventory() { return new ItemStack[0]; }
            public class DroneState { public int mode; }
        }

        public class TileEntity { }
        public class TileEntityLootContainer : TileEntity { public ItemStack[] items; public void SetModified() { } }

        public class GameManager
        {
            public static GameManager Instance;
            public void SaveWorld() { }
        }

        public static class Log
        {
            public static void Out(string s) { }
            public static void Warning(string s) { }
            public static void Error(string s) { }
        }
        """;

    /// <summary>An older game API used to build "outdated" mods: has a method that no longer exists.</summary>
    public const string OldAssemblyCSharpSource = AssemblyCSharpSource + """

        public static class OldApi { }
        public class EntityDroneLegacyHelper { }
        """;

    public const string HarmonySource = """
        using System;
        using System.Reflection;
        namespace HarmonyLib
        {
            public enum MethodType { Normal, Getter, Setter, Constructor, StaticConstructor, Enumerator, Async }
            public class Harmony
            {
                public Harmony(string id) { }
                public void PatchAll() { }
                public void PatchAll(Assembly assembly) { }
            }
            public class HarmonyAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Struct, AllowMultiple = true)]
            public class HarmonyPatch : HarmonyAttribute
            {
                public HarmonyPatch() { }
                public HarmonyPatch(Type declaringType) { }
                public HarmonyPatch(Type declaringType, string methodName) { }
                public HarmonyPatch(Type declaringType, string methodName, params Type[] argumentTypes) { }
                public HarmonyPatch(Type declaringType, MethodType methodType) { }
                public HarmonyPatch(Type declaringType, string methodName, MethodType methodType) { }
                public HarmonyPatch(string methodName) { }
                public HarmonyPatch(string methodName, MethodType methodType) { }
            }
            [AttributeUsage(AttributeTargets.Method)] public class HarmonyPrefix : HarmonyAttribute { }
            [AttributeUsage(AttributeTargets.Method)] public class HarmonyPostfix : HarmonyAttribute { }
            [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Method, AllowMultiple = true)]
            public class HarmonyArgument : Attribute { public HarmonyArgument(string originalName) { } public HarmonyArgument(int index) { } }
        }
        """;

    public const string BlocksXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <blocks>
          <block name="cntWoodWritableCrate">
            <property name="Class" value="Loot"/>
            <property name="LootList" value="playerWritableStorage"/>
          </block>
          <block name="cntStorageGeneric">
            <property name="Class" value="Loot"/>
          </block>
          <block name="woodShapes">
            <property name="Material" value="Mwood"/>
          </block>
        </blocks>
        """;

    public const string ItemsXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <items>
          <item name="resourceWood">
            <property name="Stacknumber" value="500"/>
          </item>
          <item name="gunBotT1JunkDrone">
            <property name="Class" value="ItemClassDrone"/>
          </item>
        </items>
        """;

    public const string LocalizationTxt = "Key,File,Type,UsedInMainMenu,NoTranslate,english\ncntWoodWritableCrate,blocks,Block,,,Wooden Writable Crate\nresourceWood,items,Item,,,Wood\n";

    private static readonly object Gate = new();
    private static string? _shared;

    /// <summary>A shared, lazily created fake install (do not modify it in tests).</summary>
    public static string Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared != null) return _shared;
                var dir = Path.Combine(Path.GetTempPath(), "mms-tests", "fakegame-" + Guid.NewGuid().ToString("N")[..8], "7 Days To Die");
                Create(dir);
                _shared = dir;
                return dir;
            }
        }
    }

    public static string ManagedPath(string root) => Path.Combine(root, "7DaysToDie_Data", "Managed");
    public static string HarmonyPath(string root) => Path.Combine(root, "Mods", "0_TFP_Harmony", "0Harmony.dll");

    public static void Create(string root, string? assemblyCSharpSource = null)
    {
        var managed = ManagedPath(root);
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(root, "7DaysToDie.exe"), "fake");

        foreach (var bcl in BclFiles())
            File.Copy(bcl, Path.Combine(managed, Path.GetFileName(bcl)), overwrite: true);

        var bclRefs = BclFiles().Select(f => MetadataReference.CreateFromFile(f)).ToList();
        Compile("Assembly-CSharp", assemblyCSharpSource ?? AssemblyCSharpSource, bclRefs, Path.Combine(managed, "Assembly-CSharp.dll"));

        var harmonyDir = Path.GetDirectoryName(HarmonyPath(root))!;
        Directory.CreateDirectory(harmonyDir);
        Compile("0Harmony", HarmonySource, bclRefs, HarmonyPath(root));
        File.WriteAllText(Path.Combine(harmonyDir, "ModInfo.xml"), """<?xml version="1.0"?><xml><Name value="0_TFP_Harmony"/><Version value="2.10.1"/></xml>""");

        var config = Path.Combine(root, "Data", "Config");
        Directory.CreateDirectory(Path.Combine(config, "XUi"));
        File.WriteAllText(Path.Combine(config, "blocks.xml"), BlocksXml);
        File.WriteAllText(Path.Combine(config, "items.xml"), ItemsXml);
        File.WriteAllText(Path.Combine(config, "Localization.txt"), LocalizationTxt);
        File.WriteAllText(Path.Combine(config, "XUi", "windows.xml"), """<windows><window name="windowLooting"><rect name="content"/></window></windows>""");
    }

    public static IEnumerable<string> BclFiles()
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var n in new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "mscorlib.dll", "netstandard.dll", "System.Collections.dll", "System.Linq.dll", "System.Console.dll" })
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) yield return p;
        }
    }

    public static void Compile(string assemblyName, string source, IEnumerable<MetadataReference> references, string outputPath) =>
        Compile(assemblyName, new[] { source }, references, outputPath);

    public static void Compile(string assemblyName, IEnumerable<string> sources, IEnumerable<MetadataReference> references, string outputPath)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            sources.Select(s => CSharpSyntaxTree.ParseText(s)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var result = compilation.Emit(outputPath);
        if (!result.Success)
            throw new InvalidOperationException($"Fixture compile of {assemblyName} failed: " + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }

    public static string TempDir(string name = "t")
    {
        var d = Path.Combine(Path.GetTempPath(), "mms-tests", name + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>A realistic Harmony/C# + XML mod with source, used to exercise import → compile → package.</summary>
public static class SampleMod
{
    public const string ModInfoXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xml>
          <Name value="MadWorkingRacks" />
          <DisplayName value="Mad Working Racks" />
          <Version value="1.0.8" />
          <Description value="Racks that work." />
          <Author value="MadGenius" />
          <Website value="" />
        </xml>
        """;

    public const string BlocksPatch = """
        <configs>
          <append xpath="/blocks">
            <block name="madWorkingRack">
              <property name="Extends" value="cntWoodWritableCrate"/>
            </block>
          </append>
          <set xpath="/blocks/block[@name='cntStorageGeneric']/property[@name='Class']/@value">Loot</set>
        </configs>
        """;

    public const string Localization = "Key,File,Type,UsedInMainMenu,NoTranslate,english\nmadWorkingRack,blocks,Block,,,Mad Working Rack\n";

    public const string InitSource = """
        using HarmonyLib;
        using System.Reflection;

        namespace MadWorkingRacks
        {
            public class ModInit : IModApi
            {
                public void InitMod(Mod _modInstance)
                {
                    var harmony = new Harmony("com.mad.workingracks");
                    harmony.PatchAll(Assembly.GetExecutingAssembly());
                    Log.Out("[MadWorkingRacks] loaded");
                }
            }
        }
        """;

    public const string PatchSource = """
        using HarmonyLib;

        namespace MadWorkingRacks
        {
            [HarmonyPatch(typeof(EntityDrone), nameof(EntityDrone.SendHome), new[] { typeof(bool) })]
            public class EntityDrone_SendHome_Patch
            {
                public static void Postfix(EntityDrone __instance)
                {
                    if (__instance.IsHome) Log.Out("Drone is home");
                }
            }

            [HarmonyPatch(typeof(EntityDrone), "depositInventory")]
            public class EntityDrone_Deposit_Patch
            {
                public static bool Prefix(int slot) { return slot >= 0; }
            }
        }
        """;

    public const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net48</TargetFramework>
            <AssemblyName>MadWorkingRacks</AssemblyName>
            <LangVersion>9.0</LangVersion>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>Writes the mod folder (with source) under <paramref name="parent"/> and returns the mod folder path.</summary>
    public static string WriteFolder(string parent, string version = "1.0.8", string? patchSource = null, string? blocksPatch = null)
    {
        var root = Path.Combine(parent, "MadWorkingRacks");
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        Directory.CreateDirectory(Path.Combine(root, "Harmony"));
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), ModInfoXml.Replace("1.0.8", version));
        File.WriteAllText(Path.Combine(root, "Config", "blocks.xml"), blocksPatch ?? BlocksPatch);
        File.WriteAllText(Path.Combine(root, "Config", "Localization.txt"), Localization);
        File.WriteAllText(Path.Combine(root, "Harmony", "ModInit.cs"), InitSource);
        File.WriteAllText(Path.Combine(root, "Harmony", "Patches.cs"), patchSource ?? PatchSource);
        File.WriteAllText(Path.Combine(root, "MadWorkingRacks.csproj"), Csproj);
        // Junk that must never be packaged.
        Directory.CreateDirectory(Path.Combine(root, ".vs"));
        File.WriteAllText(Path.Combine(root, ".vs", "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        File.WriteAllText(Path.Combine(root, "obj", "temp.cache"), "x");
        File.WriteAllText(Path.Combine(root, "build.log"), "log");
        return root;
    }

    public static string WriteZip(string parent, string version = "1.0.8", string? patchSource = null, string? blocksPatch = null)
    {
        var src = FakeGame.TempDir("modsrc");
        WriteFolder(src, version, patchSource, blocksPatch);
        Directory.CreateDirectory(parent);
        var zip = Path.Combine(parent, $"MadWorkingRacks_{version}.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zip;
    }
}
