using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.ModAnalysis.Assemblies;

public sealed record AssemblyReferenceInfo(string Name, string Version, string? PublicKeyToken)
{
    public override string ToString() => $"{Name}, Version={Version}";
}

public sealed class MemberReport
{
    public string Kind { get; init; } = "Method"; // Method, Constructor, Field, Property, Event
    public string Name { get; init; } = "";
    public string Signature { get; init; } = "";
    public string? ReturnType { get; init; }
    public int ParameterCount { get; init; }
    public bool IsStatic { get; init; }
    public bool IsPublic { get; init; }
    public bool IsVirtual { get; init; }
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
}

public sealed class TypeReport
{
    public string Namespace { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Reflection-style full name (nested types use '+').</summary>
    public string FullName { get; init; } = "";
    public string Kind { get; init; } = "class"; // class, interface, struct, enum, delegate
    public string? BaseType { get; init; }
    public IReadOnlyList<string> Interfaces { get; init; } = Array.Empty<string>();
    public bool IsPublic { get; init; }
    public bool IsNested { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    public IReadOnlyList<string> Attributes { get; init; } = Array.Empty<string>();
    public List<MemberReport> Members { get; } = new();
}

/// <summary>A reference from this assembly to a member defined in another assembly.</summary>
public sealed record ExternalMemberReference(string Assembly, string DeclaringType, string Name, string Kind, int ParameterCount, string Signature);

public sealed record ExternalTypeReference(string Assembly, string FullName);

/// <summary>Everything discovered about an assembly purely from its metadata. The assembly is never loaded or executed.</summary>
public sealed class AssemblyReport
{
    public string FilePath { get; init; } = "";
    public long FileSize { get; init; }
    public string? Sha256 { get; init; }
    public bool IsManagedAssembly { get; set; }
    public string? ReadError { get; set; }
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Culture { get; set; }
    public string? PublicKeyToken { get; set; }
    public string? TargetFramework { get; set; }
    public string? Mvid { get; set; }
    public List<AssemblyReferenceInfo> References { get; } = new();
    public List<TypeReport> Types { get; } = new();
    public List<HarmonyPatchInfo> HarmonyPatches { get; } = new();
    public List<string> ModApiEntryPoints { get; } = new();
    public bool UsesManualHarmonyPatching { get; set; }
    public bool ReferencesHarmony { get; set; }
    public List<ExternalTypeReference> ExternalTypes { get; } = new();
    public List<ExternalMemberReference> ExternalMembers { get; } = new();

    public IEnumerable<string> Namespaces => Types.Select(t => t.Namespace).Where(n => n.Length > 0).Distinct().OrderBy(n => n);
    public bool Success => IsManagedAssembly && ReadError is null;
}
