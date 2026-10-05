using MadModStudio.Compiler;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.History;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Validation;
using MadModStudio.Packaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Build;

public sealed class BuildOptions
{
    public BuildConfiguration Configuration { get; init; } = BuildConfiguration.Release;
    /// <summary>When set, the mod version is changed (with a revision) before building.</summary>
    public string? NewVersion { get; init; }
    public bool Package { get; init; } = true;
    /// <summary>Explicit user override: package even when validation reports errors. The ZIP is clearly labeled.</summary>
    public bool PackageWithErrors { get; init; }
    public IReadOnlyList<string> AdditionalReferences { get; init; } = Array.Empty<string>();
    /// <summary>
    /// False for ephemeral evaluation (e.g. comparing AI proposals in a scratch copy): nothing is saved to the database,
    /// no revisions or build records are created and the project status is not changed.
    /// </summary>
    public bool Persist { get; init; } = true;
}

public sealed class BuildResult
{
    public ModAnalysisReport? Analysis { get; set; }
    public List<(CompileUnit Unit, CompileResult Result)> Compiles { get; } = new();
    public ValidationReport? Validation { get; set; }
    public PackageResult? Package { get; set; }
    public List<ValidationFinding> PackageFindings { get; } = new();
    public string? StagingPath { get; set; }
    public bool CompileSkipped { get; set; }
    public bool Succeeded { get; set; }
    public bool PackagedWithErrors { get; set; }
    public BuildRecord? Record { get; set; }
    public List<PipelineEvent> Events { get; } = new();
    public string Summary { get; set; } = "";

    public bool CompileSucceeded => Compiles.All(c => c.Result.Success);
    public IEnumerable<CompilerDiagnostic> AllDiagnostics => Compiles.SelectMany(c => c.Result.Diagnostics);
}

/// <summary>
/// ANALYZE → GENERATE/REPAIR → COMPILE → VALIDATE → PACKAGE. Each stage reports what really happened; a stage is
/// only marked successful when the underlying operation succeeded.
/// </summary>
public sealed class ModBuildPipeline
{
    private readonly ModAnalyzer _analyzer;
    private readonly CompileInputResolver _inputs;
    private readonly GameReferenceResolver _references;
    private readonly IModCompiler _compiler;
    private readonly IEnumerable<IModValidator> _validators;
    private readonly ModPackager _packager;
    private readonly ProjectService _projects;
    private readonly ProjectHistoryService _history;
    private readonly IBuildRecordRepository _builds;
    private readonly IRevisionRepository _revisions;
    private readonly GameProfileService _profiles;
    private readonly ILogger<ModBuildPipeline> _log;

    public ModBuildPipeline(ModAnalyzer analyzer, CompileInputResolver inputs, GameReferenceResolver references, IModCompiler compiler,
        IEnumerable<IModValidator> validators, ModPackager packager, ProjectService projects, ProjectHistoryService history,
        IBuildRecordRepository builds, IRevisionRepository revisions, GameProfileService profiles, ILogger<ModBuildPipeline>? log = null)
    {
        _analyzer = analyzer;
        _inputs = inputs;
        _references = references;
        _compiler = compiler;
        _validators = validators;
        _packager = packager;
        _projects = projects;
        _history = history;
        _builds = builds;
        _revisions = revisions;
        _profiles = profiles;
        _log = log ?? NullLogger<ModBuildPipeline>.Instance;
    }

    public async Task<BuildResult> RunAsync(ModProject project, BuildOptions options, IProgress<PipelineEvent>? progress = null, CancellationToken ct = default)
    {
        var result = new BuildResult();
        var started = DateTimeOffset.UtcNow;
        void Emit(PipelineStage s, StageStatus st, string msg)
        {
            var e = PipelineEvent.Now(s, st, msg);
            result.Events.Add(e);
            progress?.Report(e);
        }

        var profile = project.GameProfileId is { } pid ? await _profiles.GetAsync(pid, ct).ConfigureAwait(false) : null;
        var index = _profiles.GetIndex(profile);

        // ---------------- ANALYZE ----------------
        Emit(PipelineStage.Analyze, StageStatus.Running, "Analyzing mod files...");
        if (options.Persist && options.NewVersion != null && options.NewVersion != project.Version)
        {
            await _projects.SetVersionAsync(project, options.NewVersion, ct).ConfigureAwait(false);
            Emit(PipelineStage.Analyze, StageStatus.Running, $"Version set to {project.Version} (revision recorded).");
        }
        var analysis = _analyzer.Analyze(project.ModRootPath, project.SourcePath, index, profile);
        result.Analysis = analysis;
        project.ModType = analysis.ModType;
        project.SideRequirement = analysis.Side.Result;
        project.EacCompatibility = analysis.Eac.Result;
        if (profile is null) Emit(PipelineStage.Analyze, StageStatus.Warning, "No Game Profile is assigned to this project; game-aware checks and compilation are unavailable.");
        else if (index is null) Emit(PipelineStage.Analyze, StageStatus.Warning, "Game Profile has not been indexed; API/XPath checks against the game are limited. Run Reindex Game.");
        Emit(PipelineStage.Analyze, analysis.Errors > 0 ? StageStatus.Warning : StageStatus.Succeeded,
            $"Analysis complete: {analysis.ModType}, {analysis.XmlFiles.Count} XML, {analysis.Dlls.Count} DLL, {analysis.SourceFiles.Count} source file(s), {analysis.HarmonyPatches.Count} Harmony patch(es), {analysis.Errors} error(s), {analysis.Warnings} warning(s).");

        // ---------------- GENERATE / REPAIR ----------------
        Emit(PipelineStage.GenerateOrRepair, StageStatus.Skipped, "No generation or repair requested for this build.");

        // ---------------- COMPILE ----------------
        var ws = project.Workspace;
        var stagingRoot = Path.Combine(ws.Build, "stage");
        var staging = Path.Combine(stagingRoot, project.ModFolderName);
        var compiledOutputs = new List<(string Dll, string? Pdb, string Rel)>();
        CompileOutcome? compileOutcome = null;
        var units = profile != null ? _inputs.Resolve(project, profile.Compilation) : (analysis.SourceAvailable ? null : Array.Empty<CompileUnit>());

        if (!analysis.SourceAvailable)
        {
            result.CompileSkipped = true;
            Emit(PipelineStage.Compile, StageStatus.Skipped, analysis.Dlls.Count > 0
                ? "No C# source in project; existing DLLs are packaged as-is (not recompiled)."
                : "No C# source in project; compilation not required.");
        }
        else if (profile is null || units is null)
        {
            Emit(PipelineStage.Compile, StageStatus.Failed, "Cannot compile: assign a Game Profile so the game's assemblies can be referenced.");
            compileOutcome = new CompileOutcome(true, false, 1, 0, null,
                new[] { new ModDiagnostic(Severity.Error, "MMS0100", "No Game Profile assigned; cannot resolve game references.", "Compiler") });
        }
        else
        {
            var allDiags = new List<ModDiagnostic>();
            var ok = true;
            foreach (var unit in units)
            {
                ct.ThrowIfCancellationRequested();
                Emit(PipelineStage.Compile, StageStatus.Running, $"Compiling {unit.AssemblyName} ({unit.SourceFiles.Count} file(s), {options.Configuration})...");
                var modLocal = (Directory.Exists(project.ModRootPath)
                    ? Directory.GetFiles(project.ModRootPath, "*.dll", SearchOption.AllDirectories).Concat(unit.LocalReferencePaths)
                    : unit.LocalReferencePaths).ToList();
                // A bundled DLL that declares the same types as this source is a stale build of it, not a dependency:
                // referencing it would cause ambiguous-type errors.
                var declared = SourceTypeScanner.DeclaredTypeNames(unit.SourceFiles);
                foreach (var dll in modLocal.ToList())
                {
                    var r = new MadModStudio.ModAnalysis.Assemblies.AssemblyInspector().Inspect(dll, new MadModStudio.ModAnalysis.Assemblies.AssemblyInspectionOptions { IncludeMembers = false, ComputeHash = false, CollectExternalReferences = false });
                    if (!r.Success || Path.GetFileNameWithoutExtension(dll).Equals(unit.AssemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                    var overlap = r.Types.Select(t => t.FullName).Where(declared.Contains).Take(3).ToList();
                    if (overlap.Count == 0) continue;
                    modLocal.Remove(dll);
                    Emit(PipelineStage.Compile, StageStatus.Warning, $"{Path.GetRelativePath(project.ModRootPath, dll)} declares types that are also in the source ({string.Join(", ", overlap)}); it looks like an old build and was not referenced. It is still included in the package — delete it if it is obsolete.");
                }
                var refs = _references.Resolve(profile, modLocal, options.AdditionalReferences.Concat(project.AdditionalReferencePaths), excludeAssemblyName: unit.AssemblyName);
                var outDir = Path.Combine(ws.Build, "compile", options.Configuration.ToString(), unit.AssemblyName);
                if (Directory.Exists(outDir)) FileUtil.DeleteDirectory(outDir);
                var cr = await _compiler.CompileAsync(new CompileRequest
                {
                    AssemblyName = unit.AssemblyName,
                    SourceFiles = unit.SourceFiles,
                    ReferencePaths = refs.Paths,
                    OutputDirectory = outDir,
                    Configuration = options.Configuration,
                    LanguageVersion = unit.LanguageVersion,
                    PreprocessorSymbols = unit.Defines,
                    AllowUnsafe = unit.AllowUnsafe,
                    EmitPdb = true,
                    SourceRoot = project.SourcePath,
                }, ct).ConfigureAwait(false);
                result.Compiles.Add((unit, cr));
                allDiags.AddRange(cr.Diagnostics.Select(d => d.ToModDiagnostic()));
                foreach (var n in unit.Notes) Emit(PipelineStage.Compile, StageStatus.Running, n);
                if (cr.Success)
                {
                    compiledOutputs.Add((cr.OutputAssemblyPath!, cr.PdbPath, unit.OutputRelativePath));
                    Emit(PipelineStage.Compile, StageStatus.Running, $"{unit.AssemblyName}.dll compiled ({cr.WarningCount} warning(s)) in {cr.Duration.TotalSeconds:F1}s.");
                }
                else
                {
                    ok = false;
                    Emit(PipelineStage.Compile, StageStatus.Failed, $"Compilation failed: {cr.ErrorCount} diagnostic error(s) in {unit.AssemblyName}.");
                }
            }
            compileOutcome = new CompileOutcome(true, ok, allDiags.Count(d => d.Severity == Severity.Error), allDiags.Count(d => d.Severity == Severity.Warning),
                compiledOutputs.FirstOrDefault().Dll, allDiags);
            if (ok) Emit(PipelineStage.Compile, StageStatus.Succeeded, $"Compiled {units.Count} assembly(ies) successfully.");
        }

        // Stage the mod folder exactly as it will be shipped.
        if (Directory.Exists(stagingRoot)) FileUtil.DeleteDirectory(stagingRoot);
        var packageRules = new FileExclusionRules
        {
            IncludeSource = project.IncludeSourceInPackage,
            IncludePdb = options.Configuration == BuildConfiguration.Debug,
        };
        if (Directory.Exists(project.ModRootPath))
            FileUtil.CopyDirectory(project.ModRootPath, staging, packageRules);
        else Directory.CreateDirectory(staging);
        foreach (var (dll, pdb, rel) in compiledOutputs)
        {
            var target = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dll, target, overwrite: true);
            if (pdb != null && packageRules.IncludePdb) File.Copy(pdb, Path.ChangeExtension(target, ".pdb"), overwrite: true);
        }
        result.StagingPath = staging;

        // ---------------- VALIDATE ----------------
        Emit(PipelineStage.Validate, StageStatus.Running, "Validating staged mod folder...");
        var ctx = new ValidationContext
        {
            ModRootPath = staging,
            Project = project,
            GameProfile = profile,
            GameIndex = index,
            Compile = compileOutcome,
        };
        ctx.Items[HarmonyTargetValidator.SourcePatchesKey] = analysis.HarmonyPatches.Where(p => p.Origin == "Source").ToList();
        // Side analysis is redone on the staged output so compiled DLLs are taken into account.
        var stagedAnalysis = _analyzer.Analyze(staging, staging, index, profile);
        ctx.Items[ServerSideValidator.AnalysisKey] = stagedAnalysis;
        project.SideRequirement = stagedAnalysis.Side.Result;
        project.EacCompatibility = stagedAnalysis.Eac.Result;
        var validation = await new ValidationRunner(_validators).RunAsync(ctx, ct).ConfigureAwait(false);
        result.Validation = validation;
        Emit(PipelineStage.Validate, validation.HasErrors ? StageStatus.Failed : validation.WarningCount > 0 ? StageStatus.Warning : StageStatus.Succeeded,
            validation.HasErrors ? $"Validation failed: {validation.ErrorCount} error(s), {validation.WarningCount} warning(s)."
            : $"Validation passed ({validation.WarningCount} warning(s), {validation.ValidatorsRun.Count} checks run).");

        // ---------------- PACKAGE ----------------
        var blocked = validation.HasErrors || (compileOutcome is { Succeeded: false });
        if (!options.Package)
        {
            Emit(PipelineStage.Package, StageStatus.Skipped, "Packaging not requested.");
        }
        else if (blocked && !options.PackageWithErrors)
        {
            Emit(PipelineStage.Package, StageStatus.Failed, $"Packaging blocked: {validation.ErrorCount} validation error(s){(compileOutcome is { Succeeded: false } ? " and compilation failed" : "")}. Fix them, or use 'Package With Errors' to override.");
        }
        else
        {
            Emit(PipelineStage.Package, StageStatus.Running, "Packaging...");
            var baseName = ModPackager.PackageName(project.ModFolderName, project.Version) + (blocked ? "_WITH-ERRORS" : "");
            var pkg = _packager.CreatePackage(new PackageRequest
            {
                ContentDirectory = staging,
                RootFolderName = project.ModFolderName,
                OutputDirectory = ws.Output,
                PackageBaseName = baseName,
                Rules = packageRules,
            });
            result.Package = pkg;
            if (pkg.Success)
            {
                result.PackagedWithErrors = blocked;
                var pctx = new ValidationContext { ModRootPath = staging, Project = project, GameProfile = profile, PackagePath = pkg.ZipPath };
                var pv = await new ValidationRunner(new IModValidator[] { new PackageStructureValidator() }).RunAsync(pctx, ct).ConfigureAwait(false);
                result.PackageFindings.AddRange(pv.Findings);
                if (pv.HasErrors)
                {
                    Emit(PipelineStage.Package, StageStatus.Failed, $"Package created at {pkg.ZipPath} but its structure is invalid: {pv.Findings.First(f => f.Severity == Severity.Error).Message}");
                    result.PackagedWithErrors = true;
                }
                else
                    Emit(PipelineStage.Package, blocked ? StageStatus.Warning : StageStatus.Succeeded,
                        $"{(blocked ? "PACKAGED WITH ERRORS" : "Package created")}: {pkg.ZipPath} ({pkg.FileCount} files, {pkg.ZipSizeBytes:N0} bytes).");
            }
            else Emit(PipelineStage.Package, StageStatus.Failed, pkg.Error ?? "Packaging failed.");
        }

        result.Succeeded = !blocked && (!options.Package || (result.Package?.Success == true && !result.PackagedWithErrors));
        project.Status = result.Package?.Success == true
            ? (result.PackagedWithErrors ? ProjectStatus.PackagedWithErrors : ProjectStatus.Packaged)
            : compileOutcome is { Succeeded: false } ? ProjectStatus.BuildFailed
            : validation.HasErrors ? ProjectStatus.ValidationFailed
            : ProjectStatus.BuildSucceeded;
        if (!options.Persist)
        {
            result.Summary = result.Succeeded ? "Evaluation passed (not persisted)." : "Evaluation did not pass (not persisted).";
            return result;
        }
        await _projects.SaveAsync(project, ct).ConfigureAwait(false);

        var record = new BuildRecord
        {
            ProjectId = project.Id,
            StartedUtc = started,
            FinishedUtc = DateTimeOffset.UtcNow,
            Configuration = options.Configuration,
            Version = project.Version,
            CompileSkipped = result.CompileSkipped,
            CompileSucceeded = compileOutcome?.Succeeded ?? true,
            ErrorCount = validation.ErrorCount,
            WarningCount = validation.WarningCount,
            ValidationOutcome = validation.Outcome,
            PackagePath = result.Package?.Success == true ? result.Package.ZipPath : null,
            PackagedWithErrors = result.PackagedWithErrors,
            Succeeded = result.Succeeded,
        };
        result.Summary = record.Summary = result.Succeeded
            ? $"Build complete: {record.PackagePath ?? "validated (not packaged)"}"
            : result.PackagedWithErrors ? $"Packaged WITH ERRORS: {record.PackagePath}"
            : $"Build did not complete cleanly ({validation.ErrorCount} validation error(s){(compileOutcome is { Succeeded: false } ? ", compilation failed" : "")}).";
        result.Record = await _builds.AddAsync(record, ct).ConfigureAwait(false);

        var status = result.Succeeded ? "Build succeeded" : result.PackagedWithErrors ? "Packaged with errors" : "Build failed";
        var rev = await _history.CreateRevisionAsync(project, $"Build {project.Version}", result.Summary, status, ct: ct).ConfigureAwait(false);
        if (rev is null)
        {
            var latest = (await _history.ListAsync(project, ct).ConfigureAwait(false)).FirstOrDefault();
            if (latest != null) await _revisions.UpdateBuildStatusAsync(latest.Id, status, ct).ConfigureAwait(false);
        }
        _log.LogInformation("Build of {Project} {Version}: {Summary}", project.Name, project.Version, result.Summary);
        return result;
    }
}
