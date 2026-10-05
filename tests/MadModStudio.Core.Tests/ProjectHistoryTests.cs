using MadModStudio.Core.History;
using MadModStudio.Core.Models;
using MadModStudio.Persistence;
using MadModStudio.TestSupport;

namespace MadModStudio.Core.Tests;

public class ProjectHistoryTests
{
    private static (ProjectHistoryService History, ModProject Project, SqliteProjectRepository Projects) Setup()
    {
        var root = FakeGame.TempDir("hist");
        var db = new AppDatabase(Path.Combine(root, "app.db"));
        var projects = new SqliteProjectRepository(db);
        var project = new ModProject { Name = "Test", WorkspacePath = Path.Combine(root, "ws") };
        project.Workspace.EnsureCreated();
        projects.SaveAsync(project).Wait();
        File.WriteAllText(Path.Combine(project.SourcePath, "ModInfo.xml"), "<xml><Version value=\"1.0.0\"/></xml>");
        return (new ProjectHistoryService(new SqliteRevisionRepository(db)), project, projects);
    }

    [Fact]
    public async Task Creates_revisions_with_changed_files()
    {
        var (history, project, _) = Setup();

        var first = await history.CreateRevisionAsync(project, "Imported", "initial");
        File.WriteAllText(Path.Combine(project.SourcePath, "ModInfo.xml"), "<xml><Version value=\"1.0.1\"/></xml>");
        File.WriteAllText(Path.Combine(project.SourcePath, "new.xml"), "<x/>");
        var second = await history.CreateRevisionAsync(project, "Edit", "changed");

        Assert.NotNull(first);
        Assert.Equal(new[] { "A ModInfo.xml" }, first!.ChangedFiles);
        Assert.Equal(new[] { "M ModInfo.xml", "A new.xml" }, second!.ChangedFiles);
        Assert.Equal(first.CommitId, second.ParentCommitId);
        var list = await history.ListAsync(project);
        Assert.Equal(new[] { "Edit", "Imported" }, list.Select(r => r.Action));
    }

    [Fact]
    public async Task Unchanged_tree_creates_no_revision_unless_forced()
    {
        var (history, project, _) = Setup();
        await history.CreateRevisionAsync(project, "One");

        Assert.Null(await history.CreateRevisionAsync(project, "Two"));
        Assert.False(history.HasUnrecordedChanges(project));
        Assert.NotNull(await history.CreateRevisionAsync(project, "Forced", force: true));
    }

    [Fact]
    public async Task Restore_brings_back_old_content_and_records_safety_snapshot()
    {
        var (history, project, _) = Setup();
        var path = Path.Combine(project.SourcePath, "ModInfo.xml");
        var original = File.ReadAllText(path);
        var r1 = await history.CreateRevisionAsync(project, "Original");
        File.WriteAllText(path, "<broken");
        File.WriteAllText(Path.Combine(project.SourcePath, "extra.cs"), "class X {}");

        await history.RestoreAsync(project, r1!);

        Assert.Equal(original, File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(project.SourcePath, "extra.cs")));
        var actions = (await history.ListAsync(project)).Select(r => r.Action).ToList();
        Assert.Contains("Before restore", actions);
        Assert.StartsWith("Restored revision", actions[0]);
        // The pre-restore state is still recoverable.
        var before = (await history.ListAsync(project)).First(r => r.Action == "Before restore");
        Assert.Equal("<broken", System.Text.Encoding.UTF8.GetString(history.ReadFile(project, before.CommitId, "ModInfo.xml")));
    }
}
