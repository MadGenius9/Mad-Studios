using System.Diagnostics;
using System.Text;
using MadModStudio.Core.History;
using MadModStudio.TestSupport;

namespace MadModStudio.Core.Tests;

public class GitObjectStoreTests
{
    [Fact]
    public void Blob_ids_match_real_git()
    {
        var store = new GitObjectStore(Path.Combine(FakeGame.TempDir("git"), "h.git"));
        store.EnsureInitialized();
        // `printf 'hello\n' | git hash-object --stdin`
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", store.WriteBlob(Encoding.UTF8.GetBytes("hello\n")));
        // Empty tree id is a well-known constant.
        Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", store.WriteTree(Array.Empty<GitTreeEntry>()));
    }

    [Fact]
    public void Snapshot_commit_and_read_back_roundtrip()
    {
        var work = FakeGame.TempDir("work");
        Directory.CreateDirectory(Path.Combine(work, "Config"));
        File.WriteAllText(Path.Combine(work, "ModInfo.xml"), "<xml/>");
        File.WriteAllText(Path.Combine(work, "Config", "blocks.xml"), "<configs/>");
        var store = new GitObjectStore(Path.Combine(FakeGame.TempDir("git"), "h.git"));
        store.EnsureInitialized();

        var tree = store.SnapshotDirectory(work);
        var commit = store.WriteCommit(tree, null, "Tester", "t@example.com", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "First\n\nBody");
        store.UpdateRef(commit);

        Assert.Equal(commit, store.ReadRef());
        var c = store.ReadCommit(commit);
        Assert.Equal(tree, c.Tree);
        Assert.Equal("First\n\nBody", c.Message);
        var files = store.FlattenTree(tree);
        Assert.Equal(new[] { "Config/blocks.xml", "ModInfo.xml" }, files.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("<configs/>", Encoding.UTF8.GetString(store.ReadBlob(files["Config/blocks.xml"])));
    }

    [Fact]
    public void Repository_is_readable_by_git_cli_when_available()
    {
        var git = FindGit();
        if (git is null) return; // git CLI is optional on user machines; the format itself is covered above.

        var work = FakeGame.TempDir("work");
        File.WriteAllText(Path.Combine(work, "a.txt"), "A");
        var gitDir = Path.Combine(FakeGame.TempDir("git"), "h.git");
        var store = new GitObjectStore(gitDir);
        store.EnsureInitialized();
        var first = store.WriteCommit(store.SnapshotDirectory(work), null, "T", "t@x", DateTimeOffset.UtcNow, "one");
        File.WriteAllText(Path.Combine(work, "a.txt"), "B");
        var second = store.WriteCommit(store.SnapshotDirectory(work), first, "T", "t@x", DateTimeOffset.UtcNow, "two");
        store.UpdateRef(second);

        var (fsckCode, fsckOut) = Run(git, $"--git-dir \"{gitDir}\" fsck --strict");
        Assert.True(fsckCode == 0, fsckOut);
        var (_, log) = Run(git, $"--git-dir \"{gitDir}\" log --format=%s");
        Assert.Equal("two\none", log.Trim().Replace("\r", ""));
        var (_, show) = Run(git, $"--git-dir \"{gitDir}\" show {first}:a.txt");
        Assert.Equal("A", show.Trim());
    }

    private static string? FindGit()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "git", "git.exe" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static (int, string) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}
