using System.Xml;
using System.Xml.Linq;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Checks the names a mod's config XML refers to:
/// recipes (output, ingredients), loot (item, group), traders (item, group), item/block <c>Extends</c> parents and
/// block upgrade/downgrade paths. Names resolve against the game's Config folder and this mod; a name only another mod
/// defines is a dependency on that mod (reported as such), and a name nobody defines is a warning. Names are
/// case-sensitive, so near-misses get a hint.
/// </summary>
public sealed class XmlReferenceValidator : ValidatorBase
{
    private const int MaxListedPerFile = 10;

    public override string Id => "xml-references";
    public override string DisplayName => "XML references (recipes, loot, traders, Extends, upgrades)";
    public override bool IsApplicable(ValidationContext context) => Directory.Exists(Path.Combine(context.ModRootPath, "Config"));

    /// <summary>What kind of definition a reference must resolve to.</summary>
    private enum Target { Thing, Item, Block, LootGroup, TraderGroup }

    private sealed record Reference(XElement El, string Name, string Kind, Target Target);

    private sealed class Names
    {
        private readonly Dictionary<Target, HashSet<string>> _sets = new();

        public static Names From(IEnumerable<string> configFolders)
        {
            var dirs = configFolders.ToList();
            var n = new Names();
            var items = XmlNameCatalog.Collect(dirs, "items.xml", "item");
            var blocks = XmlNameCatalog.Collect(dirs, "blocks.xml", "block");
            var things = new HashSet<string>(items, StringComparer.Ordinal);
            things.UnionWith(blocks);
            things.UnionWith(XmlNameCatalog.Collect(dirs, "item_modifiers.xml", "item_modifier"));
            n._sets[Target.Item] = items;
            n._sets[Target.Block] = blocks;
            n._sets[Target.Thing] = things;
            n._sets[Target.LootGroup] = XmlNameCatalog.Collect(dirs, "loot.xml", "lootgroup");
            n._sets[Target.TraderGroup] = XmlNameCatalog.Collect(dirs, "traders.xml", "trader_item_group");
            return n;
        }

        public HashSet<string> Of(Target t) => _sets[t];
    }

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var gameConfig = ctx.GameProfile?.ConfigPath;
        if (gameConfig is null || !Directory.Exists(gameConfig)) return; // reported by the XPath validator
        var own = Names.From(new[] { gameConfig, Path.Combine(ctx.ModRootPath, "Config") });
        var others = OtherModRoots(ctx).Select(r => (Mod: Path.GetFileName(r), Names: Names.From(new[] { Path.Combine(r, "Config") }))).ToList();
        var dependencies = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (file, refs) in References)
        {
            ct.ThrowIfCancellationRequested();
            Check(ctx, findings, file, own, others, dependencies, refs);
        }

        var rootOf = OtherModRoots(ctx).ToDictionary(Path.GetFileName, r => r, StringComparer.OrdinalIgnoreCase);
        foreach (var (mod, names) in dependencies)
        {
            var used = $"{names.Count} name(s) it defines ({string.Join(", ", names.Take(5))}{(names.Count > 5 ? ", ..." : "")})";
            if (rootOf.TryGetValue(mod, out var root) && WontLoadReason(root, ctx.GameProfile) is { } why)
                findings.Add(F(Severity.Warning, $"Depends on mod '{mod}' for {used}, but the game won't load '{mod}' because {why}. Until that mod is fixed, the entries using these names don't work."));
            else
                findings.Add(F(Severity.Info, $"Depends on mod '{mod}': uses {used}. Install that mod too, or the entries using them won't work."));
        }
    }

    /// <summary>Per config file, how to find the references in it.</summary>
    private static readonly (string File, Func<XDocument, IEnumerable<Reference>> Refs)[] References =
    {
        ("recipes.xml", doc => doc.Descendants("recipe").SelectMany(r =>
            Ref(r, "name", "Recipe output", Target.Thing)
                .Concat(r.Elements("ingredient").SelectMany(i => Ref(i, "name", "Ingredient", Target.Thing))))),
        ("loot.xml", doc => doc.Descendants("item").SelectMany(i =>
            Ref(i, "name", "Loot item", Target.Thing).Concat(Ref(i, "group", "Loot group", Target.LootGroup)))),
        ("traders.xml", doc => doc.Descendants("item").SelectMany(i =>
            Ref(i, "name", "Trader item", Target.Thing).Concat(Ref(i, "group", "Trader item group", Target.TraderGroup)))),
        ("items.xml", doc => Extends(doc, "item", Target.Item)),
        ("blocks.xml", doc => Extends(doc, "block", Target.Block).Concat(BlockPaths(doc))),
    };

    private static IEnumerable<Reference> Ref(XElement el, string attr, string kind, Target target) =>
        el.Attribute(attr)?.Value is { Length: > 0 } v ? new[] { new Reference(el, v, kind, target) } : Array.Empty<Reference>();

    /// <summary><c>&lt;property name="Extends" value="parent"/&gt;</c> directly under an item/block: the parent must be the same kind.</summary>
    private static IEnumerable<Reference> Extends(XDocument doc, string element, Target target) =>
        doc.Descendants(element).Elements("property")
            .Where(p => (string?)p.Attribute("name") == "Extends")
            .SelectMany(p => Ref(p, "value", $"Extends parent ({element})", target));

    /// <summary>Downgrade target, and the upgrade's target block and resource item.</summary>
    private static IEnumerable<Reference> BlockPaths(XDocument doc)
    {
        foreach (var block in doc.Descendants("block"))
        {
            foreach (var p in block.Elements("property").Where(p => (string?)p.Attribute("name") == "DowngradeBlock"))
                foreach (var r in Ref(p, "value", "DowngradeBlock", Target.Block)) yield return r;
            foreach (var up in block.Elements("property").Where(p => (string?)p.Attribute("class") == "UpgradeBlock"))
                foreach (var p in up.Elements("property"))
                {
                    var name = (string?)p.Attribute("name");
                    if (name == "ToBlock") foreach (var r in Ref(p, "value", "UpgradeBlock ToBlock", Target.Block)) yield return r;
                    else if (name == "Item") foreach (var r in Ref(p, "value", "UpgradeBlock Item", Target.Thing)) yield return r;
                }
        }
    }

    private void Check(ValidationContext ctx, List<ValidationFinding> findings, string file, Names own,
        List<(string Mod, Names Names)> others, Dictionary<string, SortedSet<string>> dependencies,
        Func<XDocument, IEnumerable<Reference>> references)
    {
        var path = Path.Combine(ctx.ModRootPath, "Config", file);
        if (!File.Exists(path)) return;
        XDocument doc;
        try { doc = XDocument.Load(path, LoadOptions.SetLineInfo); }
        catch (XmlException) { return; } // reported by the well-formedness validator
        var rel = "Config/" + file;
        var unresolved = new List<string>();
        foreach (var r in references(doc))
        {
            var raw = r.Name;
            if (IsPattern(raw)) continue;
            // "frameShapes:VariantHelper" refers to the block before the colon, but mods can also define an item whose
            // name literally contains the colon, so either form resolves.
            var name = raw.Split(':')[0];
            bool Defines(Names n) => n.Of(r.Target).Contains(raw) || n.Of(r.Target).Contains(name);
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
            var near = own.Of(r.Target).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            var hint = near != null
                ? $" Names are case-sensitive: did you mean '{near}'?"
                : " It may have been renamed or removed in this game version, or come from a mod that isn't installed; either way this entry doesn't work in game.";
            findings.Add(F(Severity.Warning, $"{r.Kind} '{raw}' {Describe(r.Target)} defined by the game or this mod.{hint}", rel, Line(r.El)));
        }
        if (unresolved.Count > MaxListedPerFile)
        {
            var rest = unresolved.Skip(MaxListedPerFile).Distinct().ToList();
            findings.Add(F(Severity.Warning, $"{unresolved.Count - MaxListedPerFile} more unresolved references in this file: {string.Join(", ", rest.Take(15))}{(rest.Count > 15 ? ", ..." : "")}.", rel));
        }
    }

    private static string Describe(Target t) => t switch
    {
        Target.Item => "is not an item",
        Target.Block => "is not a block",
        Target.LootGroup => "is not a lootgroup",
        Target.TraderGroup => "is not a trader_item_group",
        _ => "is not an item, block or item modifier",
    };

    // Wildcards and value lists are not single names.
    private static bool IsPattern(string name) => name.IndexOfAny(new[] { '*', ',', '{', '[' }) >= 0;

    private static int? Line(XElement e) => ((IXmlLineInfo)e).HasLineInfo() ? ((IXmlLineInfo)e).LineNumber : null;
}
