using System.Security.Cryptography;

namespace MadModStudio.Core.IO;

public static class FileUtil
{
    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Enumerates files under root as relative forward-slash paths, skipping excluded files.</summary>
    public static IEnumerable<string> EnumerateRelativeFiles(string root, FileExclusionRules? rules = null)
    {
        if (!Directory.Exists(root)) yield break;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            var rel = PathSafety.GetRelative(root, f);
            if (rules != null && rules.IsExcluded(rel, out _)) continue;
            yield return rel;
        }
    }

    public static int CopyDirectory(string source, string destination, FileExclusionRules? rules = null, ICollection<string>? excluded = null)
    {
        Directory.CreateDirectory(destination);
        var count = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var f in Directory.EnumerateFiles(source, "*", opts))
        {
            var rel = PathSafety.GetRelative(source, f);
            if (rules != null && rules.IsExcluded(rel, out var reason))
            {
                excluded?.Add($"{rel} ({reason})");
                continue;
            }
            var target = Path.Combine(destination, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
            count++;
        }
        return count;
    }

    /// <summary>Deletes a directory tree, clearing read-only flags first. Silently succeeds if it doesn't exist.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(f);
            if ((attr & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attr & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    public static void MakeTreeReadOnly(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.ReadOnly);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of a text file, reporting whether it was truncated.</summary>
    public static string ReadTextLimited(string path, long maxBytes, out bool truncated)
    {
        var info = new FileInfo(path);
        truncated = info.Length > maxBytes;
        if (!truncated) return File.ReadAllText(path);
        using var fs = File.OpenRead(path);
        var buffer = new byte[maxBytes];
        var read = fs.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    public static bool LooksBinary(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[Math.Min(8000, fs.Length)];
        var n = fs.Read(buf, 0, buf.Length);
        for (var i = 0; i < n; i++) if (buf[i] == 0) return true;
        return false;
    }
}
