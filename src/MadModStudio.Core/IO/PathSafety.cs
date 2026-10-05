namespace MadModStudio.Core.IO;

public static class PathSafety
{
    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '\\', '/' }).Distinct().ToArray();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="root"/> and returns the full path, or null if the
    /// result would escape the root (path traversal, absolute paths, drive letters) or contains invalid segments.
    /// </summary>
    public static string? ResolveUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            return null;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        foreach (var s in segments)
        {
            if (s == "." || s == "..") return null;
            if (s.IndexOfAny(InvalidNameChars) >= 0) return null;
            if (s.EndsWith(' ') || s.EndsWith('.')) return null;
            var stem = s.Split('.')[0];
            if (ReservedWindowsNames.Contains(stem)) return null;
        }

        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(segments)));
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static bool IsUnder(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Turns arbitrary text into a safe single file/folder name.</summary>
    public static string SanitizeFileName(string name, string fallback = "Mod")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var chars = name.Trim().Select(c => InvalidNameChars.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim('.', ' ');
        if (result.Length == 0 || ReservedWindowsNames.Contains(result)) return fallback;
        return result.Length > 120 ? result[..120] : result;
    }

    public static string ToForwardSlashes(string path) => path.Replace('\\', '/');

    public static string GetRelative(string root, string fullPath) =>
        ToForwardSlashes(Path.GetRelativePath(root, fullPath));
}
