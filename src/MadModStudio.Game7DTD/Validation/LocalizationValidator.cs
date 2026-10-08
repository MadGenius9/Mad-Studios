using System.Xml;
using System.Xml.Linq;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Index;

namespace MadModStudio.Game7DTD.Validation;

public sealed class LocalizationValidator : ValidatorBase
{
    public override string Id => "localization";
    public override string DisplayName => "Localization";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var files = ModFiles(ctx).ToList();
        var locFiles = files.Where(f => Path.GetFileName(f).StartsWith("Localization", StringComparison.OrdinalIgnoreCase)
            && (f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))).ToList();
        var modKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in locFiles)
        {
            var full = Path.Combine(ctx.ModRootPath, loc);
            if (!loc.StartsWith("Config/", StringComparison.OrdinalIgnoreCase))
                findings.Add(F(Severity.Warning, "Localization file is not inside the Config folder; the game will not load it.", loc));
            var header = LocalizationFile.ReadHeader(full);
            if (header.Count == 0 || !header[0].Trim().Equals("Key", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(F(Severity.Error, "Localization file must start with a header row whose first column is 'Key' (e.g. Key,File,Type,UsedInMainMenu,NoTranslate,english).", loc, 1));
                continue;
            }
            if (!header.Any(h => h.Trim().Equals("english", StringComparison.OrdinalIgnoreCase)))
                findings.Add(F(Severity.Warning, "Localization header has no 'english' column.", loc, 1));
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<(string Key, int Line, int First)>();
            var wide = new List<int>();
            foreach (var row in LocalizationFile.ReadRows(full))
            {
                if (seen.TryGetValue(row.Key, out var first)) duplicates.Add((row.Key, row.Line, first));
                else seen[row.Key] = row.Line;
                if (row.ColumnCount > header.Count) wide.Add(row.Line);
                modKeys.Add(row.Key);
            }
            // Summarised per file: large mods otherwise produce hundreds of near-identical findings.
            foreach (var (key, line, first) in duplicates.Take(5))
                findings.Add(F(Severity.Warning, $"Duplicate localization key '{key}' (first defined on line {first}); the game logs 'Duplicate key' and keeps only one.", loc, line));
            if (duplicates.Count > 5)
                findings.Add(F(Severity.Warning, $"{duplicates.Count - 5} more duplicate localization keys in this file.", loc));
            if (wide.Count > 0)
                findings.Add(F(Severity.Warning, $"{wide.Count} row(s) have more columns than the header ({header.Count}), first at line {wide[0]}. Text containing a comma must be wrapped in double quotes, otherwise everything after the comma lands in the wrong column.", loc, wide[0]));
        }

        // New items/blocks added by the mod should have display names.
        var added = new List<(string Name, string File, int Line)>();
        foreach (var rel in files.Where(f => f.StartsWith("Config/", StringComparison.OrdinalIgnoreCase) && (Path.GetFileName(f).Equals("items.xml", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f).Equals("blocks.xml", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var doc = XDocument.Load(Path.Combine(ctx.ModRootPath, rel), LoadOptions.SetLineInfo);
                foreach (var ap in doc.Descendants().Where(e => e.Name.LocalName is "append" or "insertAfter" or "insertBefore" or "prepend"))
                    foreach (var el in ap.Elements().Where(e => e.Name.LocalName is "item" or "block"))
                        if (el.Attribute("name")?.Value is { } n)
                            added.Add((n, rel, ((IXmlLineInfo)el).LineNumber));
            }
            catch (XmlException) { }
        }
        var missing = added.Where(a => !modKeys.Contains(a.Name) && !(ctx.GameIndex?.LocalizationKeyExists(a.Name) ?? false)).ToList();
        foreach (var m in missing.Take(30))
            findings.Add(F(Severity.Info, $"'{m.Name}' is added by this mod but has no localization entry; players will see the raw name.", m.File, m.Line));
        if (missing.Count > 30) findings.Add(F(Severity.Info, $"{missing.Count - 30} more added items/blocks without localization."));
    }
}
