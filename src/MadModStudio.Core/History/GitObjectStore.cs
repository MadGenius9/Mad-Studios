using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MadModStudio.Core.IO;

namespace MadModStudio.Core.History;

public sealed record GitTreeEntry(string Mode, string Name, string Sha)
{
    public bool IsTree => Mode == "40000";
}

public sealed record GitCommit(string Sha, string Tree, IReadOnlyList<string> Parents, string Author, DateTimeOffset Timestamp, string Message);

/// <summary>
/// A minimal, dependency-free implementation of Git's on-disk object format (loose zlib-compressed objects, trees,
/// commits and refs) in a bare repository. Any standard Git client can read it, e.g.
/// <c>git --git-dir=history.git log</c>. Users never need to know Git exists.
/// </summary>
public sealed class GitObjectStore
{
    public const string DefaultBranch = "refs/heads/main";

    public GitObjectStore(string gitDir) => GitDir = gitDir;

    public string GitDir { get; }
    private string ObjectsDir => Path.Combine(GitDir, "objects");

    public bool IsInitialized => File.Exists(Path.Combine(GitDir, "HEAD")) && Directory.Exists(ObjectsDir);

    public void EnsureInitialized()
    {
        if (IsInitialized) return;
        Directory.CreateDirectory(ObjectsDir);
        Directory.CreateDirectory(Path.Combine(ObjectsDir, "info"));
        Directory.CreateDirectory(Path.Combine(ObjectsDir, "pack"));
        Directory.CreateDirectory(Path.Combine(GitDir, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(GitDir, "refs", "tags"));
        File.WriteAllText(Path.Combine(GitDir, "HEAD"), "ref: " + DefaultBranch + "\n");
        File.WriteAllText(Path.Combine(GitDir, "config"), "[core]\n\trepositoryformatversion = 0\n\tfilemode = false\n\tbare = true\n");
        File.WriteAllText(Path.Combine(GitDir, "description"), "Mad Mod Studio project history\n");
    }

    public string WriteObject(string type, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"{type} {content.Length}\0");
        var full = new byte[header.Length + content.Length];
        Buffer.BlockCopy(header, 0, full, 0, header.Length);
        Buffer.BlockCopy(content, 0, full, header.Length, content.Length);
        var sha = Convert.ToHexString(SHA1.HashData(full)).ToLowerInvariant();

        var dir = Path.Combine(ObjectsDir, sha[..2]);
        var path = Path.Combine(dir, sha[2..]);
        if (File.Exists(path)) return sha;
        Directory.CreateDirectory(dir);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
        using (var z = new ZLibStream(fs, CompressionLevel.Optimal))
            z.Write(full, 0, full.Length);
        try { File.Move(temp, path); }
        catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        return sha;
    }

    public (string Type, byte[] Content) ReadObject(string sha)
    {
        ValidateSha(sha);
        var path = Path.Combine(ObjectsDir, sha[..2], sha[2..]);
        if (!File.Exists(path)) throw new FileNotFoundException($"Git object {sha} not found in history.");
        using var fs = File.OpenRead(path);
        using var z = new ZLibStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        var data = ms.ToArray();
        var nul = Array.IndexOf(data, (byte)0);
        if (nul < 0) throw new InvalidDataException($"Corrupt git object {sha}.");
        var header = Encoding.ASCII.GetString(data, 0, nul);
        var space = header.IndexOf(' ');
        var type = header[..space];
        var len = int.Parse(header[(space + 1)..]);
        var content = new byte[len];
        Buffer.BlockCopy(data, nul + 1, content, 0, len);
        return (type, content);
    }

    public string WriteBlob(byte[] content) => WriteObject("blob", content);

    public byte[] ReadBlob(string sha)
    {
        var (type, content) = ReadObject(sha);
        if (type != "blob") throw new InvalidDataException($"Object {sha} is a {type}, not a blob.");
        return content;
    }

    public string WriteTree(IEnumerable<GitTreeEntry> entries)
    {
        // Git orders entries by name, comparing tree names as if they had a trailing '/'.
        var sorted = entries.OrderBy(e => e.IsTree ? e.Name + "/" : e.Name, StringComparer.Ordinal).ToList();
        using var ms = new MemoryStream();
        foreach (var e in sorted)
        {
            var head = Encoding.UTF8.GetBytes($"{e.Mode} {e.Name}\0");
            ms.Write(head);
            ms.Write(Convert.FromHexString(e.Sha));
        }
        return WriteObject("tree", ms.ToArray());
    }

    public IReadOnlyList<GitTreeEntry> ReadTree(string sha)
    {
        var (type, content) = ReadObject(sha);
        if (type != "tree") throw new InvalidDataException($"Object {sha} is a {type}, not a tree.");
        var list = new List<GitTreeEntry>();
        var i = 0;
        while (i < content.Length)
        {
            var space = Array.IndexOf(content, (byte)' ', i);
            var nul = Array.IndexOf(content, (byte)0, space);
            var mode = Encoding.ASCII.GetString(content, i, space - i);
            var name = Encoding.UTF8.GetString(content, space + 1, nul - space - 1);
            var entrySha = Convert.ToHexString(content, nul + 1, 20).ToLowerInvariant();
            list.Add(new GitTreeEntry(mode, name, entrySha));
            i = nul + 21;
        }
        return list;
    }

    public string WriteCommit(string treeSha, string? parentSha, string authorName, string authorEmail, DateTimeOffset when, string message)
    {
        var ts = when.ToUnixTimeSeconds();
        var offset = when.Offset;
        var tz = (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString("hhmm");
        var sb = new StringBuilder();
        sb.Append("tree ").Append(treeSha).Append('\n');
        if (parentSha != null) sb.Append("parent ").Append(parentSha).Append('\n');
        var ident = $"{authorName} <{authorEmail}> {ts} {tz}";
        sb.Append("author ").Append(ident).Append('\n');
        sb.Append("committer ").Append(ident).Append('\n');
        sb.Append('\n').Append(message.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');
        return WriteObject("commit", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public GitCommit ReadCommit(string sha)
    {
        var (type, content) = ReadObject(sha);
        if (type != "commit") throw new InvalidDataException($"Object {sha} is a {type}, not a commit.");
        var text = Encoding.UTF8.GetString(content);
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = text[..split].Split('\n');
        var message = text[(split + 2)..].TrimEnd('\n');
        string tree = "", author = "";
        var parents = new List<string>();
        var when = DateTimeOffset.MinValue;
        foreach (var h in headers)
        {
            if (h.StartsWith("tree ")) tree = h[5..];
            else if (h.StartsWith("parent ")) parents.Add(h[7..]);
            else if (h.StartsWith("author "))
            {
                var a = h[7..];
                var gt = a.LastIndexOf('>');
                author = a[..(gt + 1)];
                var parts = a[(gt + 1)..].Trim().Split(' ');
                if (parts.Length > 0 && long.TryParse(parts[0], out var secs)) when = DateTimeOffset.FromUnixTimeSeconds(secs);
            }
        }
        return new GitCommit(sha, tree, parents, author, when, message);
    }

    public string? ReadRef(string refName = DefaultBranch)
    {
        var path = Path.Combine(GitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;
        var value = File.ReadAllText(path).Trim();
        return value.Length == 40 ? value : null;
    }

    public void UpdateRef(string sha, string refName = DefaultBranch)
    {
        ValidateSha(sha);
        var path = Path.Combine(GitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".lock";
        File.WriteAllText(temp, sha + "\n");
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Stores every (non-excluded) file under <paramref name="directory"/> and returns the root tree id.</summary>
    public string SnapshotDirectory(string directory, FileExclusionRules? rules = null)
    {
        rules ??= FileExclusionRules.ForSnapshots();
        Directory.CreateDirectory(directory);
        return SnapshotRecursive(directory, directory, rules);
    }

    private string SnapshotRecursive(string root, string current, FileExclusionRules rules)
    {
        var entries = new List<GitTreeEntry>();
        foreach (var file in Directory.EnumerateFiles(current))
        {
            var rel = PathSafety.GetRelative(root, file);
            if (rules.IsExcluded(rel, out _)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            entries.Add(new GitTreeEntry("100644", Path.GetFileName(file), WriteBlob(File.ReadAllBytes(file))));
        }
        foreach (var dir in Directory.EnumerateDirectories(current))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            var rel = PathSafety.GetRelative(root, dir) + "/placeholder";
            if (rules.IsExcluded(rel, out _)) continue;
            var sub = SnapshotRecursive(root, dir, rules);
            if (ReadTree(sub).Count == 0) continue; // git doesn't track empty folders
            entries.Add(new GitTreeEntry("40000", Path.GetFileName(dir), sub));
        }
        return WriteTree(entries);
    }

    /// <summary>Flattens a tree into relative path → blob id.</summary>
    public Dictionary<string, string> FlattenTree(string treeSha)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Flatten(treeSha, "", result);
        return result;
    }

    private void Flatten(string treeSha, string prefix, Dictionary<string, string> result)
    {
        foreach (var e in ReadTree(treeSha))
        {
            var path = prefix.Length == 0 ? e.Name : prefix + "/" + e.Name;
            if (e.IsTree) Flatten(e.Sha, path, result);
            else result[path] = e.Sha;
        }
    }

    public IReadOnlyList<GitCommit> Log(string? fromSha = null, int max = 1000)
    {
        var list = new List<GitCommit>();
        var sha = fromSha ?? ReadRef();
        while (sha != null && list.Count < max)
        {
            var c = ReadCommit(sha);
            list.Add(c);
            sha = c.Parents.Count > 0 ? c.Parents[0] : null;
        }
        return list;
    }

    private static void ValidateSha(string sha)
    {
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            throw new ArgumentException($"Invalid object id '{sha}'.");
    }
}
