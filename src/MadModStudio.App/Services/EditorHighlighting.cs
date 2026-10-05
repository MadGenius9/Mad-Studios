using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

namespace MadModStudio.App.Services;

/// <summary>Picks AvalonEdit syntax highlighting by extension and recolors the built-in definitions for the dark theme.</summary>
public static class EditorHighlighting
{
    private static readonly HashSet<IHighlightingDefinition> Adjusted = new();

    public static IHighlightingDefinition? ForFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var name = ext switch
        {
            ".cs" => "C#",
            ".xml" or ".csproj" or ".xaml" or ".props" or ".targets" => "XML",
            ".json" => "Json",
            ".js" => "JavaScript",
            ".txt" or ".csv" or ".md" or ".log" => null,
            _ => null,
        };
        var def = name is null ? null : HighlightingManager.Instance.GetDefinition(name);
        if (def != null) Darken(def);
        return def;
    }

    private static void Darken(IHighlightingDefinition def)
    {
        lock (Adjusted)
        {
            if (!Adjusted.Add(def)) return;
            foreach (var c in def.NamedHighlightingColors)
            {
                var n = c.Name.ToLowerInvariant();
                var color = n switch
                {
                    _ when n.Contains("comment") => Color.FromRgb(0x6A, 0x99, 0x55),
                    _ when n.Contains("string") || n.Contains("char") || n.Contains("attributevalue") => Color.FromRgb(0xCE, 0x91, 0x78),
                    _ when n.Contains("number") || n.Contains("digit") => Color.FromRgb(0xB5, 0xCE, 0xA8),
                    _ when n.Contains("attributename") => Color.FromRgb(0x9C, 0xDC, 0xFE),
                    _ when n.Contains("xmltag") || n.Contains("tag") => Color.FromRgb(0x56, 0x9C, 0xD6),
                    _ when n.Contains("keyword") || n.Contains("modifier") || n.Contains("visibility") || n.Contains("type") || n.Contains("namespace")
                        || n.Contains("this") || n.Contains("null") || n.Contains("true") || n.Contains("void") || n.Contains("value") => Color.FromRgb(0x56, 0x9C, 0xD6),
                    _ when n.Contains("preprocessor") => Color.FromRgb(0x9B, 0x9B, 0x9B),
                    _ when n.Contains("method") => Color.FromRgb(0xDC, 0xDC, 0xAA),
                    _ => Color.FromRgb(0xC5, 0x86, 0xC0),
                };
                c.Foreground = new SimpleHighlightingBrush(color);
                c.Background = null;
            }
        }
    }
}
