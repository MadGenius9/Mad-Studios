using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

/// <summary>
/// Mad Mod Studio must not keep files in the game folder open after reading them: on Windows an open handle or memory
/// map locks the game's DLLs and blocks game updates and file moves.
/// </summary>
public class GameFolderLockTests
{
    [Fact]
    public async Task Profile_index_compile_and_decompile_leave_no_open_handles_in_the_game_folder()
    {
        using var host = new TestHost();
        var root = FakeGame.TempDir("lockcheck");
        var game = Path.Combine(root, "game");
        FakeGame.Create(game);

        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(game)).Profile!; // version detection decompiles game types
        Assert.True((await profiles.ReindexAsync(profile)).Success);
        var project = Assert.Single(await host.Get<ProjectService>().ImportAsync(SampleMod.WriteZip(FakeGame.TempDir("inbox")), profile.Id)).Project;
        var build = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions { Package = false });
        Assert.True(build.Compiles.All(c => c.Result.Success), build.Summary);
        Assert.True(host.Get<DecompilerService>().DecompileType(FakeGame.ManagedPath(game) + "/Assembly-CSharp.dll", "EntityDrone", new[] { FakeGame.ManagedPath(game) }).Success);

        if (OperatingSystem.IsLinux())
        {
            var full = Path.GetFullPath(game);
            var fds = Directory.GetFiles("/proc/self/fd")
                .Select(fd => { try { return new FileInfo(fd).LinkTarget; } catch (IOException) { return null; } })
                .Where(t => t != null && t.StartsWith(full, StringComparison.Ordinal)).ToList();
            var maps = File.ReadAllLines("/proc/self/maps").Where(l => l.Contains(full, StringComparison.Ordinal)).ToList();
            Assert.True(fds.Count == 0, "Open handles: " + string.Join(", ", fds));
            Assert.True(maps.Count == 0, "Memory-mapped game files: " + string.Join(", ", maps.Select(m => m[(m.IndexOf('/'))..]).Distinct()));
        }
        else
        {
            // On Windows a remaining handle or mapping makes the move fail.
            Directory.Move(game, Path.Combine(root, "moved"));
        }
    }
}
