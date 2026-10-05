namespace MadModStudio.Core.IO;

/// <summary>
/// Decides which files must never be copied into a package or snapshot (VCS folders, IDE settings, build
/// intermediates, logs, databases, secrets...). Paths are relative with forward slashes.
/// </summary>
public sealed class FileExclusionRules
{
    private static readonly string[] AlwaysExcludedDirs =
    {
        ".git", ".svn", ".hg", ".vs", ".vscode", ".idea", "obj", "bin", "packages", "node_modules", "TestResults", "__MACOSX",
    };

    private static readonly string[] AlwaysExcludedExtensions =
    {
        ".user", ".suo", ".tmp", ".temp", ".bak", ".orig", ".swp", ".log", ".db", ".db-shm", ".db-wal", ".sqlite",
        ".sqlite3", ".apikey", ".pem", ".pfx", ".snk", ".cache", ".DotSettings",
    };

    private static readonly string[] AlwaysExcludedFileNames =
    {
        "Thumbs.db", ".DS_Store", "desktop.ini", ".env", "secrets.json", "appsettings.Development.json", ".gitignore",
        ".gitattributes", ".editorconfig",
    };

    private static readonly string[] SourceExtensions = { ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets" };

    public bool IncludeSource { get; init; }
    public bool IncludePdb { get; init; }
    /// <summary>Extra relative directory paths (forward slashes) to exclude entirely.</summary>
    public IReadOnlyList<string> ExtraExcludedDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Rules for history snapshots: keep source, drop only junk.</summary>
    public static FileExclusionRules ForSnapshots() => new() { IncludeSource = true, IncludePdb = false };

    public bool IsExcluded(string relativePath, out string reason)
    {
        var rel = PathSafety.ToForwardSlashes(relativePath).TrimStart('/');
        var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.Length > 0 ? segments[^1] : rel;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (AlwaysExcludedDirs.Any(d => d.Equals(segments[i], StringComparison.OrdinalIgnoreCase)))
            {
                reason = $"folder '{segments[i]}' is never packaged";
                return true;
            }
        }
        foreach (var extra in ExtraExcludedDirectories)
        {
            var e = extra.Trim('/') + "/";
            if (rel.StartsWith(e, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"inside excluded folder '{extra}'";
                return true;
            }
        }
        if (AlwaysExcludedFileNames.Any(n => n.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "IDE/OS/secret file";
            return true;
        }
        if (fileName.StartsWith('~') || fileName.EndsWith('~'))
        {
            reason = "temporary file";
            return true;
        }
        var ext = Path.GetExtension(fileName);
        if (AlwaysExcludedExtensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"'{ext}' files are never packaged";
            return true;
        }
        if (!IncludeSource && SourceExtensions.Any(x => x.Equals(ext, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "source file (source not included in package)";
            return true;
        }
        if (!IncludePdb && ext.Equals(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            reason = "debug symbols not included";
            return true;
        }
        reason = "";
        return false;
    }
}
