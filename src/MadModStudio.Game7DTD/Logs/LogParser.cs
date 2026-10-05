using System.Text.RegularExpressions;

namespace MadModStudio.Game7DTD.Logs;

/// <summary>
/// Streams 7 Days to Die client/server logs (Unity "output_log" style) and extracts structured problems. Designed for
/// very large files: lines are streamed, only problem entries are kept and limits are enforced.
/// </summary>
public sealed partial class LogParser
{
    public int MaxEntries { get; init; } = 20_000;
    public int MaxStackFrames { get; init; } = 40;

    // 2024-07-15T12:34:56 123.456 ERR message
    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})\s+(?<up>\d+(?:\.\d+)?)\s+(?<lvl>INF|WRN|ERR|EXC|LOG)\s+(?<msg>.*)$")]
    private static partial Regex LinePattern();

    // at Namespace.Type.Method (args) [0x00000] in <hash>:0   |   at Type.Method (args) in C:\path\File.cs:123
    [GeneratedRegex(@"^\s*at\s+(?<type>[\w.`<>+\[\],]+)\.(?<method>[\w<>`.]+)\s*\((?<args>[^)]*)\)(?:.*?\bin\s+(?<file>[^<>:]+?\.cs):(?:line\s+)?(?<line>\d+))?")]
    private static partial Regex MonoFrame();

    // Type:Method (args) (at C:/path/File.cs:123)   (Unity style)
    [GeneratedRegex(@"^(?<type>[\w.`<>+]+):(?<method>[\w<>`.]+)\s*\((?<args>[^)]*)\)(?:\s*\(at\s+(?<file>[^:]+(?::[^:]+)?\.cs):(?<line>\d+)\))?")]
    private static partial Regex UnityFrame();

    [GeneratedRegex(@"(?<type>[A-Za-z_][\w.]*(?:Exception|Error))(?::|\s+-)\s*(?<msg>.*)$")]
    private static partial Regex ExceptionPattern();

    [GeneratedRegex(@"(?<file>[\w\-. ]+\.(?:xml|cs|dll|txt))(?:'|"")?(?:\s*\(?\s*line\s*(?<line>\d+)|:(?<line2>\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FileRefPattern();

    [GeneratedRegex(@"xpath=""(?<xp>[^""]+)""|xpath='(?<xp2>[^']+)'|XPath\s+""(?<xp3>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex XPathPattern();

    [GeneratedRegex(@"(?:mod\s+['""](?<m1>[^'""]+)['""]|from mod[: ]+""?(?<m2>[^""\s,]+)""?|\[MODS\]\s+(?:Loaded Mod|Loading mod|Failed loading mod)[: ]+(?<m3>[^\s(]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ModNamePattern();

    [GeneratedRegex(@"\[MODS\]\s+Loaded Mod:\s*(?<name>.+?)\s*\((?<ver>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LoadedModPattern();

    [GeneratedRegex(@"'(?<id>[A-Za-z_][\w.`+]*(?:::[\w.]+)?)'|""(?<id2>[A-Za-z_][\w.`+]+)""")]
    private static partial Regex QuotedIdentifier();

    public LogReport Parse(string path, string kind = "client", CancellationToken ct = default)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Log file not found.", path);
        var report = new LogReport { FilePath = path, Kind = kind, FileSizeBytes = fi.Length };

        LogEntry? current = null;
        var lineNo = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if ((lineNo & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (line.Length > 8000) line = line[..8000];

            var m = LinePattern().Match(line);
            if (m.Success)
            {
                var level = m.Groups["lvl"].Value;
                var msg = m.Groups["msg"].Value;
                if (msg.Contains("[MODS]") && LoadedModPattern().Match(msg) is { Success: true } lm)
                    report.LoadedMods.Add($"{lm.Groups["name"].Value} ({lm.Groups["ver"].Value})");
                if (report.GameVersionLine == null && msg.Contains("Version:") && msg.Contains("Compatibility Version", StringComparison.OrdinalIgnoreCase))
                    report.GameVersionLine = msg;

                var keep = level is "ERR" or "EXC" or "WRN" || msg.Contains("Exception");
                if (keep && report.Entries.Count < MaxEntries)
                {
                    current = CreateEntry(lineNo, m.Groups["ts"].Value, level, msg);
                    report.Entries.Add(current);
                }
                else
                {
                    if (keep) report.Truncated = true;
                    current = null;
                }
                continue;
            }

            // Lines without the standard prefix: stack frames, exception lines or Unity raw output.
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (current != null && TryParseFrame(trimmed) is { } frame)
            {
                if (current.StackTrace.Count < MaxStackFrames) current.StackTrace.Add(frame);
                continue;
            }
            if (ExceptionPattern().IsMatch(trimmed) && !trimmed.StartsWith("at ") && report.Entries.Count < MaxEntries)
            {
                // Raw exception line (e.g. Unity "NullReferenceException: Object reference not set...")
                current = CreateEntry(lineNo, null, "EXC", trimmed);
                report.Entries.Add(current);
                continue;
            }
            if (current != null && current.StackTrace.Count == 0 && current.Message.Length < 4000 && !trimmed.StartsWith("UnityEngine.") && !trimmed.StartsWith("(Filename"))
            {
                // Continuation of a multi-line message.
                // (Only before any stack frames to avoid swallowing unrelated output.)
            }
        }
        report.TotalLines = lineNo;
        foreach (var e in report.Entries) Classify(e);
        Group(report);
        return report;
    }

    private static LogEntry CreateEntry(int line, string? ts, string level, string msg) => new()
    {
        LineNumber = line,
        Timestamp = string.IsNullOrEmpty(ts) ? null : ts,
        Level = level,
        Message = msg,
    };

    private static StackFrameRef? TryParseFrame(string line)
    {
        var m = MonoFrame().Match(line);
        if (!m.Success) m = UnityFrame().Match(line);
        if (!m.Success) return null;
        var lineVal = m.Groups["line"].Success && int.TryParse(m.Groups["line"].Value, out var l) ? l : (int?)null;
        return new StackFrameRef(m.Groups["type"].Value, m.Groups["method"].Value, m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : null, lineVal, line);
    }

    public static void Classify(LogEntry e)
    {
        var msg = e.Message;
        var ex = ExceptionPattern().Match(msg);
        if (ex.Success) e.ExceptionType = ex.Groups["type"].Value.Split('.').Last();

        e.Category = msg switch
        {
            _ when e.ExceptionType is "MissingMethodException" || msg.Contains("Method not found") || msg.Contains("Undefined target method") => LogCategory.MissingMethod,
            _ when e.ExceptionType is "MissingFieldException" || msg.Contains("Field not found") => LogCategory.MissingField,
            _ when e.ExceptionType is "TypeLoadException" or "ReflectionTypeLoadException" || msg.Contains("Could not load type") => LogCategory.MissingType,
            _ when e.ExceptionType is "FileNotFoundException" or "FileLoadException" or "BadImageFormatException" || msg.Contains("Could not load file or assembly") || msg.Contains("Failed loading DLL", StringComparison.OrdinalIgnoreCase) => LogCategory.AssemblyLoad,
            _ when msg.Contains("Harmony", StringComparison.OrdinalIgnoreCase) || msg.Contains("Patching exception") => LogCategory.Harmony,
            _ when msg.Contains("xpath", StringComparison.OrdinalIgnoreCase) || msg.Contains("did not apply", StringComparison.OrdinalIgnoreCase) => LogCategory.XPath,
            _ when e.ExceptionType is "XmlException" || msg.Contains("XML loader", StringComparison.OrdinalIgnoreCase) || (msg.Contains(".xml", StringComparison.OrdinalIgnoreCase) && (msg.Contains("failed", StringComparison.OrdinalIgnoreCase) || msg.Contains("error", StringComparison.OrdinalIgnoreCase))) => LogCategory.XmlError,
            _ when e.ExceptionType is "NullReferenceException" || msg.Contains("Object reference not set") => LogCategory.NullReference,
            _ when msg.Contains("[MODS]") => LogCategory.ModLoading,
            _ when e.ExceptionType != null => LogCategory.Exception,
            _ => LogCategory.Other,
        };

        foreach (Match f in FileRefPattern().Matches(msg))
        {
            var ln = f.Groups["line"].Success ? int.Parse(f.Groups["line"].Value) : f.Groups["line2"].Success ? int.Parse(f.Groups["line2"].Value) : (int?)null;
            e.FileReferences.Add((f.Groups["file"].Value.Trim(), ln));
        }
        foreach (var fr in e.StackTrace.Where(s => s.File != null))
            e.FileReferences.Add((Path.GetFileName(fr.File!.Replace('\\', '/')), fr.Line));

        var xp = XPathPattern().Match(msg);
        if (xp.Success) e.XPath = new[] { "xp", "xp2", "xp3" }.Select(g => xp.Groups[g]).First(g => g.Success).Value;

        foreach (Match mm in ModNamePattern().Matches(msg))
        {
            var name = new[] { "m1", "m2", "m3" }.Select(g => mm.Groups[g]).FirstOrDefault(g => g.Success)?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !e.ModNames.Contains(name)) e.ModNames.Add(name.Trim());
        }
        foreach (Match q in QuotedIdentifier().Matches(msg))
        {
            var id = q.Groups["id"].Success ? q.Groups["id"].Value : q.Groups["id2"].Value;
            if (id.Length > 2 && !id.EndsWith(".xml") && !e.ReferencedIdentifiers.Contains(id)) e.ReferencedIdentifiers.Add(id);
        }
        foreach (var f in e.StackTrace.Take(10))
        {
            var id = $"{f.Type}.{f.Method}";
            if (!e.ReferencedIdentifiers.Contains(id)) e.ReferencedIdentifiers.Add(id);
        }
    }

    [GeneratedRegex(@"\d+(\.\d+)*")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"0x[0-9a-fA-F]+")]
    private static partial Regex Hex();

    private static void Group(LogReport report)
    {
        var groups = new Dictionary<string, LogGroup>();
        foreach (var e in report.Entries.Where(e => e.Level is "ERR" or "EXC" or "WRN"))
        {
            var normalized = Numbers().Replace(Hex().Replace(e.Message, "#"), "#");
            if (normalized.Length > 300) normalized = normalized[..300];
            var top = e.StackTrace.FirstOrDefault();
            var sig = $"{e.Category}|{e.Level}|{normalized}|{(top is null ? "" : top.Type + "." + top.Method)}";
            if (!groups.TryGetValue(sig, out var g))
            {
                g = new LogGroup { Signature = sig, Category = e.Category, Level = e.Level };
                groups[sig] = g;
            }
            g.Entries.Add(e);
        }
        report.Groups.AddRange(groups.Values
            .OrderBy(g => g.Level switch { "EXC" => 0, "ERR" => 1, _ => 2 })
            .ThenByDescending(g => g.Count));
    }

    /// <summary>Searches the raw log file (streamed) for a text, returning matching lines.</summary>
    public static IReadOnlyList<(int Line, string Text)> Search(string path, string query, int limit = 1000, CancellationToken ct = default)
    {
        var results = new List<(int, string)>();
        if (string.IsNullOrEmpty(query)) return results;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var n = 0;
        string? line;
        while ((line = reader.ReadLine()) != null && results.Count < limit)
        {
            n++;
            if ((n & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
            if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add((n, line.Length > 2000 ? line[..2000] : line));
        }
        return results;
    }
}
