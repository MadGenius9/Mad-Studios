using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Mods;

namespace MadModStudio.Game7DTD.Validation;

public abstract class ValidatorBase : IModValidator
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public virtual bool IsApplicable(ValidationContext context) => true;

    protected static bool HasDlls(ValidationContext ctx) =>
        Directory.Exists(ctx.ModRootPath) && Directory.EnumerateFiles(ctx.ModRootPath, "*.dll", SearchOption.AllDirectories).Any();

    public Task<IReadOnlyList<ValidationFinding>> ValidateAsync(ValidationContext context, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var findings = new List<ValidationFinding>();
            Validate(context, findings, ct);
            return (IReadOnlyList<ValidationFinding>)findings;
        }, ct);

    protected abstract void Validate(ValidationContext context, List<ValidationFinding> findings, CancellationToken ct);

    protected ValidationFinding F(Severity s, string message, string? file = null, int? line = null, string? evidence = null) =>
        new(Id, s, message, file, line, evidence);

    /// <summary>Optional <c>IEnumerable&lt;string&gt;</c> of other mod folders installed alongside this one (e.g. the batch scanner's folder).</summary>
    public const string OtherModRootsKey = "mods.otherRoots";

    /// <summary>Other mods whose content counts as present: the game's Mods folder plus any supplied by the caller. Excludes this mod.</summary>
    protected static IReadOnlyList<string> OtherModRoots(ValidationContext ctx)
    {
        static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        var self = Normalize(ctx.ModRootPath);
        var roots = new List<string>();
        if (ctx.GameProfile?.ModsPath is { } mods && Directory.Exists(mods)) roots.AddRange(Directory.GetDirectories(mods));
        if (ctx.Items.TryGetValue(OtherModRootsKey, out var extra) && extra is IEnumerable<string> list) roots.AddRange(list);
        return roots.Where(r => Directory.Exists(Path.Combine(r, "Config")))
            .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !r.Equals(self, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// True/false when both folders are in the same Mods folder and <paramref name="a"/> loads before <paramref name="b"/>;
    /// null when that can't be told. The game sorts each Mods folder with Array.Sort (culture-aware string order) and
    /// loads mods in that order (verified in V3.30's ModManager.loadModsFromFolder).
    /// </summary>
    protected static bool? LoadsBefore(string a, string b)
    {
        var pa = Path.GetDirectoryName(Path.GetFullPath(a));
        var pb = Path.GetDirectoryName(Path.GetFullPath(b));
        if (pa is null || !string.Equals(pa, pb, StringComparison.OrdinalIgnoreCase)) return null;
        return string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.InvariantCulture) < 0;
    }

    /// <summary>
    /// Why the game would refuse to load the mod in <paramref name="modRoot"/>, or null if it would load it. Mirrors the
    /// checks in V3.30's Mod.LoadDefinitionFromFolder: a ModInfo.xml must exist, parse, have a root, not nest a legacy
    /// &lt;ModInfo&gt; element (V3+), and give a Name.
    /// </summary>
    protected static string? WontLoadReason(string modRoot, GameProfile? profile)
    {
        var path = Path.Combine(modRoot, ModInfoFile.FileName);
        if (!File.Exists(path)) return "it has no ModInfo.xml";
        try
        {
            var root = System.Xml.Linq.XDocument.Load(path).Root;
            if (root is null) return "its ModInfo.xml has no root element";
            if (root.Element("ModInfo") != null) return GameMajorVersion(profile) >= 3 ? "its ModInfo.xml uses the legacy layout this game version rejects" : null;
            if (string.IsNullOrWhiteSpace((string?)root.Element("Name")?.Attribute("value"))) return "its ModInfo.xml has no Name";
            return null;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { return "its ModInfo.xml is malformed"; }
    }

    /// <summary>Major game version from a profile's version text such as "V 3.30 (b18)", or null if unknown.</summary>
    protected static int? GameMajorVersion(GameProfile? profile) =>
        profile?.GameVersion is { } v && System.Text.RegularExpressions.Regex.Match(v, @"(\d+)\.\d+") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    protected static IEnumerable<string> ModFiles(ValidationContext ctx) =>
        FileUtil.EnumerateRelativeFiles(ctx.ModRootPath, new FileExclusionRules { IncludeSource = true, IncludePdb = true });
}
