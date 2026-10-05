using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class GameIndexTests
{
    [Fact]
    public async Task Index_contains_types_members_xml_and_localization_from_the_install()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var profile = (await svc.CreateProfileAsync(FakeGame.Shared)).Profile!;
        Assert.Null(svc.GetIndex(profile));

        var result = await svc.ReindexAsync(profile);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(profile.LastIndexedUtc);
        var index = svc.GetIndex(profile)!;
        var summary = index.GetSummary();
        Assert.Equal(2, summary.Assemblies); // Assembly-CSharp + Harmony; BCL is not indexed
        Assert.Contains("Assembly-CSharp", index.GetIndexedAssemblyNames());

        var drone = index.GetType("EntityDrone")!;
        Assert.Equal("EntityAlive", drone.BaseType);
        var members = index.GetMembers("EntityDrone");
        Assert.Contains(members, m => m.Name == "depositInventory" && !m.IsPublic && m.ParameterCount == 1); // private members of game code are indexed
        Assert.Equal(2, index.GetMembers("EntityDrone", "SendHome").Count);
        Assert.Contains(index.FindMemberInHierarchy("EntityDrone", "IsDead"), m => m.DeclaringType == "EntityAlive");
        Assert.NotNull(index.GetType("EntityDrone.DroneState")); // C#-style nested name resolves to EntityDrone+DroneState

        var xml = index.SearchXml("cntWoodWritableCrate");
        Assert.Contains(xml, x => x.File == "blocks.xml" && x.Path == "/blocks/block[@name='cntWoodWritableCrate']");
        Assert.True(index.LocalizationKeyExists("resourceWood"));
        Assert.Equal("Wood", index.SearchLocalization("resourceWood").Single().English);
    }

    [Fact]
    public async Task Reindex_of_moved_install_fails_gracefully()
    {
        using var host = new TestHost();
        var svc = host.Get<GameProfileService>();
        var root = FakeGame.TempDir("movable");
        FakeGame.Create(Path.Combine(root, "game"));
        var profile = (await svc.CreateProfileAsync(Path.Combine(root, "game"))).Profile!;
        Directory.Move(Path.Combine(root, "game"), Path.Combine(root, "moved"));

        var result = await svc.ReindexAsync(profile);

        Assert.False(result.Success);
        Assert.Equal(IndexStatus.Failed, profile.IndexStatus);
        Assert.Contains("does not exist", profile.IndexError);
    }
}
