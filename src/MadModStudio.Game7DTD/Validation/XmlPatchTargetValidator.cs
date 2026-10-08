using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Mods;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Evaluates every XPath patch operation against the installed game's actual config XML. An XPath that matches nothing
/// will silently do nothing in game (or log "did not apply"), which is a common cause of broken mods after updates.
/// </summary>
public sealed partial class XmlPatchTargetValidator : ValidatorBase
{
    public override string Id => "xml-xpath";
    public override string DisplayName => "XML patch targets (XPath)";
    public override bool IsApplicable(ValidationContext context) => Directory.Exists(Path.Combine(context.ModRootPath, "Config"));

    [GeneratedRegex("'([^']+)'|\"([^\"]+)\"")]
    private static partial Regex Literal();

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var configDir = Path.Combine(ctx.ModRootPath, "Config");
        if (!Directory.Exists(configDir)) return;
        var gameConfig = ctx.GameProfile?.ConfigPath;
        if (gameConfig is null || !Directory.Exists(gameConfig))
        {
            findings.Add(F(Severity.Info, "No game Config folder available in the Game Profile; XPath targets were not checked against the installed game."));
            return;
        }

        // Names this mod defines itself (so XPaths targeting its own additions aren't flagged).
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        var modXml = Directory.GetFiles(configDir, "*.xml", SearchOption.AllDirectories);
        foreach (var f in modXml)
        {
            try
            {
                foreach (var e in XDocument.Load(f).Descendants())
                    if (e.Attribute("name")?.Value is { } n) ownNames.Add(n);
            }
            catch (XmlException) { }
        }

        var cache = new Dictionary<string, XDocument?>(StringComparer.OrdinalIgnoreCase);
        var others = new Lazy<IReadOnlyList<string>>(() => OtherModRoots(ctx));
        var otherNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> OtherNames(string root, string rel) =>
            otherNames.TryGetValue(root + "|" + rel, out var s) ? s : otherNames[root + "|" + rel] = NamesIn(root, rel);
        // name attribute values per game file, keyed case-insensitively, for "did you mean" hints (this run only).
        var namesByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in modXml)
        {
            ct.ThrowIfCancellationRequested();
            var relInConfig = Path.GetRelativePath(configDir, file).Replace('\\', '/');
            var relInMod = "Config/" + relInConfig;
            if (relInConfig.StartsWith("Localization", StringComparison.OrdinalIgnoreCase)) continue;
            // Legacy Config/XUi files on a split-XUi game are explained (with the fix) by GameLayoutCompatibilityValidator.
            if (relInConfig.StartsWith("XUi/", StringComparison.OrdinalIgnoreCase) && GameLayoutCompatibilityValidator.GameUsesSplitXui(gameConfig)) continue;

            XDocument modDoc;
            try { modDoc = XDocument.Load(file, LoadOptions.SetLineInfo); }
            catch (XmlException) { continue; } // reported by the well-formedness validator

            var ops = modDoc.Descendants().Where(e => e.Attribute("xpath") != null &&
                ModAnalyzer.PatchOperations.Contains(e.Name.LocalName.ToLowerInvariant())).ToList();
            if (ops.Count == 0) continue;

            var gamePath = Path.Combine(gameConfig, relInConfig.Replace('/', Path.DirectorySeparatorChar));
            if (!cache.TryGetValue(gamePath, out var gameDoc))
            {
                gameDoc = null;
                if (File.Exists(gamePath))
                {
                    try { gameDoc = XDocument.Load(gamePath); }
                    catch (XmlException ex) { findings.Add(F(Severity.Warning, $"Game file {relInConfig} could not be parsed: {ex.Message}", relInMod)); }
                }
                cache[gamePath] = gameDoc;
            }
            if (gameDoc is null)
            {
                if (!File.Exists(gamePath))
                    findings.Add(F(Severity.Warning, $"Patch file targets '{relInConfig}', which does not exist in the installed game's Config folder. The patches in this file will not apply (unless another mod creates that file).", relInMod));
                continue;
            }

            var unmatched = 0;
            foreach (var op in ops)
            {
                var xpath = op.Attribute("xpath")!.Value;
                var line = ((IXmlLineInfo)op).HasLineInfo() ? ((IXmlLineInfo)op).LineNumber : (int?)null;
                int matches;
                try
                {
                    var result = gameDoc.XPathEvaluate(xpath);
                    matches = result is IEnumerable<object> seq ? seq.Count() : (result is bool b ? (b ? 1 : 0) : 1);
                }
                catch (XPathException ex)
                {
                    var v2 = xpath.Contains("ends-with(") || xpath.Contains("matches(") || xpath.Contains("lower-case(")
                        ? " The game uses XPath 1.0: ends-with(), matches() and lower-case() are not available (use contains() or starts-with())." : "";
                    findings.Add(F(Severity.Error, $"Invalid XPath expression in <{op.Name.LocalName}>: {ex.Message}{v2}", relInMod, line, xpath));
                    continue;
                }
                if (matches > 0) continue;
                var literals = Literal().Matches(xpath).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
                if (literals.Any(ownNames.Contains))
                    findings.Add(F(Severity.Info, $"XPath matches nothing in the base game but appears to target content this mod adds itself: {xpath}", relInMod, line));
                else if (others.Value.FirstOrDefault(o => literals.Any(OtherNames(o, relInConfig).Contains)) is { } provider)
                {
                    // Content another mod adds: the patch only works if that mod is applied first.
                    var mod = Path.GetFileName(provider);
                    var self = Path.GetFileName(ctx.ModRootPath);
                    if (WontLoadReason(provider, ctx.GameProfile) is { } why)
                        findings.Add(F(Severity.Warning, $"This <{op.Name.LocalName}> targets content added by mod '{mod}', but the game won't load '{mod}' because {why}, so the patch does nothing: {xpath}", relInMod, line, xpath));
                    else if (LoadsBefore(provider, ctx.ModRootPath) == false)
                        findings.Add(F(Severity.Warning, $"Load order: this <{op.Name.LocalName}> targets content added by mod '{mod}', but '{mod}' loads after '{self}' (the game loads mods in alphabetical folder order), so the target doesn't exist yet and the patch does nothing. Rename this mod's folder so it sorts after '{mod}' (for example with a 'z' prefix): {xpath}", relInMod, line, xpath));
                    else
                        findings.Add(F(Severity.Info, $"XPath targets content added by mod '{mod}'; it only applies when that mod is installed and loads first: {xpath}", relInMod, line));
                }
                else
                {
                    unmatched++;
                    if (unmatched <= 50)
                        findings.Add(F(Severity.Warning, $"XPath matches nothing in the installed game's {relInConfig}; this <{op.Name.LocalName}> will not apply (it may target content from another mod): {xpath}{Hint(op, xpath, literals, gameDoc, gamePath, namesByFile)}", relInMod, line, xpath));
                }
            }
            if (unmatched > 50) findings.Add(F(Severity.Warning, $"{unmatched - 50} more unmatched XPath operations in this file.", relInMod));
        }
    }

    /// <summary>Every name defined in another mod's copy of the same config file (cached by <see cref="XmlNameCatalog"/>).</summary>
    private static HashSet<string> NamesIn(string modRoot, string relInConfig)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in XmlNameCatalog.Read(Path.Combine(modRoot, "Config", relInConfig.Replace('/', Path.DirectorySeparatorChar))).Values) all.UnionWith(set);
        return all;
    }

    /// <summary>Why an XPath that matches nothing probably fails: the common modder mistakes, checked against the game file.</summary>
    private static string Hint(XElement op, string xpath, List<string> literals, XDocument gameDoc, string gamePath,
        Dictionary<string, Dictionary<string, string>> namesByFile)
    {
        // <set> on an attribute the element doesn't have: set only changes existing attributes.
        var at = xpath.LastIndexOf("/@", StringComparison.Ordinal);
        if (op.Name.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase) && at > 0 && xpath.IndexOf('/', at + 2) < 0)
        {
            var parent = xpath[..at];
            var attr = xpath[(at + 2)..];
            try
            {
                if (gameDoc.XPathSelectElements(parent).Any())
                    return $" The element exists but has no '{attr}' attribute: <set> only changes existing attributes. Use <setattribute xpath=\"{parent}\" name=\"{attr}\">value</setattribute> to add it.";
            }
            catch (XPathException) { }
        }
        // A name that only differs in case.
        if (!namesByFile.TryGetValue(gamePath, out var names))
        {
            names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in gameDoc.Descendants())
                if (e.Attribute("name")?.Value is { Length: > 0 } n) names.TryAdd(n, n);
            namesByFile[gamePath] = names;
        }
        foreach (var lit in literals)
            if (names.TryGetValue(lit, out var actual) && actual != lit)
                return $" XPath is case-sensitive: the game names it '{actual}', not '{lit}'.";
        if (!xpath.StartsWith('/'))
            return " XPaths should start with '/' (the document root), e.g. /items/item[@name='...'].";
        return "";
    }
}
