using MadModStudio.Core.IO;

namespace MadModStudio.Packaging;

public sealed class PackageRequest
{
    /// <summary>Folder whose contents become the single top-level folder in the ZIP.</summary>
    public required string ContentDirectory { get; init; }
    /// <summary>Name of the top-level folder inside the ZIP (the mod folder name).</summary>
    public required string RootFolderName { get; init; }
    public required string OutputDirectory { get; init; }
    /// <summary>File name without extension, e.g. "MadWorkingRacks_1.0.9".</summary>
    public required string PackageBaseName { get; init; }
    public FileExclusionRules Rules { get; init; } = new();
    public bool OverwriteExisting { get; init; } = true;
}

public sealed class PackageResult
{
    public bool Success { get; init; }
    public string? ZipPath { get; init; }
    public int FileCount { get; init; }
    public long ZipSizeBytes { get; init; }
    public IReadOnlyList<string> ExcludedFiles { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

/// <summary>Builds installable mod ZIPs with the correct folder hierarchy and no development artifacts.</summary>
public sealed class ModPackager
{
    public PackageResult CreatePackage(PackageRequest request)
    {
        try
        {
            if (!Directory.Exists(request.ContentDirectory))
                return new PackageResult { Error = $"Content folder does not exist: {request.ContentDirectory}" };

            var root = PathSafety.SanitizeFileName(request.RootFolderName);
            var baseName = PathSafety.SanitizeFileName(request.PackageBaseName);
            Directory.CreateDirectory(request.OutputDirectory);
            var zipPath = Path.Combine(request.OutputDirectory, baseName + ".zip");
            if (File.Exists(zipPath) && !request.OverwriteExisting)
                return new PackageResult { Error = $"A package already exists at {zipPath}." };

            var excluded = new List<string>();
            var count = SafeZip.Create(request.ContentDirectory, zipPath, root, request.Rules, excluded);
            if (count == 0)
            {
                File.Delete(zipPath);
                return new PackageResult { Error = "No files to package after exclusions.", ExcludedFiles = excluded };
            }
            return new PackageResult
            {
                Success = true,
                ZipPath = zipPath,
                FileCount = count,
                ZipSizeBytes = new FileInfo(zipPath).Length,
                ExcludedFiles = excluded,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ZipSafetyException)
        {
            return new PackageResult { Error = $"Packaging failed: {ex.Message}" };
        }
    }

    /// <summary>Builds the standard package file name, e.g. "MadWorkingRacks_1.0.9".</summary>
    public static string PackageName(string modName, string version) =>
        PathSafety.SanitizeFileName(modName.Replace(' ', '_')) + "_" + PathSafety.SanitizeFileName(version, "0.0.0");
}

public sealed record PackageInspection(
    bool IsValidZip,
    IReadOnlyList<string> TopLevelFolders,
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> ModInfoLocations,
    IReadOnlyList<string> ForbiddenEntries,
    int EntryCount,
    string? Error);

public static class PackageInspector
{
    /// <summary>Inspects a ZIP's structure without extracting it.</summary>
    public static PackageInspection Inspect(string zipPath, FileExclusionRules? forbidden = null)
    {
        forbidden ??= new FileExclusionRules { IncludeSource = true, IncludePdb = true };
        IReadOnlyList<string> entries;
        try { entries = SafeZip.ListEntries(zipPath); }
        catch (Exception ex) when (ex is ZipSafetyException or IOException)
        {
            return new PackageInspection(false, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, ex.Message);
        }
        var files = entries.Where(e => !e.EndsWith('/')).Select(e => e.Replace('\\', '/')).ToList();
        var top = files.Where(f => f.Contains('/')).Select(f => f[..f.IndexOf('/')]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rootFiles = files.Where(f => !f.Contains('/')).ToList();
        var modInfos = files.Where(f => Path.GetFileName(f).Equals("ModInfo.xml", StringComparison.OrdinalIgnoreCase)).ToList();
        var bad = files.Where(f => forbidden.IsExcluded(f, out _)).ToList();
        return new PackageInspection(true, top, rootFiles, modInfos, bad, files.Count, null);
    }
}
