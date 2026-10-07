using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;
using MadModStudio.AI.Secrets;
using MadModStudio.Core;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Deploy;
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
          mms deploy <projectId> [--package <zip>]   copy the latest clean package into the game's Mods folder
          mms deploy list <projectId>
          mms deploy undo <projectId> [--force]       undo the newest active deployment (restores the backup)
          mms logs <logFile>
          mms diagnose <projectId> [--log <file>]... [--working <zip|folder>]
          mms compare <oldModFolder> <newModFolder>
          mms scan <modsFolder> [--profile <id>]   read-only scan (lists available safe fixes)
          mms safe-fix <modsFolder> [--profile <id>] [--deploy]
                                                   fix copies of mods as projects (folder untouched); --deploy installs
                                                   clean results into the profile's Mods folder (backup + undo)

        AI (multi-provider, multi-agent):
          mms ai providers                         provider status (never "Connected" without a real test)
          mms ai key <provider> <key>              store a key (anthropic | openai | google | xai | custom)
          mms ai test [provider]                   real connection test(s)
          mms ai models [--refresh]                discovered models with capability metadata
          mms ai policy [set <Field> <value>]      show/change routing, control, privacy and limits
          mms ai fix <projectId> "<request>" --yes [--approve-all] [--log <file>]... [--working <zip|folder>] [--no-package]
          mms ai create <projectId> "<request>" --yes [--approve-all]
          mms ai agent <projectId> <AgentKind> "<instructions>" --yes [--model provider/model] [--approve-all]
          mms ai handoff <projectId>               structured handoff for switching models
          mms ai performance                       model success rates from your own history
          mms ai spend                             recorded AI spend (unpriced calls are listed, never guessed)
          mms ai price <provider/model> <in> <out> set a model's USD per million tokens ("clear" removes it)

        AI commands send data to AI providers; --yes confirms you accept that. Without --approve-all, proposed
        file changes are rejected (Guided control) unless Agent Control is AUTOMATIC.

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
        var sp0Paths = new AppPaths();
        services.AddPersistence(sp0Paths);
        services.AddGame7DTD();
        services.AddAI(sp0Paths);
        services.AddSingleton<IAIConsentService>(new CliConsent(args.Contains("--yes")));
        services.AddSingleton<IChangeApprovalService>(new CliApproval(args.Contains("--approve-all")));
        await using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<AIPolicy>().CopyFrom(await AIPolicy.LoadAsync(sp.GetRequiredService<MadModStudio.Core.Abstractions.ISettingsRepository>()));

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
                "deploy" => await Deploy(sp, args),
                "logs" => Logs(sp, args),
                "diagnose" => await Diagnose(sp, args),
                "compare" => Compare(sp, args),
                "scan" => await Scan(sp, args),
                "safe-fix" => await SafeFix(sp, args),
                "ai" => await Ai(sp, args),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (Exception ex) when (ex is ImportException or DeployException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
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

    private static async Task<int> Deploy(IServiceProvider sp, string[] args)
    {
        var deploy = sp.GetRequiredService<ModDeployService>();
        var sub = args.ElementAtOrDefault(1);
        var project = await ResolveProject(sp, sub is "list" or "undo" ? args.ElementAtOrDefault(2) ?? "" : sub ?? "");
        var profile = project.GameProfileId is { } pid ? await sp.GetRequiredService<GameProfileService>().GetAsync(pid) : null;
        if (sub == "list")
        {
            var all = await deploy.ListAsync(project);
            if (all.Count == 0) Console.WriteLine("No deployments.");
            foreach (var d in all)
                Console.WriteLine($"{d.Id,4}  {d.DeployedUtc.LocalDateTime:g}  {d.Version,-10} {(d.IsUndone ? "UNDONE" : "ACTIVE"),-7} {d.TargetPath}{(d.BackupPath != null ? "  (backup kept)" : "")}");
            return 0;
        }
        if (profile is null) return Fail("The project has no game profile.");
        if (sub == "undo")
        {
            var latest = (await deploy.ListAsync(project)).FirstOrDefault(d => !d.IsUndone);
            if (latest is null) return Fail("No active deployment to undo.");
            var r = await deploy.UndoAsync(latest, profile, Flag(args, "--force"));
            foreach (var f in r.ModifiedFiles) Console.WriteLine("  changed: " + f);
            Console.WriteLine(r.Message);
            return r.Undone ? 0 : 2;
        }
        var result = await deploy.DeployAsync(project, profile, Opt(args, "--package"));
        foreach (var w in result.Warnings) Console.WriteLine("  WARNING: " + w);
        Console.WriteLine(result.Message);
        return 0;
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
        Console.WriteLine($"{"Mod",-32} {"Version",-10} {"Type",-14} {"XML",4} {"DLL",4} {"Err",4} {"Warn",5} {"Fixes",5}  Status");
        foreach (var r in rows)
        {
            var fixes = SafeFixService.Plan(r.FolderPath);
            Console.WriteLine($"{Trunc(r.Name, 32),-32} {r.Version,-10} {r.Type,-14} {r.XmlCount,4} {r.DllCount,4} {r.Errors,4} {r.Warnings,5} {fixes.Count,5}  {r.Status}{(r.Error != null ? " — " + r.Error : "")}");
            foreach (var f in fixes) Console.WriteLine($"    safe fix: {f.Description}");
        }
        var conflicts = BatchModScanner.FindConflicts(rows, profile);
        Console.WriteLine();
        Console.WriteLine(conflicts.Count == 0 ? "No conflicts between mods." : $"Conflicts between mods ({conflicts.Count}):");
        foreach (var c in conflicts)
        {
            Console.WriteLine($"  {c.Severity.ToString().ToUpperInvariant(),-7} {c.Summary}");
            if (c.Detail.Length > 0) Console.WriteLine($"          {c.Detail}");
        }
        return conflicts.Any(c => c.Severity == Severity.Error) ? 2 : 0;
    }

    private static async Task<int> SafeFix(IServiceProvider sp, string[] args)
    {
        var folder = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Missing Mods folder.");
        if (!Directory.Exists(folder)) return Fail($"Mods folder not found: {folder}");
        var profile = await ResolveProfile(sp, args);
        var deploy = Flag(args, "--deploy");
        if (deploy)
        {
            if (profile is null) return Fail("--deploy needs a game profile.");
            var profileMods = Path.GetFullPath(profile.ModsPath ?? Path.Combine(profile.InstallPath, "Mods")).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(profileMods, Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return Fail($"--deploy only installs into the profile's Mods folder ({profileMods}).");
        }
        var fixer = sp.GetRequiredService<SafeFixService>();
        var deployer = sp.GetRequiredService<ModDeployService>();
        var targets = Directory.GetDirectories(folder).Where(d => SafeFixService.Plan(d).Count > 0).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) { Console.WriteLine("No safe fixes apply to any mod in this folder."); return 0; }
        var failures = 0;
        foreach (var dir in targets)
        {
            var r = await fixer.FixCopyAsync(dir, profile?.Id);
            Console.WriteLine($"{Path.GetFileName(dir)}:");
            foreach (var f in r.Applied) Console.WriteLine($"  fixed: {f.Description}");
            if (r.Error != null) { Console.WriteLine($"  FAILED: {r.Error}"); failures++; continue; }
            Console.WriteLine($"  project {r.Project!.Id}{(r.Reused ? " (reused: folder unchanged since it was fixed)" : "")}: {r.Build?.Summary}");
            if (!r.Packaged) { failures++; continue; }
            if (deploy)
            {
                try
                {
                    var d = await deployer.DeployAsync(r.Project, profile!);
                    foreach (var w in d.Warnings) Console.WriteLine("  WARNING: " + w);
                    Console.WriteLine("  " + d.Message);
                }
                catch (DeployException ex) { Console.WriteLine("  deploy failed: " + ex.Message); failures++; }
            }
        }
        Console.WriteLine(deploy ? "Undo any deployment with: mms deploy undo <projectId>" : "The Mods folder was not modified. Re-run with --deploy to install the clean results (backup + undo).");
        return failures == 0 ? 0 : 2;
    }

    private static async Task<int> Ai(IServiceProvider sp, string[] args)
    {
        var registry = sp.GetRequiredService<IAIProviderRegistry>();
        var catalog = sp.GetRequiredService<IModelCatalog>();
        var policy = sp.GetRequiredService<AIPolicy>();
        var settings = sp.GetRequiredService<MadModStudio.Core.Abstractions.ISettingsRepository>();
        switch (args.ElementAtOrDefault(1))
        {
            case "providers":
                foreach (var p in registry.All)
                    Console.WriteLine($"{p.Id,-10} {p.DisplayName,-28} {(p.IsConfigured ? "Configured (run 'mms ai test' to verify)" : "Not configured"),-44} → {p.Destination}");
                return 0;
            case "key":
            {
                var id = args.ElementAtOrDefault(2) ?? throw new ArgumentException("Missing provider id.");
                var key = args.ElementAtOrDefault(3) ?? throw new ArgumentException("Missing key.");
                if (registry.Get(id) is null) return Fail($"Unknown provider '{id}'.");
                try { sp.GetRequiredService<ISecretStore>().Set(SecretNames.ApiKey(id), key); }
                catch (NotSupportedException ex) { return Fail(ex.Message + " Use the provider's environment variable instead."); }
                Console.WriteLine($"Key stored for {id} ({sp.GetRequiredService<ISecretStore>().Description}). Run 'mms ai test {id}'.");
                return 0;
            }
            case "test":
                foreach (var p in registry.All.Where(p => args.Length < 3 || p.Id == args[2]))
                {
                    var status = await p.TestConnectionAsync();
                    Console.WriteLine($"{p.DisplayName,-28} {status.State,-14} {status.Message}");
                }
                return 0;
            case "models":
                if (Flag(args, "--refresh"))
                    foreach (var r in await catalog.RefreshAsync())
                        Console.WriteLine(r.Success ? $"{r.ProviderId}: {r.ModelCount} model(s) discovered" : $"{r.ProviderId}: {r.Error}");
                Console.WriteLine($"{"Model",-48} {"Tier",-9} {"Context",10} {"Tools",6} {"Price in/out",14}  Source");
                foreach (var m in catalog.Models)
                    Console.WriteLine($"{m.Key,-48} {m.Tier,-9} {(m.ContextTokens?.ToString("N0") ?? "?"),10} {(m.SupportsTools ? "yes" : "no"),6} {(m.CostKnown ? $"{m.InputCostPerMTok}/{m.OutputCostPerMTok}" : "unknown"),14}  {m.MetadataSource}");
                if (catalog.Models.Count == 0) Console.WriteLine("(no models known — configure a provider and run 'mms ai models --refresh')");
                return 0;
            case "policy":
                if (args.ElementAtOrDefault(2) == "set")
                {
                    var field = args.ElementAtOrDefault(3) ?? throw new ArgumentException("Missing field.");
                    var value = args.ElementAtOrDefault(4) ?? throw new ArgumentException("Missing value.");
                    var prop = typeof(AIPolicy).GetProperty(field) ?? throw new ArgumentException($"Unknown field '{field}'.");
                    var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                    object? v = value == "null" ? null : t.IsEnum ? Enum.Parse(t, value, true) : Convert.ChangeType(value, t, System.Globalization.CultureInfo.InvariantCulture);
                    prop.SetValue(policy, v);
                    await policy.SaveAsync(settings);
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(policy, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
                return 0;
            case "fix":
            case "create":
            {
                var project = await ResolveProject(sp, args.ElementAtOrDefault(2) ?? "");
                var request = args.ElementAtOrDefault(3) ?? throw new ArgumentException("Missing request text.");
                var logs = Opts(args, "--log").Select(l => (l, l.Contains("server", StringComparison.OrdinalIgnoreCase) ? "server" : "client")).ToList();
                var result = await sp.GetRequiredService<IAgentCoordinator>().RunWorkflowAsync(new WorkflowRequest
                {
                    Project = project, UserRequest = request, Kind = args[1] == "fix" ? WorkflowKind.Repair : WorkflowKind.Create,
                    Package = !Flag(args, "--no-package"), Logs = logs, WorkingVersionPath = Opt(args, "--working"),
                }, new ConsoleObserver());
                Console.WriteLine();
                Console.WriteLine(result.Report);
                return result.Success ? 0 : 2;
            }
            case "agent":
            {
                var project = await ResolveProject(sp, args.ElementAtOrDefault(2) ?? "");
                var kind = Enum.Parse<AgentKind>(args.ElementAtOrDefault(3) ?? throw new ArgumentException("Missing agent kind."), true);
                var instructions = args.ElementAtOrDefault(4) ?? throw new ArgumentException("Missing instructions.");
                var task = await sp.GetRequiredService<IAgentCoordinator>().RunAgentTaskAsync(project, kind, instructions, Opt(args, "--model"), null, new ConsoleObserver());
                Console.WriteLine($"{task.Agent}: {task.State} — {task.ResultSummary ?? task.Error}");
                return task.State == AgentTaskState.Complete ? 0 : 2;
            }
            case "handoff":
            {
                var project = await ResolveProject(sp, args.ElementAtOrDefault(2) ?? "");
                var profile = project.GameProfileId is { } pid ? await sp.GetRequiredService<GameProfileService>().GetAsync(pid) : null;
                Console.WriteLine(await sp.GetRequiredService<IAIContextBuilder>().BuildHandoffAsync(project, profile, null, null));
                return 0;
            }
            case "performance":
            {
                var stats = await sp.GetRequiredService<IModelPerformanceTracker>().GetStatsAsync();
                if (stats.Count == 0) { Console.WriteLine("INSUFFICIENT DATA — no AI tasks have been recorded yet."); return 0; }
                foreach (var s in stats.OrderBy(s => s.Provider).ThenBy(s => s.Model).ThenBy(s => s.TaskType))
                    Console.WriteLine($"{s.Provider}/{s.Model,-36} {s.TaskType,-22} {(s.HasEnoughData ? $"{s.SuccessRate:P0}" : "INSUFFICIENT DATA"),-18} ({s.Successes}/{s.Total}, reverted {s.Reverted})");
                return 0;
            }
            case "spend":
            {
                var all = await sp.GetRequiredService<MadModStudio.Core.Knowledge.IAISpendRepository>().ListAsync();
                if (all.Count == 0) { Console.WriteLine("No AI calls have been recorded yet."); return 0; }
                string Cost(IEnumerable<MadModStudio.Core.Knowledge.AISpendRecord> rs)
                {
                    var l = rs.ToList();
                    var known = l.Where(r => r.CostUsd != null).ToList();
                    var unknown = l.Count - known.Count;
                    return known.Count == 0 ? "cost unknown" : $"${known.Sum(r => r.CostUsd!.Value):0.00}{(unknown > 0 ? $" + {unknown} unpriced call(s)" : "")}";
                }
                var now = DateTimeOffset.UtcNow;
                Console.WriteLine($"Last 7 days:  {Cost(all.Where(r => r.Utc >= now.AddDays(-7)))}");
                Console.WriteLine($"Last 30 days: {Cost(all.Where(r => r.Utc >= now.AddDays(-30)))}");
                Console.WriteLine($"All time:     {Cost(all)}  ({all.Count} call(s), {all.Sum(r => r.InputTokens):N0} input / {all.Sum(r => r.OutputTokens):N0} output tokens)");
                Console.WriteLine();
                foreach (var g in all.GroupBy(r => $"{r.Provider}/{r.Model}").OrderByDescending(g => g.Sum(r => r.CostUsd ?? 0)))
                    Console.WriteLine($"  {g.Key,-48} {g.Count(),5} call(s)  {Cost(g)}");
                return 0;
            }
            case "price":
            {
                var key = ModelKey.Parse(args.ElementAtOrDefault(2)) ?? throw new ArgumentException("Use: ai price <provider/model> <input $/MTok> <output $/MTok> | clear");
                decimal? input = null, output = null;
                if (args.ElementAtOrDefault(3) != "clear")
                {
                    input = decimal.Parse(args.ElementAtOrDefault(3) ?? throw new ArgumentException("Missing input price."), System.Globalization.CultureInfo.InvariantCulture);
                    output = decimal.Parse(args.ElementAtOrDefault(4) ?? throw new ArgumentException("Missing output price."), System.Globalization.CultureInfo.InvariantCulture);
                }
                sp.GetRequiredService<ModelProfileStore>().SetPrice(key.Provider, key.Model, input, output);
                catalog.Rebuild();
                Console.WriteLine(input is null ? $"Price cleared for {key.Provider}/{key.Model} (cost unknown)." : $"Price set for {key.Provider}/{key.Model}: ${input} in / ${output} out per million tokens.");
                return 0;
            }
            default:
                return Fail("Use: ai providers|key|test|models|policy|fix|create|agent|handoff|performance|spend|price");
        }
    }

    private sealed class ConsoleObserver : IAgentRunObserver
    {
        private readonly Dictionary<string, (AgentTaskState State, string? Activity)> _last = new();
        public void OnEvent(string message) => Console.WriteLine(message);
        public void OnTaskChanged(AgentTaskRecord t)
        {
            AgentTaskState state;
            string? activity;
            lock (t) { state = t.State; activity = t.CurrentActivity; }
            bool stateChanged;
            lock (_last)
            {
                var had = _last.TryGetValue(t.Id, out var last);
                if (had && last.State == state && last.Activity == activity) return;
                stateChanged = !had || last.State != state;
                _last[t.Id] = (state, activity);
            }
            if (stateChanged)
                Console.WriteLine($"  [{state,-11}] {t.Agent,-16} {t.Title}{(t.Model != null ? "  (" + t.Model + ")" : "")}{(t.Error != null ? " — " + t.Error : "")}");
            else if (activity != null)
                Console.WriteLine($"  {"",13} {t.Agent,-16} {activity}");
        }
    }

    private sealed class CliApproval : IChangeApprovalService
    {
        private readonly bool _approve;
        public CliApproval(bool approve) => _approve = approve;
        public Task<bool> RequestAsync(PendingChange change, CancellationToken ct = default)
        {
            Console.WriteLine($"Proposed change from {change.Agent} ({change.Model}): {change.Summary}");
            Console.WriteLine(change.Diff.Length > 4000 ? change.Diff[..4000] + "\n…" : change.Diff);
            Console.WriteLine(_approve ? "→ approved (--approve-all)" : "→ rejected (pass --approve-all to apply changes from the CLI)");
            return Task.FromResult(_approve);
        }
    }

    private sealed class CliConsent : IAIConsentService
    {
        private readonly bool _yes;
        public CliConsent(bool yes) => _yes = yes;
        public Task<bool> ConfirmAsync(AIEgressNotice notice, CancellationToken ct = default)
        {
            Console.WriteLine($"{notice.Operation} may send to {notice.ProviderName} ({notice.Destination}): {string.Join("; ", notice.DataCategories)}");
            if (!_yes) Console.Error.WriteLine("Refusing without --yes.");
            return Task.FromResult(_yes);
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private sealed class SyncProgress : IProgress<PipelineEvent>
    {
        private readonly Action<PipelineEvent> _a;
        public SyncProgress(Action<PipelineEvent> a) => _a = a;
        public void Report(PipelineEvent value) => _a(value);
    }
}
