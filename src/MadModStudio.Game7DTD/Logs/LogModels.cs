namespace MadModStudio.Game7DTD.Logs;

public enum LogCategory
{
    Other,
    Exception,
    NullReference,
    MissingType,
    MissingMethod,
    MissingField,
    AssemblyLoad,
    Harmony,
    XmlError,
    XPath,
    ModLoading,
}

public sealed record StackFrameRef(string Type, string Method, string? File, int? Line, string Raw);

public sealed class LogEntry
{
    public int LineNumber { get; init; }
    public string? Timestamp { get; init; }
    /// <summary>INF, WRN, ERR, EXC or LOG (unknown).</summary>
    public string Level { get; init; } = "LOG";
    public string Message { get; init; } = "";
    public LogCategory Category { get; set; }
    public string? ExceptionType { get; set; }
    public List<StackFrameRef> StackTrace { get; } = new();
    public List<string> ReferencedIdentifiers { get; } = new();
    public List<(string File, int? Line)> FileReferences { get; } = new();
    public List<string> ModNames { get; } = new();
    public string? XPath { get; set; }
}

public sealed class LogGroup
{
    public string Signature { get; init; } = "";
    public LogCategory Category { get; init; }
    public string Level { get; init; } = "";
    public List<LogEntry> Entries { get; } = new();
    public LogEntry First => Entries[0];
    public int Count => Entries.Count;
}

public sealed class LogReport
{
    public string FilePath { get; init; } = "";
    public string Kind { get; init; } = "client"; // client or server
    public long FileSizeBytes { get; init; }
    public int TotalLines { get; set; }
    public bool Truncated { get; set; }
    public List<LogEntry> Entries { get; } = new();
    public List<LogGroup> Groups { get; } = new();
    public List<string> LoadedMods { get; } = new();
    public string? GameVersionLine { get; set; }

    public int ErrorCount => Entries.Count(e => e.Level is "ERR" or "EXC");
    public int WarningCount => Entries.Count(e => e.Level == "WRN");
}
