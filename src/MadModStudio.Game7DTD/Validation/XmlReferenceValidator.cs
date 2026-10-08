using System.Xml;
using System.Xml.Linq;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Checks the names a mod's recipes and loot refer to: a recipe's output and ingredients must be an item, block or item
/// modifier, a loot entry's item must exist and its group must be a defined lootgroup. Names are resolved against the
/// game's Config folder and this mod; a name only another mod defines is a dependency on that mod (reported as such),
/// and a name nobody defines is a warning. Names are case-sensitive, so near-misses get a hint.
/// </summary>
public sealed class XmlReferenceValidator : ValidatorBase
{
    /// <summary>Optional <c>IEnumerable&lt;string&gt;</c> of other mod folders checked alongside this one (e.g. by the batch scanner).</summary>
    public const string OtherModRootsKey = "mods.otherRoots";
    private const int MaxListedPerFile = 10;

    public override string Id => "xml-references";
    public override string DisplayName => "XML references (recipes, ingredients, loot)";
    public override bool IsApplicable(ValidationContext context) => Directory.Exists(Path.Combine(context.ModRootPath, "Config"));

    private sealed class Names
    {
        public HashSet<string> Things { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Groups { get; } = new(StringComparer.Ordinal);

        public static Names From(IEnumerable<string> configFolders)
        {
            var n = new Names();
            var dirs = configFolders.ToList();
            n.Things.UnionWith(XmlNameCatalog.Collect(dirs, "items.xml", "item"));
            n.Things.UnionWith(XmlNameCatalog.Collect(dirs, "blocks.xml", "block"));
            n.Things.UnionWith(XmlNameCatalog.Collect(dirs, "item_modifiers.xml", "item_modifier"));
            n.Groups.UnionWith(XmlNameCatalog.Collect(dirs, "loot.xml", "lootgroup"));
            return n;
        }

        public HashSet<string> Of(bool group) => group ? Groups : Things;
    }

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var gameConfig = ctx.GameProfile?.ConfigPath;
        if (gameConfig is null || !Directory.Exists(gameConfig)) return; // reported by the XPath validator
        var own = Names.From(new[] { gameConfig, Path.Combine(ctx.ModRootPath, "Config") });
        var others = OtherModRoots(ctx).Select(r => (Mod: Path.GetFileName(r), Names: Names.From(new[] { Path.Combine(r, "Config") }))).ToList();
        var dependencies = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        Check(ctx, findings, "recipes.xml", own, others, dependencies, doc =>
            doc.Descendants("recipe").SelectMany(r =>
                new[] { (El: r, Attr: "name", Kind: "Recipe output", Group: false) }
                    .Concat(r.Elements("ingredient").Select(i => (El: i, Attr: "name", Kind: "Ingredient", Group: false)))));
        ct.ThrowIfCancellationRequested();
        Check(ctx, findings, "loot.xml", own, others, dependencies, doc =>
            doc.Descendants("item").SelectMany(i => new[]
            {
                (El: i, Attr: "name", Kind: "Loot item", Group: false),
                (El: i, Attr: "group", Kind: "Loot group", Group: true),
            }));

        foreach (var (mod, names) in dependencies)
            findings.Add(F(Severity.Info, $"Depends on mod '{mod}': uses {names.Count} name(s) it defines ({string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", ..." : "")}). Install that mod too, or these recipe/loot entries won't work."));
    }

    private void Check(ValidationContext ctx, List<ValidationFinding> findings, string file, Names own,
        List<(string Mod, Names Names)> others, Dictionary<string, SortedSet<string>> dependencies,
        Func<XDocument, IEnumerable<(XElement El, string Attr, string Kind, bool Group)>> references)
    {
        var path = Path.Combine(ctx.ModRootPath, "Config", file);
        if (!File.Exists(path)) return;
        XDocument doc;
        try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
        catch (XmlException) { return; } // reported by the well-formedness validator
        var rel = "Config/" + file;
        var unresolved = new List<string>();
        foreach (var (el, attr, kind, isGroup) in references(doc))
        {
            if (el.Attribute(attr)?.Value is not { Length: > 0 } raw || IsPattern(raw)) continue;
            // "frameShapes:VariantHelper" refers to the block before the colon, but mods can also define an item whose
            // name literally contains the colon, so either form resolves.
            var name = raw.Split(':')[0];
            bool Defines(Names n) => n.Of(isGroup).Contains(raw) || n.Of(isGroup).Contains(name);
            if (Defines(own)) continue;
            var provider = others.FirstOrDefault(o => Defines(o.Names)).Mod;
            if (provider != null)
            {
                if (!dependencies.TryGetValue(provider, out var set)) dependencies[provider] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(raw);
                continue;
            }
            unresolved.Add(raw);
            if (unresolved.Count > MaxListedPerFile) continue;
            var near = own.Of(isGroup).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            var hint = near != null
                ? $" Names are case-sensitive: did you mean '{near}'?"
                : " It may have been renamed or removed in this game version, or come from a mod that isn't installed; either way this entry doesn't work in game.";
            var what = isGroup ? "is not a lootgroup" : "is not an item, block or item modifier";
            findings.Add(F(Severity.Warning, $"{kind} '{raw}' {what} defined by the game or this mod.{hint}", rel, Line(el)));
        }
        if (unresolved.Count > MaxListedPerFile)
        {
            var rest = unresolved.Skip(MaxListedPerFile).Distinct().ToList();
            findings.Add(F(Severity.Warning, $"{unresolved.Count - MaxListedPerFile} more unresolved references in this file: {string.Join(", ", rest.Take(15))}{(rest.Count > 15 ? ", ..." : "")}.", rel));
        }
    }

    /// <summary>Other mods whose definitions count as available: the game's Mods folder plus any supplied by the caller.</summary>
    private static IEnumerable<string> OtherModRoots(ValidationContext ctx)
    {
        var self = Normalize(ctx.ModRootPath);
        var roots = new List<string>();
        if (ctx.GameProfile?.ModsPath is { } mods && Directory.Exists(mods)) roots.AddRange(Directory.GetDirectories(mods));
        if (ctx.Items.TryGetValue(OtherModRootsKey, out var extra) && extra is IEnumerable<string> list) roots.AddRange(list);
        return roots.Where(r => Directory.Exists(Path.Combine(r, "Config")))
            .Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(r => !r.Equals(self, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    // Wildcards and value lists are not single names.
    private static bool IsPattern(string name) => name.IndexOfAny(new[] { '*', ',', '{', '[' }) >= 0;

    private static int? Line(XElement e) => ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : null;
}
