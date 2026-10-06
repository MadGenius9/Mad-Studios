using MadModStudio.Core.IO;
using MadModStudio.Game7DTD.Deploy;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class SafeFixTests
{
    private const string LegacyModInfo = """
        <?xml version="1.0" encoding="UTF-8"?>
        <xml>
          <ModInfo>
            <Name value="OldRacks" />
            <Description value="Legacy layout" />
            <Author value="Someone" />
          </ModInfo>
        </xml>
        """;

    /// <summary>Writes an XML-only mod into a Mods folder. Returns the mod folder.</summary>
    private static string WriteMod(string mods, string folder, string? modInfo, string configFolder = "Config")
    {
        var root = Path.Combine(mods, folder);
        Directory.CreateDirectory(Path.Combine(root, configFolder));
        File.WriteAllText(Path.Combine(root, configFolder, "blocks.xml"), SampleMod.BlocksPatch);
        if (modInfo != null) File.WriteAllText(Path.Combine(root, ModInfoFile.FileName), modInfo);
        return root;
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), FileUtil.Sha256);

    [Fact]
    public void Plan_detects_only_mechanical_fixes_and_never_modifies_the_folder()
    {
        var mods = FakeGame.TempDir("mods");
        var legacy = WriteMod(mods, "OldRacks", LegacyModInfo, configFolder: "config");
        var clean = WriteMod(mods, "Clean", SampleMod.ModInfoXml);
        var noModInfo = WriteMod(mods, "NoInfo", null);
        var malformed = WriteMod(mods, "Broken", "<xml><Name value=\"Broken\"");
        var nested = Path.Combine(mods, "Wrapper");
        WriteMod(nested, "Inner", SampleMod.ModInfoXml); // wrong hierarchy needs a human decision
        var before = Snapshot(mods);

        var legacyPlan = SafeFixService.Plan(legacy).Select(f => f.Id).ToList();
        Assert.Contains(SafeFixService.ConfigFolderCase, legacyPlan);
        Assert.Contains(SafeFixService.ModInfoLegacyLayout, legacyPlan);
        Assert.Contains(SafeFixService.ModInfoMissingVersion, legacyPlan);
        Assert.DoesNotContain(SafeFixService.ModInfoMissingName, legacyPlan);
        Assert.Empty(SafeFixService.Plan(clean));
        Assert.Equal(new[] { SafeFixService.ModInfoMissing }, SafeFixService.Plan(noModInfo).Select(f => f.Id));
        Assert.Empty(SafeFixService.Plan(malformed));
        Assert.Empty(SafeFixService.Plan(nested));

        Assert.Equal(before, Snapshot(mods));
    }

    [Fact]
    public async Task Fixes_a_copy_with_revisions_builds_it_and_leaves_the_original_untouched()
    {
        using var host = new TestHost();
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);
        var mods = FakeGame.TempDir("mods");
        var original = WriteMod(mods, "OldRacks", LegacyModInfo, configFolder: "config");
        var before = Snapshot(original);

        var result = await host.Get<SafeFixService>().FixCopyAsync(original, profile.Id);

        Assert.Null(result.Error);
        Assert.Equal(before, Snapshot(original));
        var project = result.Project!;
        Assert.Equal(3, result.Applied.Count);
        Assert.Equal(new[] { "Config" }, Directory.GetDirectories(project.ModRootPath).Select(Path.GetFileName));
        var info = ModInfoFile.Parse(Path.Combine(project.ModRootPath, ModInfoFile.FileName));
        Assert.Equal(ModInfoFormat.V2, info.Format);
        Assert.Equal("OldRacks", info.Name);
        Assert.Equal("Legacy layout", info.Description);
        Assert.Equal("Someone", info.Author);
        Assert.Equal(SafeFixService.PlaceholderVersion, info.Version);
        Assert.True(result.Packaged, result.Build?.Summary);

        var revisions = await host.Get<ProjectService>().History.ListAsync(project);
        var beforeFix = Assert.Single(revisions, r => r.Action == "Before safe fixes");
        Assert.Contains(revisions, r => r.Action == "Safe fixes applied");
        Assert.Equal("safe-fix", beforeFix.Metadata!["kind"]);

        // The fixed copy is what Deploy to Game installs (with backup/undo of the original folder).
        profile.ModsPath = mods;
        var deploy = host.Get<ModDeployService>();
        deploy.RunningGameProcesses = () => Array.Empty<string>();
        var deployed = await deploy.DeployAsync(project, profile);
        Assert.NotNull(deployed.Record!.BackupPath);
        Assert.True(Directory.Exists(Path.Combine(mods, "OldRacks", "Config")));
        Assert.True((await deploy.UndoAsync(deployed.Record, profile)).Undone);
        Assert.Equal(before, Snapshot(original));
    }

    [Fact]
    public async Task Restoring_the_before_revision_reverts_the_fixes()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        var original = WriteMod(FakeGame.TempDir("mods"), "NoVersion", SampleMod.ModInfoXml.Replace("<Version value=\"1.0.8\" />", ""));
        var result = await host.Get<SafeFixService>().FixCopyAsync(original, profile.Id);
        var project = result.Project!;
        Assert.Equal(SafeFixService.PlaceholderVersion, ModInfoFile.Parse(Path.Combine(project.ModRootPath, ModInfoFile.FileName)).Version);

        var projects = host.Get<ProjectService>();
        var beforeFix = (await projects.History.ListAsync(project)).Single(r => r.Action == "Before safe fixes");
        await projects.History.RestoreAsync(project, beforeFix);
        Assert.Null(ModInfoFile.Parse(Path.Combine(project.ModRootPath, ModInfoFile.FileName)).Version);
    }

    [Fact]
    public async Task Running_again_on_an_unchanged_folder_reuses_the_fixed_copy()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        var original = WriteMod(FakeGame.TempDir("mods"), "OldRacks", LegacyModInfo, configFolder: "config");
        var fixer = host.Get<SafeFixService>();

        var first = await fixer.FixCopyAsync(original, profile.Id);
        var second = await fixer.FixCopyAsync(original, profile.Id);
        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.Project!.Id, second.Project!.Id);
        Assert.Single(await host.Get<ProjectService>().ListAsync());

        // A changed folder is a different mod state: it gets a fresh copy.
        File.AppendAllText(Path.Combine(original, "config", "blocks.xml"), "<!-- changed -->");
        var third = await fixer.FixCopyAsync(original, profile.Id);
        Assert.False(third.Reused);
        Assert.NotEqual(first.Project.Id, third.Project!.Id);
    }

    [Fact]
    public async Task Mod_without_safe_fixes_is_not_imported()
    {
        using var host = new TestHost();
        var clean = WriteMod(FakeGame.TempDir("mods"), "Clean", SampleMod.ModInfoXml);
        var result = await host.Get<SafeFixService>().FixCopyAsync(clean, null);
        Assert.Null(result.Project);
        Assert.Equal("No safe fixes apply.", result.Error);
        Assert.Empty(await host.Get<ProjectService>().ListAsync());
    }
}
