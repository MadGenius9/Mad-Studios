using MadModStudio.Core;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Mods;

public sealed class ImportException : Exception
{
    public ImportException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ImportedMod
{
    public required ModProject Project { get; init; }
    public ModInfoData? ModInfo { get; init; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Imports a mod folder or ZIP into a fresh project workspace. The original is copied into original/ untouched (and
/// marked read-only); the editable copy goes into source/. Imported content is treated as untrusted.
/// </summary>
public sealed class ModImporter
{
    private readonly AppPaths _paths;
    private readonly ILogger<ModImporter> _log;

    public ModImporter(AppPaths paths, ILogger<ModImporter>? log = null)
    {
        _paths = paths;
        _log = log ?? NullLogger<ModImporter>.Instance;
    }

    public Task<IReadOnlyList<ImportedMod>> ImportAsync(string sourcePath, Guid? gameProfileId, ProjectOrigin origin = ProjectOrigin.Imported, CancellationToken ct = default) =>
        Task.Run(() => Import(sourcePath, gameProfileId, origin, ct), ct);

    private IReadOnlyList<ImportedMod> Import(string sourcePath, Guid? gameProfileId, ProjectOrigin origin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ImportException("No file or folder was selected.");
        var full = Path.GetFullPath(sourcePath);
        var isZip = File.Exists(full) && Path.GetExtension(full).Equals(".zip", StringComparison.OrdinalIgnoreCase);
        var isDir = Directory.Exists(full);
        if (!isZip && !isDir)
            throw new ImportException(File.Exists(full) ? "Only .zip archives or folders can be imported." : $"Not found: {full}");

        _paths.EnsureCreated();
        // Stage: extract/copy once into a scratch area, then build one workspace per mod found.
        var staging = Path.Combine(_paths.Scratch, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            if (isZip)
            {
                try { SafeZip.Extract(full, staging); }
                catch (ZipSafetyException ex) { throw new ImportException(ex.Message, ex); }
            }
            else
            {
                if (PathSafety.IsUnder(_paths.Root, full))
                    throw new ImportException("Cannot import a folder from inside Mad Mod Studio's own workspace.");
                FileUtil.CopyDirectory(full, Path.Combine(staging, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))));
            }
            ct.ThrowIfCancellationRequested();

            var contentRoot = UnwrapSingleFolder(staging);
            var modInfos = ModInfoFile.FindAll(contentRoot);
            var results = new List<ImportedMod>();
            var sourceName = Path.GetFileNameWithoutExtension(full.TrimEnd(Path.DirectorySeparatorChar));

            if (modInfos.Count <= 1)
            {
                var modRoot = modInfos.Count == 1 ? Path.GetDirectoryName(modInfos[0])! : contentRoot;
                results.Add(CreateProject(full, isZip, contentRoot, modRoot, sourceName, gameProfileId, origin,
                    modInfos.Count == 0 ? "No ModInfo.xml was found; the project was imported as Unknown." : null));
            }
            else
            {
                // A mod pack: one project per mod folder, each containing only that mod.
                foreach (var mi in modInfos)
                {
                    var modRoot = Path.GetDirectoryName(mi)!;
                    if (modInfos.Any(o => o != mi && PathSafety.IsUnder(Path.GetDirectoryName(o)!, modRoot)))
                        continue; // nested inside another mod; reported by validation of the parent
                    results.Add(CreateProject(full, isZip, modRoot, modRoot, sourceName, gameProfileId, origin,
                        $"Imported from a package containing {modInfos.Count} mods."));
                }
            }
            return results;
        }
        finally
        {
            try { FileUtil.DeleteDirectory(staging); } catch (IOException ex) { _log.LogWarning(ex, "Could not clean import staging {Path}", staging); }
        }
    }

    private ImportedMod CreateProject(string originalPath, bool isZip, string contentRoot, string modRoot, string sourceName,
        Guid? gameProfileId, ProjectOrigin origin, string? warning)
    {
        var project = new ModProject { GameProfileId = gameProfileId, Origin = origin, ImportedFrom = originalPath };
        project.WorkspacePath = _paths.ProjectWorkspace(project.Id);
        var ws = project.Workspace;
        ws.EnsureCreated();

        // 1. Preserve the original exactly as provided.
        if (isZip)
        {
            var dest = Path.Combine(ws.Original, Path.GetFileName(originalPath));
            File.Copy(originalPath, dest);
        }
        else
        {
            FileUtil.CopyDirectory(originalPath, Path.Combine(ws.Original, Path.GetFileName(originalPath.TrimEnd(Path.DirectorySeparatorChar))));
        }
        FileUtil.MakeTreeReadOnly(ws.Original);

        // 2. Editable working copy.
        FileUtil.CopyDirectory(contentRoot, ws.Source);
        project.ModRootRelativePath = PathSafety.IsUnder(contentRoot, modRoot) ? PathSafety.GetRelative(contentRoot, modRoot) : "";
        if (project.ModRootRelativePath == ".") project.ModRootRelativePath = "";

        ModInfoData? info = null;
        var modInfoPath = Path.Combine(project.ModRootPath, ModInfoFile.FileName);
        string? modInfoError = null;
        if (File.Exists(modInfoPath)) info = ModInfoFile.TryParse(modInfoPath, out modInfoError);

        var folderName = string.IsNullOrEmpty(project.ModRootRelativePath)
            ? (Path.GetFileName(contentRoot) is { Length: > 0 } n && !n.StartsWith("import-") ? n : info?.Name ?? sourceName)
            : Path.GetFileName(modRoot);
        project.ModFolderName = PathSafety.SanitizeFileName(folderName);
        project.Name = info?.DisplayName ?? info?.Name ?? project.ModFolderName;
        project.Version = info?.Version ?? "1.0.0";
        project.Description = info?.Description;
        project.Author = info?.Author;
        project.Status = ProjectStatus.Imported;

        var result = new ImportedMod { Project = project, ModInfo = info };
        if (warning != null) result.Warnings.Add(warning);
        if (modInfoError != null) result.Warnings.Add(modInfoError);
        _log.LogInformation("Imported {Name} {Version} into {Workspace}", project.Name, project.Version, project.WorkspacePath);
        return result;
    }

    /// <summary>Descends through wrapper folders that contain exactly one folder and nothing else.</summary>
    private static string UnwrapSingleFolder(string dir)
    {
        var current = dir;
        for (var i = 0; i < 5; i++)
        {
            var entries = Directory.GetFileSystemEntries(current).Where(e => Path.GetFileName(e) != "__MACOSX").ToList();
            if (entries.Count == 1 && Directory.Exists(entries[0]) && !File.Exists(Path.Combine(current, ModInfoFile.FileName)))
                current = entries[0];
            else break;
        }
        // Keep the folder whose name is the mod folder: if current itself holds ModInfo.xml, its parent is the wrapper.
        return current;
    }
}
