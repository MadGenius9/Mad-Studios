using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.TestSupport;

namespace MadModStudio.Compiler.Tests;

public class RoslynModCompilerTests
{
    private static IReadOnlyList<string> GameReferences() =>
        new GameReferenceResolver().Resolve(new GameProfile
        {
            ManagedPath = FakeGame.ManagedPath(FakeGame.Shared),
            HarmonyAssemblyPath = FakeGame.HarmonyPath(FakeGame.Shared),
        }).Paths;

    private static (string Dir, List<string> Files) WriteSources(params (string Name, string Text)[] files)
    {
        var dir = FakeGame.TempDir("src");
        var list = new List<string>();
        foreach (var (name, text) in files)
        {
            var p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            list.Add(p);
        }
        return (dir, list);
    }

    [Fact]
    public async Task Compiles_mod_source_against_game_assemblies()
    {
        var (dir, files) = WriteSources(("ModInit.cs", SampleMod.InitSource), ("Harmony/Patches.cs", SampleMod.PatchSource));
        var outDir = FakeGame.TempDir("out");

        var result = await new RoslynModCompiler().CompileAsync(new CompileRequest
        {
            AssemblyName = "MadWorkingRacks",
            SourceFiles = files,
            ReferencePaths = GameReferences(),
            OutputDirectory = outDir,
            SourceRoot = dir,
        });

        Assert.True(result.Success, result.Log);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(File.Exists(result.OutputAssemblyPath));
        Assert.True(File.Exists(result.PdbPath));
        Assert.Contains(result.ReferencesUsed, r => r.EndsWith("Assembly-CSharp.dll"));
        Assert.Contains("Build succeeded", result.Log);
        var report = new AssemblyInspector().Inspect(result.OutputAssemblyPath!);
        Assert.Equal(2, report.HarmonyPatches.Count);
    }

    [Fact]
    public async Task Errors_are_reported_with_relative_file_line_and_column_and_no_dll_is_written()
    {
        var (dir, files) = WriteSources(("Broken/Bad.cs", "public class Bad\n{\n    public void M() { EntityDrone d = null; d.NoSuchMethod(); int x = \"s\"; }\n}\n"));
        var outDir = FakeGame.TempDir("out");

        var result = await new RoslynModCompiler().CompileAsync(new CompileRequest
        {
            AssemblyName = "Bad",
            SourceFiles = files,
            ReferencePaths = GameReferences(),
            OutputDirectory = outDir,
            SourceRoot = dir,
        });

        Assert.False(result.Success);
        Assert.Equal(2, result.ErrorCount);
        var missing = result.Errors.Single(e => e.Id == "CS1061");
        Assert.Equal("Broken/Bad.cs", missing.FilePath);
        Assert.Equal(3, missing.Line);
        Assert.Equal(47, missing.Column);
        Assert.Contains("NoSuchMethod", missing.Message);
        Assert.Contains(result.Errors, e => e.Id == "CS0029");
        Assert.False(File.Exists(Path.Combine(outDir, "Bad.dll")));
        Assert.Contains("Build FAILED", result.Log);
        // Structured diagnostics convert for the repair engine.
        var md = missing.ToModDiagnostic();
        Assert.Equal("Compiler", md.Source);
        Assert.Equal(Severity.Error, md.Severity);
        Assert.Equal("Broken/Bad.cs(3,47): Error CS1061: " + missing.Message, md.ToString());
    }

    [Fact]
    public async Task Debug_configuration_defines_DEBUG_and_release_does_not()
    {
        var (dir, files) = WriteSources(("A.cs", "#if DEBUG\npublic class OnlyInDebug {}\n#endif\npublic class Always {}\n"));
        async Task<AssemblyReport> Build(BuildConfiguration c)
        {
            var r = await new RoslynModCompiler().CompileAsync(new CompileRequest
            {
                AssemblyName = "Cfg" + c, SourceFiles = files, ReferencePaths = GameReferences(), OutputDirectory = FakeGame.TempDir("out"), Configuration = c,
            });
            Assert.True(r.Success, r.Log);
            return new AssemblyInspector().Inspect(r.OutputAssemblyPath!);
        }
        Assert.Contains((await Build(BuildConfiguration.Debug)).Types, t => t.Name == "OnlyInDebug");
        Assert.DoesNotContain((await Build(BuildConfiguration.Release)).Types, t => t.Name == "OnlyInDebug");
    }

    [Fact]
    public async Task Missing_core_library_and_missing_sources_are_clear_errors()
    {
        var (_, files) = WriteSources(("A.cs", "public class A {}"));
        var noCore = await new RoslynModCompiler().CompileAsync(new CompileRequest
        {
            AssemblyName = "A", SourceFiles = files, ReferencePaths = new[] { FakeGame.HarmonyPath(FakeGame.Shared), "/nope/missing.dll" }, OutputDirectory = FakeGame.TempDir("out"),
        });
        Assert.False(noCore.Success);
        Assert.Contains(noCore.Diagnostics, d => d.Id == "MMS0006");
        Assert.Contains(noCore.Diagnostics, d => d.Id == "MMS0004" && d.Severity == Severity.Warning);

        var noSource = await new RoslynModCompiler().CompileAsync(new CompileRequest
        {
            AssemblyName = "A", SourceFiles = Array.Empty<string>(), ReferencePaths = GameReferences(), OutputDirectory = FakeGame.TempDir("out"),
        });
        Assert.Contains(noSource.Diagnostics, d => d.Id == "MMS0001");
    }

    [Fact]
    public async Task Language_version_is_honoured()
    {
        var (_, files) = WriteSources(("A.cs", "public record Point(int X, int Y);"));
        var r = await new RoslynModCompiler().CompileAsync(new CompileRequest
        {
            AssemblyName = "A", SourceFiles = files, ReferencePaths = GameReferences(), OutputDirectory = FakeGame.TempDir("out"), LanguageVersion = "7.3",
        });
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Message.Contains("7.3"));
    }

    [Fact]
    public void Csproj_reader_extracts_build_hints()
    {
        var dir = FakeGame.TempDir("proj");
        var path = Path.Combine(dir, "Mod.csproj");
        File.WriteAllText(path, """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>MyMod</AssemblyName>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
                <LangVersion>8.0</LangVersion>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <DefineConstants>DEBUG;TRACE;MYFLAG</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Assembly-CSharp"><HintPath>..\..\7DaysToDie_Data\Managed\Assembly-CSharp.dll</HintPath></Reference>
                <Reference Include="0Harmony, Version=2.10.0.0, Culture=neutral" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="Harmony\Init.cs" />
                <Compile Include="Scripts\Thing.cs" />
              </ItemGroup>
            </Project>
            """);
        var info = CsprojReader.Read(path);
        Assert.Null(info.Error);
        Assert.False(info.IsSdkStyle);
        Assert.Equal("MyMod", info.AssemblyName);
        Assert.Equal("v4.8", info.TargetFramework);
        Assert.Equal("8.0", info.LangVersion);
        Assert.True(info.AllowUnsafe);
        Assert.Contains("MYFLAG", info.DefineConstants);
        Assert.Equal(new[] { "Harmony/Init.cs", "Scripts/Thing.cs" }, info.CompileIncludes);
        Assert.Equal(new[] { "Assembly-CSharp", "0Harmony" }, info.References);

        File.WriteAllText(path, "<Project><bad");
        Assert.NotNull(CsprojReader.Read(path).Error);
    }

    [Fact]
    public void Reference_resolver_prefers_game_assemblies_and_honours_exclusions()
    {
        var modDir = FakeGame.TempDir("modrefs");
        File.Copy(FakeGame.HarmonyPath(FakeGame.Shared), Path.Combine(modDir, "0Harmony.dll"));
        var profile = new GameProfile { ManagedPath = FakeGame.ManagedPath(FakeGame.Shared), HarmonyAssemblyPath = FakeGame.HarmonyPath(FakeGame.Shared) };
        profile.Compilation.ExcludedReferences.Add("System.Console.dll");

        var refs = new GameReferenceResolver().Resolve(profile, new[] { Path.Combine(modDir, "0Harmony.dll") });

        Assert.Single(refs.Paths, p => Path.GetFileName(p) == "0Harmony.dll");
        Assert.Equal(FakeGame.HarmonyPath(FakeGame.Shared), refs.Paths.Single(p => Path.GetFileName(p) == "0Harmony.dll"));
        Assert.DoesNotContain(refs.Paths, p => p.EndsWith("System.Console.dll"));
        Assert.Contains(refs.Paths, p => p.EndsWith("Assembly-CSharp.dll"));
    }
}
