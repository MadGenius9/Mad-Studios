using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;
using MadModStudio.TestSupport;
using Microsoft.CodeAnalysis;

namespace MadModStudio.Game7DTD.Tests;

public class ValidatorTests : IClassFixture<ValidatorTests.IndexedGame>
{
    public sealed class IndexedGame : IDisposable
    {
        public IndexedGame()
        {
            Host = new TestHost();
            var svc = Host.Get<GameProfileService>();
            Profile = svc.CreateProfileAsync(FakeGame.Shared).Result.Profile!;
            svc.ReindexAsync(Profile).Wait();
            Index = svc.GetIndex(Profile)!;
        }
        public TestHost Host { get; }
        public GameProfile Profile { get; }
        public Core.Abstractions.IGameKnowledgeIndex Index { get; }
        public void Dispose() => Host.Dispose();
    }

    private readonly IndexedGame _game;
    public ValidatorTests(IndexedGame game) => _game = game;

    private ValidationContext Ctx(string root) => new() { ModRootPath = root, GameProfile = _game.Profile, GameIndex = _game.Index };

    private static string ModDir(string modInfo = """<xml><Name value="T"/><DisplayName value="T"/><Version value="1.0"/><Author value="a"/></xml>""")
    {
        var d = Path.Combine(FakeGame.TempDir("vmod"), "T");
        Directory.CreateDirectory(Path.Combine(d, "Config"));
        File.WriteAllText(Path.Combine(d, "ModInfo.xml"), modInfo);
        return d;
    }

    [Fact]
    public async Task ModInfo_missing_version_is_an_error_and_legacy_layout_a_warning()
    {
        var noVersion = ModDir("""<xml><Name value="T"/></xml>""");
        var f = await new ModInfoValidator().ValidateAsync(Ctx(noVersion));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("no Version"));

        var legacy = ModDir("""<xml><ModInfo><Name value="T"/><Version value="1.0"/></ModInfo></xml>""");
        f = await new ModInfoValidator().ValidateAsync(Ctx(legacy));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Message.Contains("legacy"));

        var missing = FakeGame.TempDir("nomodinfo");
        f = await new ModInfoValidator().ValidateAsync(Ctx(missing));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("missing"));
    }

    [Fact]
    public async Task Malformed_xml_is_an_error_with_line()
    {
        var d = ModDir();
        File.WriteAllText(Path.Combine(d, "Config", "items.xml"), "<configs>\n<append xpath=\"/items\">\n</configs>");
        var f = await new XmlWellFormedValidator().ValidateAsync(Ctx(d));
        var err = Assert.Single(f, x => x.Severity == Severity.Error);
        Assert.Equal("Config/items.xml", err.FilePath);
        Assert.Equal(3, err.Line);
    }

    [Fact]
    public async Task XPath_targets_are_evaluated_against_installed_game_xml()
    {
        var d = ModDir();
        File.WriteAllText(Path.Combine(d, "Config", "blocks.xml"), """
            <configs>
              <set xpath="/blocks/block[@name='cntWoodWritableCrate']/property[@name='Class']/@value">Loot</set>
              <set xpath="/blocks/block[@name='doesNotExist']/@name">x</set>
              <append xpath="/blocks"><block name="myBlock"/></append>
              <set xpath="/blocks/block[@name='myBlock']/@name">y</set>
              <remove xpath="/blocks/block[@name='bad'"/>
            </configs>
            """);
        File.WriteAllText(Path.Combine(d, "Config", "nosuchfile.xml"), """<configs><set xpath="/x">1</set></configs>""");

        var f = await new XmlPatchTargetValidator().ValidateAsync(Ctx(d));

        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Message.Contains("doesNotExist") && x.Line == 3);
        Assert.Contains(f, x => x.Severity == Severity.Info && x.Message.Contains("content this mod adds"));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("Invalid XPath"));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.FilePath == "Config/nosuchfile.xml");
        Assert.DoesNotContain(f, x => x.Message.Contains("cntWoodWritableCrate"));
    }

    [Fact]
    public async Task Localization_header_and_duplicates()
    {
        var d = ModDir();
        File.WriteAllText(Path.Combine(d, "Config", "Localization.txt"), "Key,english\nfoo,Foo\nfoo,Foo again\n");
        File.WriteAllText(Path.Combine(d, "Config", "items.xml"), """<configs><append xpath="/items"><item name="newThing"/></append></configs>""");
        var f = await new LocalizationValidator().ValidateAsync(Ctx(d));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Message.Contains("Duplicate localization key 'foo'"));
        Assert.Contains(f, x => x.Severity == Severity.Info && x.Message.Contains("'newThing'"));

        File.WriteAllText(Path.Combine(d, "Config", "Localization.txt"), "name,english\nfoo,Foo\n");
        f = await new LocalizationValidator().ValidateAsync(Ctx(d));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("'Key'"));
    }

    [Fact]
    public async Task Harmony_targets_are_resolved_against_the_index()
    {
        var v = new HarmonyTargetValidator(new AssemblyInspector());
        var findings = new List<ValidationFinding>();
        v.Check(new HarmonyPatchInfo { PatchClass = "P1", TargetType = "EntityDrone", TargetMethod = "depositInventory" }, _game.Index, findings);
        Assert.Empty(findings);

        v.Check(new HarmonyPatchInfo { PatchClass = "P2", TargetType = "EntityDrone", TargetMethod = "depositInventorys" }, _game.Index, findings);
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("Similar methods: depositInventory"));

        findings.Clear();
        v.Check(new HarmonyPatchInfo { PatchClass = "P3", TargetType = "EntityDrone", TargetMethod = "SendHome" }, _game.Index, findings);
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Message.Contains("overloads"));

        findings.Clear();
        v.Check(new HarmonyPatchInfo { PatchClass = "P4", TargetType = "EntityDrone", TargetMethod = "SendHome", ArgumentTypes = new[] { "bool", "int" } }, _game.Index, findings);
        Assert.Contains(findings, f => f.Severity == Severity.Error && f.Message.Contains("2 parameter"));

        findings.Clear();
        v.Check(new HarmonyPatchInfo { PatchClass = "P5", TargetType = "EntityRobot", TargetMethod = "X" }, _game.Index, findings);
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Message.Contains("not found"));

        findings.Clear();
        v.Check(new HarmonyPatchInfo { PatchClass = "P6", TargetType = "EntityDrone", TargetMethod = "IsHome", MethodType = "Getter" }, _game.Index, findings);
        Assert.Empty(findings);
    }

    [Fact]
    public async Task Dll_compiled_against_an_older_game_reports_missing_api()
    {
        // Build a mod against an "older" Assembly-CSharp that still had EntityDrone.Recall(), then validate against the current game.
        var oldGame = FakeGame.TempDir("oldgame");
        var oldSource = FakeGame.AssemblyCSharpSource.Replace("public void SendHome() { }", "public void SendHome() { }\n    public void Recall(int delay) { }\n    public int legacyField;");
        FakeGame.Create(oldGame, oldSource);
        var refs = FakeGame.BclFiles().Append(Path.Combine(FakeGame.ManagedPath(oldGame), "Assembly-CSharp.dll"))
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(f));
        var d = ModDir();
        FakeGame.Compile("OldMod", """
            public class OldUser
            {
                public void Run(EntityDrone d) { d.Recall(5); d.legacyField = 1; d.SendHome(); Log.Out("ok"); }
            }
            """, refs, Path.Combine(d, "OldMod.dll"));

        var f = await new GameApiReferenceValidator(new AssemblyInspector()).ValidateAsync(Ctx(d));

        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("EntityDrone.Recall") && x.Message.Contains("MissingMethodException"));
        Assert.Contains(f, x => x.Severity == Severity.Error && x.Message.Contains("EntityDrone.legacyField"));
        Assert.DoesNotContain(f, x => x.Message.Contains("SendHome") || x.Message.Contains("Log.Out"));
    }

    [Fact]
    public async Task Dll_targeting_modern_dotnet_is_flagged()
    {
        var d = ModDir();
        // A DLL referencing System.Runtime 99.0 cannot be satisfied by the game's runtime.
        var fakeRuntime = FakeGame.TempDir("rt");
        FakeGame.Compile("System.Runtime.Future", "namespace System.Future { public class Thing {} }",
            FakeGame.BclFiles().Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)), Path.Combine(fakeRuntime, "System.Runtime.Future.dll"));
        FakeGame.Compile("UsesFuture", "public class U { public System.Future.Thing T; }",
            FakeGame.BclFiles().Append(Path.Combine(fakeRuntime, "System.Runtime.Future.dll")).Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)),
            Path.Combine(d, "UsesFuture.dll"));

        var f = await new DllDependencyValidator(new AssemblyInspector()).ValidateAsync(Ctx(d));

        Assert.Contains(f, x => x.Message.Contains("System.Runtime.Future") && x.Severity >= Severity.Warning);
    }

    [Fact]
    public async Task Runner_isolates_failing_validators_and_skips_inapplicable_ones()
    {
        var d = ModDir();
        var runner = new ValidationRunner(new IModValidator[] { new ThrowingValidator(), new PackageStructureValidator(), new ModInfoValidator() });
        var report = await runner.RunAsync(Ctx(d));
        Assert.Contains(report.Findings, f => f.ValidatorId == "throws" && f.Severity == Severity.Error && f.Message.Contains("could not run"));
        Assert.Contains("package", report.ValidatorsSkipped);
        Assert.DoesNotContain(report.Findings, f => f.ValidatorId == "package");
        Assert.True(report.HasErrors);
    }

    [Fact]
    public async Task Structure_and_duplicates()
    {
        var d = ModDir();
        File.WriteAllText(Path.Combine(d, "install.bat"), "echo hi");
        File.WriteAllText(Path.Combine(d, "loose.xml"), "<x/>");
        Directory.CreateDirectory(Path.Combine(d, "Nested"));
        File.WriteAllText(Path.Combine(d, "Nested", "ModInfo.xml"), "<xml/>");
        var s = await new FolderStructureValidator().ValidateAsync(Ctx(d));
        Assert.Contains(s, x => x.Severity == Severity.Warning && x.FilePath == "install.bat");
        Assert.Contains(s, x => x.Severity == Severity.Info && x.FilePath == "loose.xml");
        Assert.Contains(s, x => x.Message.Contains("nested ModInfo.xml"));
        var dup = await new DuplicateFileValidator().ValidateAsync(Ctx(d));
        Assert.Contains(dup, x => x.Message.Contains("Multiple ModInfo.xml"));
    }

    private sealed class ThrowingValidator : IModValidator
    {
        public string Id => "throws";
        public string DisplayName => "Throws";
        public Task<IReadOnlyList<ValidationFinding>> ValidateAsync(ValidationContext context, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }
}
