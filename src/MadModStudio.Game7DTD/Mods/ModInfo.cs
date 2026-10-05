using System.Xml;
using System.Xml.Linq;

namespace MadModStudio.Game7DTD.Mods;

public enum ModInfoFormat
{
    Unknown = 0,
    /// <summary>Legacy format: &lt;xml&gt;&lt;ModInfo&gt;...&lt;/ModInfo&gt;&lt;/xml&gt;.</summary>
    V1 = 1,
    /// <summary>Current format (A21+): fields directly under &lt;xml&gt;.</summary>
    V2 = 2,
}

public sealed class ModInfoData
{
    public string FilePath { get; init; } = "";
    public ModInfoFormat Format { get; init; }
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Website { get; init; }
    public IReadOnlyDictionary<string, string> AllFields { get; init; } = new Dictionary<string, string>();

    public string EffectiveName => DisplayName ?? Name ?? Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? "Unnamed Mod";
}

public sealed class ModInfoParseException : Exception
{
    public ModInfoParseException(string message, int? line = null, Exception? inner = null) : base(message, inner) => Line = line;
    public int? Line { get; }
}

/// <summary>Reads and updates 7 Days to Die ModInfo.xml files in both known layouts.</summary>
public static class ModInfoFile
{
    public const string FileName = "ModInfo.xml";

    public static ModInfoData Parse(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
        catch (XmlException ex) { throw new ModInfoParseException($"ModInfo.xml is malformed: {ex.Message}", ex.LineNumber, ex); }
        catch (IOException ex) { throw new ModInfoParseException($"ModInfo.xml could not be read: {ex.Message}", null, ex); }

        var root = doc.Root ?? throw new ModInfoParseException("ModInfo.xml has no root element.");
        XElement container;
        ModInfoFormat format;
        var legacy = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase));
        if (legacy != null) { container = legacy; format = ModInfoFormat.V1; }
        else if (root.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase)) { container = root; format = ModInfoFormat.V1; }
        else { container = root; format = ModInfoFormat.V2; }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in container.Elements())
        {
            var v = e.Attribute("value")?.Value ?? e.Value;
            fields.TryAdd(e.Name.LocalName, v.Trim());
        }
        if (fields.Count == 0) format = ModInfoFormat.Unknown;

        string? F(string key) => fields.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        return new ModInfoData
        {
            FilePath = path,
            Format = format,
            Name = F("Name"),
            DisplayName = F("DisplayName"),
            Version = F("Version"),
            Description = F("Description"),
            Author = F("Author"),
            Website = F("Website"),
            AllFields = fields,
        };
    }

    public static ModInfoData? TryParse(string path, out string? error)
    {
        try
        {
            error = null;
            return Parse(path);
        }
        catch (ModInfoParseException ex)
        {
            error = ex.Line is null ? ex.Message : $"{ex.Message} (line {ex.Line})";
            return null;
        }
    }

    /// <summary>Sets the Version value, preserving the existing layout, formatting and other fields.</summary>
    public static void SetVersion(string path, string version)
    {
        var doc = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new ModInfoParseException("ModInfo.xml has no root element.");
        var container = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ModInfo", StringComparison.OrdinalIgnoreCase)) ?? root;
        var el = container.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Version", StringComparison.OrdinalIgnoreCase));
        if (el is null)
        {
            el = new XElement("Version");
            var last = container.Elements().LastOrDefault();
            if (last != null) last.AddAfterSelf(new XText("\n" + Indent(last)), el);
            else container.Add(el);
        }
        el.SetAttributeValue("value", version);
        Save(doc, path);
    }

    /// <summary>Writes a new ModInfo.xml in the current (V2) format.</summary>
    public static void Create(string path, string name, string displayName, string version, string? description, string? author, string? website = null)
    {
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("xml",
                new XElement("Name", new XAttribute("value", name)),
                new XElement("DisplayName", new XAttribute("value", displayName)),
                new XElement("Version", new XAttribute("value", version)),
                new XElement("Description", new XAttribute("value", description ?? "")),
                new XElement("Author", new XAttribute("value", author ?? "")),
                new XElement("Website", new XAttribute("value", website ?? ""))));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(doc, path);
    }

    private static string Indent(XElement e) =>
        e.PreviousNode is XText t ? t.Value.Replace("\r", "").Split('\n').Last() : "  ";

    private static void Save(XDocument doc, string path)
    {
        var settings = new XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), Indent = false, OmitXmlDeclaration = doc.Declaration is null };
        var temp = path + ".tmp";
        using (var w = XmlWriter.Create(temp, settings)) doc.Save(w);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Increments the last numeric component of a version string ("1.0.8" -> "1.0.9"). Returns null if not numeric.</summary>
    public static string? IncrementVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "1.0.0";
        var parts = version.Trim().Split('.');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (int.TryParse(parts[i], out var n))
            {
                parts[i] = (n + 1).ToString();
                return string.Join('.', parts);
            }
        }
        return null;
    }

    /// <summary>Finds ModInfo.xml files under a folder (bounded depth), ignoring junk folders.</summary>
    public static IReadOnlyList<string> FindAll(string root, int maxDepth = 6)
    {
        var result = new List<string>();
        Walk(root, 0);
        return result;

        void Walk(string dir, int depth)
        {
            if (depth > maxDepth) return;
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            var modInfo = files.FirstOrDefault(f => Path.GetFileName(f).Equals(FileName, StringComparison.OrdinalIgnoreCase));
            if (modInfo != null) result.Add(modInfo);
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d);
                if (n is "__MACOSX" or ".git" or "obj" or "bin" or ".vs") continue;
                Walk(d, depth + 1);
            }
        }
    }
}
