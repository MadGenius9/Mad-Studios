using MadModStudio.Core.Models;
using MadModStudio.Persistence;
using MadModStudio.TestSupport;

namespace MadModStudio.Core.Tests;

public class PersistenceTests
{
    [Fact]
    public async Task Profiles_projects_builds_and_settings_roundtrip()
    {
        var db = new AppDatabase(Path.Combine(FakeGame.TempDir("db"), "app.db"));
        var profiles = new SqliteGameProfileRepository(db);
        var profile = new GameProfile { Name = "7DTD", InstallPath = "C:/Games/7DTD", GameVersion = "V 2.1 (b7)", IndexStatus = IndexStatus.Indexed };
        profile.Compilation.ExtraReferencePaths.Add("C:/libs/Extra.dll");
        await profiles.SaveAsync(profile);

        var loaded = await profiles.GetAsync(profile.Id);
        Assert.Equal("V 2.1 (b7)", loaded!.GameVersion);
        Assert.Equal(IndexStatus.Indexed, loaded.IndexStatus);
        Assert.Equal("C:/libs/Extra.dll", Assert.Single(loaded.Compilation.ExtraReferencePaths));

        var projects = new SqliteProjectRepository(db);
        var project = new ModProject { Name = "Racks", Version = "1.0.9", GameProfileId = profile.Id, ServerSideOnly = true, ModType = ModType.Hybrid };
        await projects.SaveAsync(project);
        var p = Assert.Single(await projects.ListAsync());
        Assert.True(p.ServerSideOnly);
        Assert.Equal(ModType.Hybrid, p.ModType);

        var builds = new SqliteBuildRecordRepository(db);
        await builds.AddAsync(new BuildRecord { ProjectId = project.Id, StartedUtc = DateTimeOffset.UtcNow, Version = "1.0.9", Succeeded = true, PackagePath = "x.zip" });
        Assert.Equal("x.zip", Assert.Single(await builds.ListAsync(project.Id)).PackagePath);

        var settings = new SqliteSettingsRepository(db);
        await settings.SetAsync("ai.provider", "anthropic");
        Assert.Equal("anthropic", await settings.GetAsync("ai.provider"));
        Assert.Null(await settings.GetAsync("missing"));

        await projects.DeleteAsync(project.Id);
        Assert.Empty(await projects.ListAsync());
        Assert.Empty(await builds.ListAsync(project.Id)); // cascade
    }
}
