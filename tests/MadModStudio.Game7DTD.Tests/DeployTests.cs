using System.IO.Compression;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Deploy;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class DeployTests
{
    private sealed record Setup(TestHost Host, ModProject Project, GameProfile Profile, ModDeployService Deploy, string Mods);

    /// <summary>Imports the sample mod, builds a clean package and points the profile at an empty temp Mods folder.</summary>
    private static async Task<Setup> BuildAsync(string version = "1.0.9")
    {
        var host = new TestHost();
        var created = await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared);
        var profile = created.Profile!;
        await host.Get<GameProfileService>().ReindexAsync(profile);
        var project = Assert.Single(await host.Get<ProjectService>().ImportAsync(SampleMod.WriteZip(FakeGame.TempDir("inbox")), profile.Id)).Project;
        var build = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions { NewVersion = version });
        Assert.True(build.Succeeded, build.Summary);
        // Never touch the shared fake game's Mods folder.
        profile.ModsPath = FakeGame.TempDir("mods");
        var deploy = host.Get<ModDeployService>();
        deploy.RunningGameProcesses = () => Array.Empty<string>();
        return new Setup(host, project, profile, deploy, profile.ModsPath);
    }

    [Fact]
    public async Task Deploys_latest_clean_package_into_mods_folder_and_undo_removes_it()
    {
        var s = await BuildAsync();
        using var _ = s.Host;

        var result = await s.Deploy.DeployAsync(s.Project, s.Profile);
        var target = Path.Combine(s.Mods, "MadWorkingRacks");
        Assert.Equal(target, result.Record!.TargetPath);
        Assert.True(File.Exists(Path.Combine(target, "ModInfo.xml")));
        Assert.True(File.Exists(Path.Combine(target, "MadWorkingRacks.dll")));
        Assert.Null(result.Record.BackupPath);
        Assert.Equal("1.0.9", result.Record.Version);
        Assert.NotEmpty(result.Record.Manifest);
        Assert.Single(await s.Deploy.ListAsync(s.Project));

        var undo = await s.Deploy.UndoAsync(result.Record, s.Profile);
        Assert.True(undo.Undone, undo.Message);
        Assert.False(Directory.Exists(target));
        Assert.True((await s.Deploy.ListAsync(s.Project))[0].IsUndone);
    }

    [Fact]
    public async Task Existing_folder_is_backed_up_and_restored_on_undo()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var target = Path.Combine(s.Mods, "MadWorkingRacks");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "ModInfo.xml"), "<xml><Name value=\"MadWorkingRacks\" /><Version value=\"1.0.7\" /></xml>");
        File.WriteAllText(Path.Combine(target, "user-notes.txt"), "keep me");

        var result = await s.Deploy.DeployAsync(s.Project, s.Profile);
        Assert.NotNull(result.Record!.BackupPath);
        Assert.False(File.Exists(Path.Combine(target, "user-notes.txt")));

        var undo = await s.Deploy.UndoAsync(result.Record, s.Profile);
        Assert.True(undo.Undone, undo.Message);
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "user-notes.txt")));
        Assert.Contains("1.0.7", File.ReadAllText(Path.Combine(target, "ModInfo.xml")));
    }

    [Fact]
    public async Task Undo_refuses_when_deployed_files_were_changed_unless_forced()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var record = (await s.Deploy.DeployAsync(s.Project, s.Profile)).Record!;
        File.AppendAllText(Path.Combine(record.TargetPath, "ModInfo.xml"), "<!-- edited in game folder -->");

        var refused = await s.Deploy.UndoAsync(record, s.Profile);
        Assert.False(refused.Undone);
        Assert.Contains("ModInfo.xml", refused.ModifiedFiles);
        Assert.True(Directory.Exists(record.TargetPath));

        var forced = await s.Deploy.UndoAsync(record, s.Profile, force: true);
        Assert.True(forced.Undone);
        Assert.False(Directory.Exists(record.TargetPath));
    }

    [Fact]
    public async Task Older_deployment_cannot_be_undone_while_a_newer_one_is_active()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var first = (await s.Deploy.DeployAsync(s.Project, s.Profile)).Record!;
        var second = (await s.Deploy.DeployAsync(s.Project, s.Profile)).Record!;
        Assert.NotNull(second.BackupPath);

        var refused = await s.Deploy.UndoAsync(first, s.Profile);
        Assert.False(refused.Undone);
        Assert.Contains("newer deployment", refused.Message);

        Assert.True((await s.Deploy.UndoAsync(second, s.Profile)).Undone);
        Assert.True(Directory.Exists(first.TargetPath)); // first deployment is back
        Assert.True((await s.Deploy.UndoAsync(first, s.Profile)).Undone);
        Assert.False(Directory.Exists(first.TargetPath));
    }

    [Fact]
    public async Task Newer_deployment_from_another_project_blocks_undo_even_when_forced()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var first = (await s.Deploy.DeployAsync(s.Project, s.Profile)).Record!;
        // A second project of the same mod (e.g. a fixed copy) deploys into the same folder.
        var other = Assert.Single(await s.Host.Get<ProjectService>().ImportAsync(SampleMod.WriteZip(FakeGame.TempDir("inbox2"), "1.1.0"), s.Profile.Id)).Project;
        Assert.True((await s.Host.Get<ModBuildPipeline>().RunAsync(other, new BuildOptions())).Succeeded);
        var second = (await s.Deploy.DeployAsync(other, s.Profile)).Record!;

        var refused = await s.Deploy.UndoAsync(first, s.Profile, force: true);
        Assert.False(refused.Undone);
        Assert.Contains("newer deployment", refused.Message);
        Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(second.TargetPath, "ModInfo.xml")));
    }

    [Fact]
    public async Task Refuses_while_the_game_is_running()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        s.Deploy.RunningGameProcesses = () => new[] { "7DaysToDie" };
        var ex = await Assert.ThrowsAsync<DeployException>(() => s.Deploy.DeployAsync(s.Project, s.Profile));
        Assert.Contains("running", ex.Message);
        Assert.Empty(Directory.GetDirectories(s.Mods));
    }

    [Fact]
    public async Task Refuses_without_a_clean_package()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        profile.ModsPath = FakeGame.TempDir("mods");
        var project = Assert.Single(await host.Get<ProjectService>().ImportAsync(SampleMod.WriteZip(FakeGame.TempDir("inbox")), profile.Id)).Project;
        var deploy = host.Get<ModDeployService>();
        deploy.RunningGameProcesses = () => Array.Empty<string>();
        var ex = await Assert.ThrowsAsync<DeployException>(() => deploy.DeployAsync(project, profile));
        Assert.Contains("Build the project first", ex.Message);
    }

    [Fact]
    public async Task Rejects_package_without_a_single_mod_folder()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var bad = Path.Combine(FakeGame.TempDir("badpkg"), "bad.zip");
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create))
        {
            zip.CreateEntry("A/ModInfo.xml");
            zip.CreateEntry("B/ModInfo.xml");
        }
        var ex = await Assert.ThrowsAsync<DeployException>(() => s.Deploy.DeployAsync(s.Project, s.Profile, bad));
        Assert.Contains("exactly one", ex.Message);
        Assert.Empty(Directory.GetDirectories(s.Mods));
    }

    [Fact]
    public async Task Warns_when_another_folder_declares_the_same_mod_name()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        var other = Path.Combine(s.Mods, "OldCopyOfRacks");
        Directory.CreateDirectory(other);
        var deployedInfo = File.ReadAllText(Path.Combine(s.Project.ModRootPath, "ModInfo.xml"));
        File.WriteAllText(Path.Combine(other, "ModInfo.xml"), deployedInfo);

        var result = await s.Deploy.DeployAsync(s.Project, s.Profile);
        Assert.Contains(result.Warnings, w => w.Contains("OldCopyOfRacks"));
    }

    [Fact]
    public async Task Warns_when_the_deployed_mod_conflicts_with_an_installed_mod()
    {
        var s = await BuildAsync();
        using var _ = s.Host;
        s.Profile.ConfigPath = Path.Combine(FakeGame.Shared, "Data", "Config");
        // The sample mod appends <block name="madWorkingRack">; another installed mod adds the same block.
        var other = Path.Combine(s.Mods, "ZZ_OtherRacks", "Config");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "blocks.xml"), File.ReadAllText(Path.Combine(s.Project.ModRootPath, "Config", "blocks.xml")));

        var result = await s.Deploy.DeployAsync(s.Project, s.Profile);
        Assert.Contains(result.Warnings, w => w.StartsWith("Conflict:") && w.Contains("ZZ_OtherRacks") && w.Contains("madWorkingRack"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Folder_names_cannot_escape_the_mods_folder(string name)
    {
        Assert.Throws<DeployException>(() => ModDeployService.InsideModsFolder(FakeGame.TempDir("mods"), name));
    }
}
