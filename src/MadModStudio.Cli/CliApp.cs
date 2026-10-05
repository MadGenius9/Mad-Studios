using MadModStudio.Core;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Logs;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.Game7DTD.Scanner;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MadModStudio.Cli;

/// <summary>
/// Headless front-end over the same services the desktop app uses. Useful for scripting, CI, and verifying the
/// pipeline without the UI.
/// </summary>
public static class CliApp
{
    private const string Usage = """
        Mad Mod Studio CLI (mms)

        Usage:
          mms profile add <gameInstallPath> [--name <name>] [--no-index]
          mms profile list
          mms profile reindex <profileId>
          mms search <query> [--profile <id>]
          mms import <zip|folder> [--profile <id>]
          mms projects
          mms analyze <projectId>
          mms inspect-dll <path> [--decompile <TypeFullName>]
          mms build <projectId> [--version <x.y.z>] [--debug] [--package-with-errors] [--no-package]
          mms history <projectId>
          mms logs <logFile>
          mms diagnose <projectId> [--log <file>]... [--working <zip|folder>]
          mms compare <oldModFolder> <newModFolder>
          mms scan <modsFolder> [--profile <id>]

        Data folder: %LOCALAPPDATA%\MadModStudio (override with MADMODSTUDIO_HOME).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(
            Environment.GetEnvironmentVariable("MMS_VERBOSE") == "1" ? LogLevel.Debug : LogLevel.Warning));
        services.AddPersistence(new AppPaths());
        services.AddGame7DTD();
        await using var sp = services.BuildServiceProvider();

        try
        {
            return args[0] switch
            {
                "profile" => await Profile(sp, args),
                "search" => await Search(sp, args),
                "import" => await Import(sp, args),
                "projects" => await Projects(sp),
                "analyze" => await Analyze(sp, args),
                "inspect-dll" => InspectDll(args),
                "build" => await Build(sp, args),
                "history" => await History(sp, args),
                "logs" => Logs(sp, args),
                "diagnose" => await Diagnose(sp, args),
                "compare" => Compare(sp, args),
                "scan" => await Scan(sp, args),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ImportException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ERROR: " + message);
        return 1;
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IEnumerable<string> Opts(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) yield return args[i + 1];
    }

    private static bool Flag(string[] args, string name) => args.Contains(name);

    private static async Task<GameProfile?> ResolveProfile(IServiceProvider sp, string[] args)
    {
        var svc = sp.GetRequiredService<GameProfileService>();
        var id = Opt(args, "--profile");
        var all = await svc.ListAsync();
        return id != null ? all.FirstOrDefault(p => p.Id.ToString().StartsWith(id, StringComparison.OrdinalIgnoreCase)) : all.FirstOrDefault();
    }

    private static async Task<ModProject> ResolveProject(IServiceProvider sp, string idPrefix)
    {
        var all = await sp.GetRequiredService<ProjectService>().ListAsync();
        var matches = all.Where(p => p.Id.ToString().StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(idPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new ArgumentException($"No project matches '{idPrefix}'."),
            _ => throw new ArgumentException($"'{idPrefix}' matches {matches.Count} projects; use more of the id."),
        };
    }

    private static async Task<int> Profile(IServiceProvider sp, string[] args)
    {
        var svc = sp.GetRequiredService<GameProfileService>();
        var sub = args.ElementAtOrDefault(1);
        switch (sub)
        {
            case "add":
            {
                var path = args.ElementAtOrDefault(2) ?? throw new ArgumentException("Missing game install path.");
                var r = await svc.CreateProfileAsync(path, Opt(args, "--name"));
                foreach (var n in r.Validation.Notes) Console.WriteLine("note: " + n);
                foreach (var w in r.Validation.Warnings) Console.WriteLine("warning: " + w);
                if (r.Profile is null)
                {
                    foreach (var e in r.Validation.Errors) Console.Error.WriteLine("error: " + e);
                    return 1;
                }
                PrintProfile(r.Profile);
                if (r.Version != null) foreach (var e in r.Version.Evidence) Console.WriteLine("  version evidence: " + e);
                if (r.Runtime != null) foreach (var e in r.Runtime.Evidence) Console.WriteLine("  runtime evidence: " + e);
                if (!Flag(args, "--no-index")) return await Reindex(svc, r.Profile);
                return 0;
            }
            case "list":
                foreach (var p in await svc.ListAsync()) PrintProfile(p);
                return 0;
            case "reindex":
            {
                var p = (await svc.ListAsync()).FirstOrDefault(x => x.Id.ToString().StartsWith(args.ElementAtOrDefault(2) ?? "", StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("Profile not found.");
                return await Reindex(svc, p);
            }
            default:
                return Fail("Use: profile add|list|reindex");
        }
    }

    private static async Task<int> Reindex(GameProfileService svc, GameProfile p)
    {
        Console.WriteLine("Indexing game files (metadata only, nothing is executed)...");
        var r = await svc.ReindexAsync(p, new Progress<Game7DTD.Index.IndexProgress>(_ => { }));
        foreach (var w in r.Warnings) Console.WriteLine("warning: " + w);
        if (!r.Success) return Fail(r.Error ?? "Indexing failed.");
        var s = r.Summary!;
        Console.WriteLine($"Indexed in {r.Duration.TotalSeconds:F1}s: {s.Assemblies} assemblies, {s.Types:N0} types, {s.Members:N0} members, {s.XmlFiles} XML files, {s.XmlEntries:N0} XML entries, {s.LocalizationKeys:N0} localization keys.");
        return 0;
    }

    private static void PrintProfile(GameProfile p)
    {
        Console.WriteLine($"""
            Profile {p.Id}
              Name:            {p.Name}
              Game:            {p.GameName}
              Version:         {p.GameVersion ?? "unknown"}  ({p.GameVersionSource})
              Unity:           {p.UnityVersion ?? "unknown"}
              Install path:    {p.InstallPath}
              Mods path:       {p.ModsPath}
              Managed path:    {p.ManagedPath}
              Config path:     {p.ConfigPath}
              Harmony:         {p.HarmonyAssemblyPath ?? "(not found)"}
              Target runtime:  {p.Compilation.DetectedTargetRuntime}
              Last indexed:    {p.LastIndexedUtc?.LocalDateTime.ToString("g") ?? "never"}
              Index status:    {p.IndexStatus}{(p.IndexError != null ? " — " + p.IndexError : "")}
            """);
    }

    private static async Task<int> Search(IServiceProvider sp, string[] args)
    {
        var q = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing query.");
        var profile = await ResolveProfile(sp, args) ?? throw new ArgumentException("No game profile. Run 'mms profile add <path>' first.");
        var index = sp.GetRequiredService<GameProfileService>().GetIndex(profile) ?? throw new InvalidOperationException("Profile is not indexed.");
        foreach (var t in index.SearchTypes(q, 20))
        {
            Console.WriteLine($"{t.Kind} {t.FullName} : {t.BaseType}   [{t.Assembly}]");
            foreach (var m in index.GetMembers(t.FullName).Take(40)) Console.WriteLine($"    {(m.IsPublic ? "public " : "")}{m.Signature}");
        }
        foreach (var x in index.SearchXml(q, 20)) Console.WriteLine($"xml {x.File}:{x.Line} {x.Path}");
        foreach (var l in index.SearchLocalization(q, 10)) Console.WriteLine($"loc {l.Key} = {l.English}");
        return 0;
    }

    private static async Task<int> Import(IServiceProvider sp, string[] args)
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing path.");
        var profile = await ResolveProfile(sp, args);
        var imported = await sp.GetRequiredService<ProjectService>().ImportAsync(path, profile?.Id);
        foreach (var m in imported)
        {
            Console.WriteLine($"Imported project {m.Project.Id}: {m.Project.Name} {m.Project.Version}");
            Console.WriteLine($"  Workspace: {m.Project.WorkspacePath}");
            foreach (var w in m.Warnings) Console.WriteLine("  warning: " + w);
        }
        return 0;
    }

    private static async Task<int> Projects(IServiceProvider sp)
    {
        foreach (var p in await sp.GetRequiredService<ProjectService>().ListAsync())
            Console.WriteLine($"{p.Id.ToString()[..8]}  {p.Name,-30} {p.Version,-10} {p.ModType,-14} {p.Status,-18} {p.ModifiedUtc.LocalDateTime:g}");
        return 0;
    }

    private static async Task<int> Analyze(IServiceProvider sp, string[] args)
    {
        var project = await ResolveProject(sp, args.ElementAtOrDefault(1) ?? "");
        var profiles = sp.GetRequiredService<GameProfileService>();
        var profile = project.GameProfileId is { } id ? await profiles.GetAsync(id) : null;
        var a = sp.GetRequiredService<ModAnalyzer>().Analyze(project.ModRootPath, project.SourcePath, profiles.GetIndex(profile), profile);
        Console.WriteLine(a.Summary());
        if (a.ModInfo != null) Console.WriteLine($"ModInfo ({a.ModInfo.Format}): Name={a.ModInfo.Name} DisplayName={a.ModInfo.DisplayName} Version={a.ModInfo.Version} Author={a.ModInfo.Author}");
        foreach (var d in a.Dlls) Console.WriteLine($"DLL {d.RelativePath}: {d.Assembly.Name} {d.Assembly.Version} {(d.Assembly.ReadError ?? "")}");
        foreach (var h in a.HarmonyPatches) Console.WriteLine($"Harmony [{h.Origin}] {h.PatchKind} {h.TargetDisplay}  ({h.PatchClass}) {h.File}:{h.Line}");
        Console.WriteLine($"Server-side: {a.Side.Result}");
        foreach (var r in a.Side.Reasons) Console.WriteLine("  " + r);
        Console.WriteLine($"EAC: {a.Eac.Result}");
        foreach (var r in a.Eac.Reasons) Console.WriteLine("  " + r);
        foreach (var f in a.Findings) Console.WriteLine($"{f.Severity}: {f.Message} {f.FilePath}");
        return 0;
    }

    private static int InspectDll(string[] args)
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing DLL path.");
        var r = new AssemblyInspector().Inspect(path);
        if (!r.Success) return Fail(r.ReadError ?? "Unreadable assembly.");
        Console.WriteLine($"Assembly: {r.Name} {r.Version}  TFM: {r.TargetFramework ?? "(none)"}  SHA256: {r.Sha256}");
        Console.WriteLine("References: " + string.Join(", ", r.References));
        Console.WriteLine("Namespaces: " + string.Join(", ", r.Namespaces));
        foreach (var t in r.Types)
        {
            Console.WriteLine($"  {t.Kind} {t.FullName} : {t.BaseType}");
            foreach (var m in t.Members) Console.WriteLine($"      {m.Signature}");
        }
        foreach (var h in r.HarmonyPatches) Console.WriteLine($"Harmony: {h.PatchKind} {h.TargetDisplay} ({h.PatchClass}.{h.PatchMethod})");
        foreach (var e in r.ModApiEntryPoints) Console.WriteLine($"IModApi entry point: {e}");
        if (Opt(args, "--decompile") is { } type)
        {
            var d = new DecompilerService().DecompileType(path, type);
            Console.WriteLine(d.Success ? d.Text : d.Error);
        }
        return 0;
    }

    private static async Task<int> Build(IServiceProvider sp, string[] args)
    {
        var project = await ResolveProject(sp, args.ElementAtOrDefault(1) ?? "");
        var result = await sp.GetRequiredService<ModBuildPipeline>().RunAsync(project, new BuildOptions
        {
            Configuration = Flag(args, "--debug") ? BuildConfiguration.Debug : BuildConfiguration.Release,
            NewVersion = Opt(args, "--version"),
            PackageWithErrors = Flag(args, "--package-with-errors"),
            Package = !Flag(args, "--no-package"),
        }, new SyncProgress(e => Console.WriteLine($"[{e.Stage,-16}] {e.Status,-9} {e.Message}")));
        foreach (var (_, c) in result.Compiles)
            foreach (var d in c.Diagnostics) Console.WriteLine("  " + d);
        if (result.Validation != null)
            foreach (var f in result.Validation.Findings.Where(f => f.Severity != Severity.Pass))
                Console.WriteLine($"  {f.Severity,-7} [{f.ValidatorId}] {f.Message}{(f.FilePath != null ? $"  ({f.FilePath}{(f.Line != null ? ":" + f.Line : "")})" : "")}");
        foreach (var f in result.PackageFindings.Where(f => f.Severity != Severity.Pass)) Console.WriteLine($"  {f.Severity} [{f.ValidatorId}] {f.Message}");
        Console.WriteLine(result.Summary);
        return result.Succeeded ? 0 : 2;
    }

    private static async Task<int> History(IServiceProvider sp, string[] args)
    {
        var project = await ResolveProject(sp, args.ElementAtOrDefault(1) ?? "");
        foreach (var r in await sp.GetRequiredService<ProjectService>().History.ListAsync(project))
            Console.WriteLine($"{r.TimestampUtc.LocalDateTime:g}  {r.CommitId[..10]}  {r.Action,-28} {r.BuildStatus,-20} {string.Join(", ", r.ChangedFiles.Take(6))}{(r.ChangedFiles.Count > 6 ? " ..." : "")}");
        return 0;
    }

    private static int Logs(IServiceProvider sp, string[] args)
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing log path.");
        var r = sp.GetRequiredService<LogParser>().Parse(path);
        Console.WriteLine($"{r.TotalLines:N0} lines, {r.ErrorCount} errors, {r.WarningCount} warnings, {r.Groups.Count} distinct problems.");
        foreach (var m in r.LoadedMods) Console.WriteLine("Loaded mod: " + m);
        foreach (var g in r.Groups.Take(50))
        {
            Console.WriteLine($"[{g.Category}] {g.Level} x{g.Count} line {g.First.LineNumber}: {g.First.Message}");
            foreach (var f in g.First.StackTrace.Take(4)) Console.WriteLine($"     at {f.Type}.{f.Method}{(f.File != null ? $" ({f.File}:{f.Line})" : "")}");
        }
        return 0;
    }

    private static async Task<int> Diagnose(IServiceProvider sp, string[] args)
    {
        var project = await ResolveProject(sp, args.ElementAtOrDefault(1) ?? "");
        var inputs = new RepairInputs { WorkingVersionPath = Opt(args, "--working") };
        foreach (var l in Opts(args, "--log")) inputs.Logs.Add((l, l.Contains("server", StringComparison.OrdinalIgnoreCase) ? "server" : "client"));
        var d = await sp.GetRequiredService<RepairService>().DiagnoseAsync(project, inputs);
        Console.WriteLine(d.Report);
        return 0;
    }

    private static int Compare(IServiceProvider sp, string[] args)
    {
        var a = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing old folder.");
        var b = args.ElementAtOrDefault(2) ?? throw new ArgumentException("Missing new folder.");
        Console.WriteLine(sp.GetRequiredService<VersionComparer>().Compare(a, b).Summarize());
        return 0;
    }

    private static async Task<int> Scan(IServiceProvider sp, string[] args)
    {
        var folder = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing Mods folder.");
        var profile = await ResolveProfile(sp, args);
        var rows = await sp.GetRequiredService<BatchModScanner>().ScanAsync(folder, profile);
        Console.WriteLine($"{"Mod",-32} {"Version",-10} {"Type",-14} {"XML",4} {"DLL",4} {"Err",4} {"Warn",5}  Status");
        foreach (var r in rows)
            Console.WriteLine($"{Trunc(r.Name, 32),-32} {r.Version,-10} {r.Type,-14} {r.XmlCount,4} {r.DllCount,4} {r.Errors,4} {r.Warnings,5}  {r.Status}{(r.Error != null ? " — " + r.Error : "")}");
        return 0;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private sealed class SyncProgress : IProgress<PipelineEvent>
    {
        private readonly Action<PipelineEvent> _a;
        public SyncProgress(Action<PipelineEvent> a) => _a = a;
        public void Report(PipelineEvent value) => _a(value);
    }
}
