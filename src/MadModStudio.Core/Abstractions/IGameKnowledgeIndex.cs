namespace MadModStudio.Core.Abstractions;

public sealed record IndexedType(
    string Assembly,
    string Namespace,
    string Name,
    string FullName,
    string Kind,
    string? BaseType,
    bool IsPublic);

public sealed record IndexedMember(
    string DeclaringType,
    string Kind,
    string Name,
    string Signature,
    string? ReturnType,
    bool IsStatic,
    bool IsPublic,
    int ParameterCount = 0);

public sealed record IndexedXmlEntry(
    string File,
    string Element,
    string? Name,
    string Path,
    int Line);

public sealed record IndexedXmlFile(string RelativePath, string FullPath, string? RootElement, int EntryCount, long Size);

public sealed record IndexedLocalization(string Key, string File, string? English);

public sealed record IndexSummary(int Assemblies, int Types, int Members, int XmlFiles, int XmlEntries, int LocalizationKeys, DateTimeOffset? BuiltUtc);

/// <summary>
/// Read-only searchable knowledge about one installed game. Built from metadata / config files only;
/// nothing is executed. Used by validators, the UI search and the AI context tools.
/// </summary>
public interface IGameKnowledgeIndex
{
    IndexSummary GetSummary();
    IReadOnlyList<IndexedType> SearchTypes(string query, int limit = 100);
    IndexedType? GetType(string fullOrSimpleName);
    IReadOnlyList<IndexedMember> GetMembers(string typeFullName, string? memberName = null, int limit = 2000);
    IReadOnlyList<IndexedMember> SearchMembers(string query, int limit = 100);
    /// <summary>Finds members with the given name on the type or any of its base types in the index.</summary>
    IReadOnlyList<IndexedMember> FindMemberInHierarchy(string typeFullName, string memberName);
    /// <summary>Names of the assemblies that were indexed (e.g. "Assembly-CSharp").</summary>
    IReadOnlyList<string> GetIndexedAssemblyNames();
    IReadOnlyList<IndexedXmlFile> GetXmlFiles();
    IReadOnlyList<IndexedXmlEntry> SearchXml(string query, int limit = 100);
    IReadOnlyList<IndexedLocalization> SearchLocalization(string query, int limit = 100);
    bool LocalizationKeyExists(string key);
}
