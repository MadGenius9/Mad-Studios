using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Names defined by config XML files (items, blocks, item modifiers, loot groups), read from the game's Config folder and
/// cached per file (path + size + timestamp): the game's items.xml/blocks.xml are large and many mods are checked in a row.
/// </summary>
public static class XmlNameCatalog
{
    /// <summary>Which element names define referencable things, per config file.</summary>
    public static readonly (string File, string Element)[] Definitions =
    {
        ("items.xml", "item"), ("blocks.xml", "block"), ("item_modifiers.xml", "item_modifier"), ("loot.xml", "lootgroup"),
    };

    private sealed record Entry(long Length, DateTime Written, IReadOnlyDictionary<string, HashSet<string>> Names);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Element name → defined `name` attribute values in one XML file (empty if missing or unreadable).</summary>
    public static IReadOnlyDictionary<string, HashSet<string>> Read(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, HashSet<string>>();
        var info = new FileInfo(path);
        if (Cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc) return hit.Names;
        var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try
        {
            foreach (var e in XDocument.Load(path).Descendants())
                if (e.Attribute("name")?.Value is { Length: > 0 } n)
                {
                    if (!names.TryGetValue(e.Name.LocalName, out var set)) names[e.Name.LocalName] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(n);
                }
        }
        catch (Exception ex) when (ex is XmlException or IOException) { }
        Cache[path] = new Entry(info.Length, info.LastWriteTimeUtc, names);
        return names;
    }

    /// <summary>All names of <paramref name="element"/> defined in <paramref name="file"/> under each of the given Config folders.</summary>
    public static HashSet<string> Collect(IEnumerable<string> configFolders, string file, string element)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in configFolders)
            if (Read(Path.Combine(dir, file)).TryGetValue(element, out var set)) all.UnionWith(set);
        return all;
    }
}
