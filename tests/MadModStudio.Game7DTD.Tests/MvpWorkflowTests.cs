using System.IO.Compression;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

/// <summary>
/// The MVP success criterion end to end: game profile → index → import ZIP → analyze → inspect DLL → edit source →
/// compile against the game's assemblies → validate → set version → package → original preserved → history recorded.
/// </summary>
public class MvpWorkflowTests
{
    [Fact]
    public async Task Full_pipeline_from_zip_to_installable_package()
    {
        using var host = new TestHost();
        var gameRoot = FakeGame.Shared;

        // 2-5. Add installation, validate, create profile.
        var profiles = host.Get<GameProfileService>();
        var created = await profiles.CreateProfileAsync(gameRoot);
        Assert.True(created.Validation.IsValid, string.Join("; ", created.Validation.Errors));
        var profile = created.Profile!;
        Assert.Equal("V 2.1 (b7)", profile.GameVersion);
        Assert.Equal(FakeGame.HarmonyPath(gameRoot), profile.HarmonyAssemblyPath);

        // 5. Index.
        var indexResult = await profiles.ReindexAsync(profile);
        Assert.True(indexResult.Success, indexResult.Error);
        Assert.Equal(IndexStatus.Indexed, profile.IndexStatus);
        var index = profiles.GetIndex(profile)!;
        Assert.Contains(index.SearchTypes("EntityDrone"), t => t.FullName == "EntityDrone");

        // 6-7. Import an existing ZIP.
        var inbox = FakeGame.TempDir("inbox");
        var zip = SampleMod.WriteZip(inbox);
        var zipHashBefore = MadModStudio.Core.IO.FileUtil.Sha256(zip);
        var projects = host.Get<ProjectService>();
        var imported = await projects.ImportAsync(zip, profile.Id);
        var project = Assert.Single(imported).Project;
        Assert.Equal("Mad Working Racks", project.Name);
        Assert.Equal("1.0.8", project.Version);
        Assert.Equal("MadWorkingRacks", project.ModFolderName);
        Assert.True(File.Exists(Path.Combine(project.ModRootPath, "ModInfo.xml")));

        // 8-9. Analyze.
        var analyzer = host.Get<Mods.ModAnalyzer>();
        var analysis = analyzer.Analyze(project.ModRootPath, project.SourcePath, index, profile);
        Assert.Equal(ModType.Hybrid, analysis.ModType);
        Assert.True(analysis.SourceAvailable);
        Assert.Equal(2, analysis.SourceFiles.Count);
        Assert.Equal(2, analysis.HarmonyPatches.Count);
        Assert.Contains(analysis.HarmonyPatches, p => p.TargetType == "EntityDrone" && p.TargetMethod == "SendHome" && p.PatchKind == "Postfix");

        // 12. Edit C# source (recorded in history).
        var patchFile = "Harmony/Patches.cs";
        var text = projects.ReadFile(project, patchFile);
        await projects.SaveFileAsync(project, patchFile, text.Replace("Drone is home", "Drone returned home"), "Tweak log message");

        // 13-18. Compile, validate, set version, package.
        var events = new List<PipelineEvent>();
        var pipeline = host.Get<ModBuildPipeline>();
        var result = await pipeline.RunAsync(project, new BuildOptions { Configuration = BuildConfiguration.Release, NewVersion = "1.0.9" },
            new SyncProgress<PipelineEvent>(events.Add));

        var compile = Assert.Single(result.Compiles);
        Assert.True(compile.Result.Success, string.Join("\n", compile.Result.Diagnostics));
        Assert.Equal("MadWorkingRacks", compile.Unit.AssemblyName);
        Assert.False(result.Validation!.HasErrors, string.Join("\n", result.Validation.Findings.Where(f => f.Severity == Severity.Error)));
        Assert.True(result.Succeeded, result.Summary);
        Assert.NotNull(result.Package);
        Assert.Equal("MadWorkingRacks_1.0.9.zip", Path.GetFileName(result.Package!.ZipPath));
        Assert.Contains(events, e => e.Stage == PipelineStage.Compile && e.Status == StageStatus.Succeeded);
        Assert.Contains(events, e => e.Stage == PipelineStage.Package && e.Status == StageStatus.Succeeded && e.Message.Contains(result.Package.ZipPath!));

        // The ZIP has the proper mod folder hierarchy, the compiled DLL, and no junk or source.
        using (var archive = ZipFile.OpenRead(result.Package.ZipPath!))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("MadWorkingRacks/ModInfo.xml", names);
            Assert.Contains("MadWorkingRacks/Config/blocks.xml", names);
            Assert.Contains("MadWorkingRacks/Config/Localization.txt", names);
            Assert.Contains("MadWorkingRacks/MadWorkingRacks.dll", names);
            Assert.DoesNotContain(names, n => n.EndsWith(".cs") || n.EndsWith(".csproj") || n.Contains("/.vs/") || n.Contains("/obj/") || n.EndsWith(".log") || n.EndsWith(".pdb"));
            var modInfo = new StreamReader(archive.GetEntry("MadWorkingRacks/ModInfo.xml")!.Open()).ReadToEnd();
            Assert.Contains("value=\"1.0.9\"", modInfo);
        }

        // 10-11. Inspect the compiled DLL from the package without executing it.
        var extracted = FakeGame.TempDir("extract");
        ZipFile.ExtractToDirectory(result.Package.ZipPath!, extracted);
        var dll = Path.Combine(extracted, "MadWorkingRacks", "MadWorkingRacks.dll");
        var report = new AssemblyInspector().Inspect(dll);
        Assert.True(report.Success, report.ReadError);
        Assert.Equal("MadWorkingRacks", report.Name);
        Assert.Contains(report.References, r => r.Name == "Assembly-CSharp");
        Assert.Contains(report.References, r => r.Name == "0Harmony");
        Assert.Contains("MadWorkingRacks.ModInit", report.ModApiEntryPoints);
        Assert.Contains(report.HarmonyPatches, p => p.TargetType == "EntityDrone" && p.TargetMethod == "depositInventory" && p.PatchKind == "Prefix");
        var decompiled = new DecompilerService().DecompileType(dll, "MadWorkingRacks.EntityDrone_SendHome_Patch", new[] { FakeGame.ManagedPath(gameRoot) });
        Assert.True(decompiled.Success, decompiled.Error);
        Assert.StartsWith(DecompiledSource.Banner, decompiled.Text);
        Assert.Contains("Drone returned home", decompiled.Text);

        // 19. Original preserved.
        Assert.Equal(zipHashBefore, MadModStudio.Core.IO.FileUtil.Sha256(zip));
        var originalCopy = Path.Combine(project.Workspace.Original, Path.GetFileName(zip));
        Assert.Equal(zipHashBefore, MadModStudio.Core.IO.FileUtil.Sha256(originalCopy));

        // 20. History records the import, the edit, the version change and the build.
        var history = await projects.History.ListAsync(project);
        Assert.Contains(history, r => r.Action == "Imported");
        Assert.Contains(history, r => r.Action == "Edited Harmony/Patches.cs" && r.ChangedFiles.Contains("M Harmony/Patches.cs"));
        Assert.Contains(history, r => r.Action == "Set version 1.0.9" && r.ChangedFiles.Contains("M ModInfo.xml"));
        Assert.Contains(history, r => r.BuildStatus == "Build succeeded");

        var saved = await projects.GetAsync(project.Id);
        Assert.Equal(ProjectStatus.Packaged, saved!.Status);
        Assert.Equal("1.0.9", saved.Version);
    }

    [Fact]
    public async Task Compile_errors_block_packaging_and_report_file_and_line()
    {
        using var host = new TestHost();
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        await profiles.ReindexAsync(profile);

        var broken = SampleMod.PatchSource.Replace("__instance.IsHome", "__instance.IsAtHomeBase");
        var zip = SampleMod.WriteZip(FakeGame.TempDir("inbox"), patchSource: broken);
        var project = (await host.Get<ProjectService>().ImportAsync(zip, profile.Id)).Single().Project;

        var result = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions());
        var compile = Assert.Single(result.Compiles).Result;
        Assert.False(compile.Success);
        var error = Assert.Single(compile.Errors);
        Assert.Equal("CS1061", error.Id);
        Assert.Equal("Harmony/Patches.cs", error.FilePath);
        Assert.Equal(10, error.Line);
        Assert.Null(result.Package);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Events, e => e.Stage == PipelineStage.Package && e.Status == StageStatus.Failed && e.Message.Contains("Package With Errors"));
        Assert.Empty(Directory.GetFiles(project.Workspace.Output));

        // Explicit override packages, clearly labeled.
        var forced = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions { PackageWithErrors = true });
        Assert.True(forced.PackagedWithErrors);
        Assert.Contains("WITH-ERRORS", Path.GetFileName(forced.Package!.ZipPath));
        Assert.False(forced.Succeeded);
    }

    [Fact]
    public async Task Xml_only_mod_skips_compile_honestly()
    {
        using var host = new TestHost();
        var profile = (await host.Get<GameProfileService>().CreateProfileAsync(FakeGame.Shared)).Profile!;
        var src = FakeGame.TempDir("xmlmod");
        var root = Path.Combine(src, "XmlOnly");
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        File.WriteAllText(Path.Combine(root, "ModInfo.xml"), """<xml><Name value="XmlOnly"/><DisplayName value="Xml Only"/><Version value="1.0.0"/><Author value="me"/></xml>""");
        File.WriteAllText(Path.Combine(root, "Config", "items.xml"), """<configs><set xpath="/items/item[@name='resourceWood']/property[@name='Stacknumber']/@value">1000</set></configs>""");
        var project = (await host.Get<ProjectService>().ImportAsync(root, profile.Id)).Single().Project;

        var result = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions());
        Assert.True(result.CompileSkipped);
        Assert.Contains(result.Events, e => e.Stage == PipelineStage.Compile && e.Status == StageStatus.Skipped);
        Assert.True(result.Succeeded, result.Summary + string.Join("\n", result.Validation!.Findings));
        Assert.Equal(SideRequirement.LikelyServerSide, project.SideRequirement);
        Assert.Equal(EacCompatibility.LikelyCompatible, project.EacCompatibility);
    }
}

/// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts asynchronously).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

public class StaleDllTests
{
    [Fact]
    public async Task Old_build_with_different_name_is_not_referenced()
    {
        using var host = new TestHost();
        var profiles = host.Get<GameProfileService>();
        var profile = (await profiles.CreateProfileAsync(FakeGame.Shared)).Profile!;
        var src = FakeGame.TempDir("stale");
        var root = SampleMod.WriteFolder(src);
        // Simulate an old build of the same code shipped under another file name.
        var refs = FakeGame.BclFiles()
            .Append(Path.Combine(FakeGame.ManagedPath(FakeGame.Shared), "Assembly-CSharp.dll"))
            .Append(FakeGame.HarmonyPath(FakeGame.Shared))
            .Select(f => (Microsoft.CodeAnalysis.MetadataReference)Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(f));
        FakeGame.Compile("MadWorkingRacks_Old", new[] { SampleMod.InitSource, SampleMod.PatchSource }, refs, Path.Combine(root, "MadWorkingRacks_Old.dll"));
        var project = (await host.Get<ProjectService>().ImportAsync(root, profile.Id)).Single().Project;

        var result = await host.Get<ModBuildPipeline>().RunAsync(project, new BuildOptions { Package = false });

        Assert.True(result.CompileSucceeded, string.Join("\n", result.AllDiagnostics));
        Assert.Contains(result.Events, e => e.Status == StageStatus.Warning && e.Message.Contains("MadWorkingRacks_Old.dll") && e.Message.Contains("old build"));
    }
}
