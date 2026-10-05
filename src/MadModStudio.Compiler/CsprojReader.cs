using System.Xml.Linq;

namespace MadModStudio.Compiler;

/// <summary>Information extracted from a .csproj (SDK-style or legacy). Used as hints only; MSBuild is not executed.</summary>
public sealed class CsprojInfo
{
    public string Path { get; init; } = "";
    public string? AssemblyName { get; set; }
    public string? RootNamespace { get; set; }
    public string? TargetFramework { get; set; }
    public string? LangVersion { get; set; }
    public bool AllowUnsafe { get; set; }
    public bool IsSdkStyle { get; set; }
    public List<string> DefineConstants { get; } = new();
    /// <summary>Explicit Compile includes (legacy projects). Empty for SDK-style projects (implicit globbing).</summary>
    public List<string> CompileIncludes { get; } = new();
    public List<string> CompileRemoves { get; } = new();
    /// <summary>Referenced assembly simple names (from Reference Include / HintPath).</summary>
    public List<string> References { get; } = new();
    public List<string> HintPaths { get; } = new();
    public string? OutputPath { get; set; }
    public string? Error { get; set; }
}

public static class CsprojReader
{
    public static CsprojInfo Read(string path)
    {
        var info = new CsprojInfo { Path = path };
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            info.Error = $"Could not parse project file: {ex.Message}";
            return info;
        }
        var root = doc.Root!;
        info.IsSdkStyle = root.Attribute("Sdk") != null;
        string? Prop(string name) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !string.IsNullOrWhiteSpace(e.Value))?.Value.Trim();

        info.AssemblyName = Prop("AssemblyName");
        info.RootNamespace = Prop("RootNamespace");
        info.TargetFramework = Prop("TargetFramework") ?? Prop("TargetFrameworkVersion") ?? Prop("TargetFrameworks");
        info.LangVersion = Prop("LangVersion");
        info.OutputPath = Prop("OutputPath");
        info.AllowUnsafe = string.Equals(Prop("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase);
        foreach (var d in root.Descendants().Where(e => e.Name.LocalName == "DefineConstants"))
            info.DefineConstants.AddRange(d.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !s.StartsWith("$(")));

        foreach (var c in root.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            var inc = c.Attribute("Include")?.Value;
            var rem = c.Attribute("Remove")?.Value;
            if (inc != null) info.CompileIncludes.Add(inc.Replace('\\', '/'));
            if (rem != null) info.CompileRemoves.Add(rem.Replace('\\', '/'));
        }
        foreach (var r in root.Descendants().Where(e => e.Name.LocalName == "Reference"))
        {
            var inc = r.Attribute("Include")?.Value;
            if (inc != null) info.References.Add(inc.Split(',')[0].Trim());
            var hint = r.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
            if (hint != null) info.HintPaths.Add(hint.Trim());
        }
        return info;
    }
}
