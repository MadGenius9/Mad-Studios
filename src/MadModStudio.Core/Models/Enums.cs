namespace MadModStudio.Core.Models;

/// <summary>Severity used by validators, analyzers and diagnostics. Ordered so that comparisons work (Error is worst).</summary>
public enum Severity
{
    Pass = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

public enum ModType
{
    Unknown = 0,
    XmlOnly = 1,
    HarmonyCSharp = 2,
    Hybrid = 3,
}

public enum IndexStatus
{
    NotIndexed = 0,
    Indexing = 1,
    Indexed = 2,
    Failed = 3,
    Stale = 4,
}

public enum ProjectStatus
{
    Imported = 0,
    Draft = 1,
    BuildSucceeded = 2,
    BuildFailed = 3,
    ValidationFailed = 4,
    Packaged = 5,
    PackagedWithErrors = 6,
}

public enum ProjectOrigin
{
    New = 0,
    Imported = 1,
    Repair = 2,
}

public enum BuildConfiguration
{
    Debug = 0,
    Release = 1,
}

/// <summary>Result of the (heuristic) server-side analysis. Never a guarantee.</summary>
public enum SideRequirement
{
    Unknown = 0,
    LikelyServerSide = 1,
    ClientRequired = 2,
    Warning = 3,
}

/// <summary>Result of the (heuristic) EAC analysis. There is intentionally no "guaranteed safe" value.</summary>
public enum EacCompatibility
{
    Unknown = 0,
    LikelyCompatible = 1,
    LikelyIncompatible = 2,
}
