using MadModStudio.Compiler;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Build;

public sealed class CompileUnit
{
    public string AssemblyName { get; init; } = "";
    public List<string> SourceFiles { get; } = new();
    public CsprojInfo? Project { get; init; }
    /// <summary>Path inside the mod folder where the DLL belongs, relative (e.g. "MyMod.dll" or "Harmony/MyMod.dll").</summary>
    public string OutputRelativePath { get; init; } = "";
    public string LanguageVersion { get; init; } = "9.0";
    public List<string> Defines { get; } = new();
    public bool AllowUnsafe { get; init; }
    public List<string> LocalReferencePaths { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>Works out what to compile in a project: source files, assembly names and where the DLLs go.</summary>
public sealed class CompileInputResolver
{
    public IReadOnlyList<CompileUnit> Resolve(ModProject project, CompilationSettings settings)
    {
        var sourceRoot = project.SourcePath;
        var rules = FileExclusionRules.ForSnapshots();
        var allCs = FileUtil.EnumerateRelativeFiles(sourceRoot, rules).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        if (allCs.Count == 0) return Array.Empty<CompileUnit>();
        var csprojs = FileUtil.EnumerateRelativeFiles(sourceRoot, rules).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(f => CsprojReader.Read(Path.Combine(sourceRoot, f))).Where(c => c.Error is null).ToList();

        var modRoot = project.ModRootPath;
        var existingDlls = Directory.Exists(modRoot)
            ? FileUtil.EnumerateRelativeFiles(modRoot, rules).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();

        var units = new List<CompileUnit>();
        if (csprojs.Count == 0)
        {
            units.Add(MakeUnit(project, settings, null, allCs.Select(f => Path.Combine(sourceRoot, f)), existingDlls));
            return units;
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Deepest project directories first so nested projects claim their own files.
        foreach (var proj in csprojs.OrderByDescending(p => p.Path.Length))
        {
            var dir = Path.GetDirectoryName(proj.Path)!;
            IEnumerable<string> files;
            if (!proj.IsSdkStyle && proj.CompileIncludes.Count > 0 && !proj.CompileIncludes.Any(i => i.Contains('*')))
                files = proj.CompileIncludes.Select(i => Path.GetFullPath(Path.Combine(dir, i))).Where(File.Exists);
            else
                files = allCs.Select(f => Path.Combine(sourceRoot, f)).Where(f => PathSafety.IsUnder(dir, f));
            var removes = proj.CompileRemoves.Select(r => Path.GetFullPath(Path.Combine(dir, r))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var list = files.Where(f => !removes.Contains(Path.GetFullPath(f)) && claimed.Add(Path.GetFullPath(f))).ToList();
            if (list.Count == 0) continue;
            var unit = MakeUnit(project, settings, proj, list, existingDlls);
            // Hint paths that point at DLLs shipped with the source (e.g. lib/Other.dll) become references.
            foreach (var hint in proj.HintPaths)
            {
                var hp = Path.GetFullPath(Path.Combine(dir, hint.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(hp) && PathSafety.IsUnder(sourceRoot, hp)) unit.LocalReferencePaths.Add(hp);
            }
            units.Add(unit);
        }
        var orphans = allCs.Select(f => Path.Combine(sourceRoot, f)).Where(f => !claimed.Contains(Path.GetFullPath(f))).ToList();
        if (orphans.Count > 0 && units.Count > 0)
            units[0].Notes.Add($"{orphans.Count} .cs file(s) are not part of any .csproj and were not compiled: {string.Join(", ", orphans.Take(5).Select(o => Path.GetRelativePath(sourceRoot, o)))}");
        return units;
    }

    private static CompileUnit MakeUnit(ModProject project, CompilationSettings settings, CsprojInfo? proj, IEnumerable<string> files, List<string> existingDlls)
    {
        var assemblyName = proj?.AssemblyName ?? (proj != null ? Path.GetFileNameWithoutExtension(proj.Path) : null);
        string? existing = null;
        if (assemblyName != null)
            existing = existingDlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d).Equals(assemblyName, StringComparison.OrdinalIgnoreCase));
        if (assemblyName is null)
        {
            // Reuse the name of an existing (non-Harmony) DLL in the mod, otherwise derive from the mod folder.
            existing = existingDlls.FirstOrDefault(d => !Path.GetFileName(d).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)
                && AssemblyInspector.IsManagedAssembly(Path.Combine(project.ModRootPath, d)));
            assemblyName = existing != null ? Path.GetFileNameWithoutExtension(existing) : SafeAssemblyName(project.ModFolderName);
        }
        var unit = new CompileUnit
        {
            AssemblyName = assemblyName,
            Project = proj,
            OutputRelativePath = existing ?? assemblyName + ".dll",
            LanguageVersion = NormalizeLang(proj?.LangVersion) ?? settings.LanguageVersion,
            AllowUnsafe = settings.AllowUnsafe || (proj?.AllowUnsafe ?? false),
        };
        unit.SourceFiles.AddRange(files.OrderBy(f => f, StringComparer.Ordinal));
        unit.Defines.AddRange(settings.PreprocessorSymbols);
        if (proj != null) unit.Defines.AddRange(proj.DefineConstants.Where(d => d is not "DEBUG" and not "TRACE"));
        if (proj?.LangVersion != null) unit.Notes.Add($"LangVersion {proj.LangVersion} taken from {Path.GetFileName(proj.Path)}");
        if (proj?.TargetFramework != null) unit.Notes.Add($"{Path.GetFileName(proj.Path)} declares {proj.TargetFramework}; compiling against the game's own assemblies instead.");
        return unit;
    }

    private static string? NormalizeLang(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "default" or "latestmajor" or "latest" or "preview" => "latest",
        var x => x,
    };

    private static string SafeAssemblyName(string name)
    {
        var s = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-').ToArray());
        return s.Length == 0 ? "Mod" : s;
    }
}
