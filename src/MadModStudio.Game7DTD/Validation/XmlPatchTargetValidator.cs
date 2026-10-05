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
        foreach (var file in modXml)
        {
            ct.ThrowIfCancellationRequested();
            var relInConfig = Path.GetRelativePath(configDir, file).Replace('\\', '/');
            var relInMod = "Config/" + relInConfig;
            if (relInConfig.StartsWith("Localization", StringComparison.OrdinalIgnoreCase)) continue;

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
                    findings.Add(F(Severity.Error, $"Invalid XPath expression in <{op.Name.LocalName}>: {ex.Message}", relInMod, line, xpath));
                    continue;
                }
                if (matches > 0) continue;
                var literals = Literal().Matches(xpath).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToList();
                if (literals.Any(ownNames.Contains))
                    findings.Add(F(Severity.Info, $"XPath matches nothing in the base game but appears to target content this mod adds itself: {xpath}", relInMod, line));
                else
                {
                    unmatched++;
                    if (unmatched <= 50)
                        findings.Add(F(Severity.Warning, $"XPath matches nothing in the installed game's {relInConfig}; this <{op.Name.LocalName}> will not apply (it may target content from another mod): {xpath}", relInMod, line, xpath));
                }
            }
            if (unmatched > 50) findings.Add(F(Severity.Warning, $"{unmatched - 50} more unmatched XPath operations in this file.", relInMod));
        }
    }
}
