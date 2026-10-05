using System.Text;

namespace MadModStudio.Game7DTD.Index;

public sealed record LocalizationRow(string Key, string? English, int Line, int ColumnCount);

/// <summary>Reads 7 Days to Die Localization.txt (CSV with a header row, quoted fields allowed).</summary>
public static class LocalizationFile
{
    public static IReadOnlyList<string> ReadHeader(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var line = reader.ReadLine();
        return line is null ? Array.Empty<string>() : SplitCsvLine(line);
    }

    public static IEnumerable<LocalizationRow> ReadRows(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var header = reader.ReadLine();
        if (header is null) yield break;
        var cols = SplitCsvLine(header);
        var english = cols.FindIndex(c => c.Equals("english", StringComparison.OrdinalIgnoreCase));
        var lineNo = 1;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = SplitCsvLine(line);
            if (fields.Count == 0 || string.IsNullOrWhiteSpace(fields[0])) continue;
            yield return new LocalizationRow(fields[0].Trim(), english >= 0 && english < fields.Count ? fields[english] : null, lineNo, fields.Count);
        }
    }

    public static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }
}
