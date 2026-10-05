using System.IO.Compression;

namespace MadModStudio.Core.IO;

public sealed class ZipSafetyException : Exception
{
    public ZipSafetyException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ZipExtractionLimits
{
    public long MaxTotalUncompressedBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaxEntryUncompressedBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 100_000;
    /// <summary>Entries whose compression ratio exceeds this (and are larger than 1 MB) are treated as zip bombs.</summary>
    public double MaxCompressionRatio { get; init; } = 200;
}

public sealed record ZipExtractionResult(int FilesExtracted, long BytesExtracted, IReadOnlyList<string> SkippedEntries);

/// <summary>ZIP extraction/creation that treats archives as untrusted input.</summary>
public static class SafeZip
{
    /// <summary>
    /// Extracts <paramref name="zipPath"/> into <paramref name="destination"/>. Rejects path traversal, absolute paths,
    /// device names and zip bombs. The source archive is opened read-only and never modified.
    /// </summary>
    public static ZipExtractionResult Extract(string zipPath, string destination, ZipExtractionLimits? limits = null)
    {
        limits ??= new ZipExtractionLimits();
        if (!File.Exists(zipPath)) throw new ZipSafetyException($"Archive not found: {zipPath}");
        Directory.CreateDirectory(destination);

        ZipArchive archive;
        FileStream stream;
        try
        {
            stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new ZipSafetyException($"'{Path.GetFileName(zipPath)}' is not a valid ZIP archive or is corrupt: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new ZipSafetyException($"Could not open '{Path.GetFileName(zipPath)}': {ex.Message}", ex);
        }

        using (archive)
        {
            if (archive.Entries.Count > limits.MaxEntries)
                throw new ZipSafetyException($"Archive contains {archive.Entries.Count} entries, more than the allowed {limits.MaxEntries}.");

            long total = 0;
            var count = 0;
            var skipped = new List<string>();
            foreach (var entry in archive.Entries)
            {
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var target = PathSafety.ResolveUnderRoot(destination, entry.FullName.TrimEnd('/', '\\'));
                if (target is null)
                {
                    if (!string.IsNullOrEmpty(entry.FullName.Trim('/', '\\')))
                        throw new ZipSafetyException($"Unsafe path in archive rejected: '{entry.FullName}'. The archive may be malicious.");
                    continue;
                }
                if (isDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (entry.Length > limits.MaxEntryUncompressedBytes)
                    throw new ZipSafetyException($"Entry '{entry.FullName}' is too large ({entry.Length:N0} bytes).");
                if (entry.Length > 1024 * 1024 && entry.CompressedLength > 0 &&
                    (double)entry.Length / entry.CompressedLength > limits.MaxCompressionRatio)
                    throw new ZipSafetyException($"Entry '{entry.FullName}' has a suspicious compression ratio (possible zip bomb).");

                total += entry.Length;
                if (total > limits.MaxTotalUncompressedBytes)
                    throw new ZipSafetyException("Archive expands beyond the allowed total size.");

                if (File.Exists(target))
                {
                    skipped.Add($"{entry.FullName} (duplicate entry)");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    using var src = entry.Open();
                    using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                    CopyBounded(src, dst, entry.Length > 0 ? entry.Length : limits.MaxEntryUncompressedBytes, entry.FullName);
                }
                catch (InvalidDataException ex)
                {
                    throw new ZipSafetyException($"Entry '{entry.FullName}' is corrupt: {ex.Message}", ex);
                }
                count++;
            }
            return new ZipExtractionResult(count, total, skipped);
        }
    }

    private static void CopyBounded(Stream src, Stream dst, long declaredLength, string name)
    {
        var buffer = new byte[81920];
        long written = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += n;
            if (written > declaredLength)
                throw new ZipSafetyException($"Entry '{name}' expanded beyond its declared size.");
            dst.Write(buffer, 0, n);
        }
    }

    /// <summary>
    /// Creates a ZIP whose entries are placed under <paramref name="rootFolderName"/> (pass null/empty for no root folder).
    /// Writes to a temp file first so a failure never leaves a half-written archive at the destination.
    /// </summary>
    public static int Create(string sourceDirectory, string zipPath, string? rootFolderName, FileExclusionRules? rules = null, ICollection<string>? excluded = null)
    {
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException(sourceDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
        var temp = zipPath + ".partial";
        if (File.Exists(temp)) File.Delete(temp);
        var count = 0;
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var rel in FileUtil.EnumerateRelativeFiles(sourceDirectory).OrderBy(r => r, StringComparer.Ordinal))
            {
                if (rules != null && rules.IsExcluded(rel, out var reason))
                {
                    excluded?.Add($"{rel} ({reason})");
                    continue;
                }
                var entryName = string.IsNullOrEmpty(rootFolderName) ? rel : rootFolderName.TrimEnd('/') + "/" + rel;
                zip.CreateEntryFromFile(Path.Combine(sourceDirectory, rel), entryName, CompressionLevel.Optimal);
                count++;
            }
        }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(temp, zipPath);
        return count;
    }

    public static IReadOnlyList<string> ListEntries(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Select(e => e.FullName).ToList();
        }
        catch (InvalidDataException ex)
        {
            throw new ZipSafetyException($"'{Path.GetFileName(zipPath)}' is not a valid ZIP archive: {ex.Message}", ex);
        }
    }
}
