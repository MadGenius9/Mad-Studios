using System.Globalization;
using MadModStudio.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace MadModStudio.Game7DTD.Index;

/// <summary>Read-only access to a per-profile game knowledge index file.</summary>
public sealed class SqliteGameKnowledgeIndex : IGameKnowledgeIndex
{
    private readonly string _connectionString;

    public SqliteGameKnowledgeIndex(string indexPath)
    {
        if (!File.Exists(indexPath)) throw new FileNotFoundException("Game index has not been built yet.", indexPath);
        IndexPath = indexPath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = indexPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
    }

    public string IndexPath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    private static string Like(string q) => "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public IndexSummary GetSummary()
    {
        using var c = Open();
        long Count(string table)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)cmd.ExecuteScalar()!;
        }
        DateTimeOffset? built = null;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM meta WHERE key = 'built_utc'";
            if (cmd.ExecuteScalar() is string s) built = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
        }
        return new IndexSummary((int)Count("assemblies"), (int)Count("types"), (int)Count("members"), (int)Count("xml_files"),
            (int)Count("xml_entries"), (int)Count("localization"), built);
    }

    private const string TypeSelect = "SELECT a.name, t.namespace, t.name, t.full_name, t.kind, t.base_type, t.is_public FROM types t JOIN assemblies a ON a.id = t.assembly_id";

    private static IndexedType MapType(SqliteDataReader r) =>
        new(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? "class" : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6) == 1);

    public IReadOnlyList<IndexedType> SearchTypes(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<IndexedType>();
        // Exact matches first, then prefix, then contains.
        return Query($"""
            {TypeSelect}
            WHERE t.name LIKE $q ESCAPE '\' OR t.full_name LIKE $q ESCAPE '\'
            ORDER BY CASE WHEN t.name = $exact COLLATE NOCASE THEN 0 WHEN t.name LIKE $prefix ESCAPE '\' THEN 1 ELSE 2 END, t.is_public DESC, length(t.name), t.full_name
            LIMIT $limit
            """, MapType, ("$q", Like(query.Trim())), ("$exact", query.Trim()), ("$prefix", query.Trim() + "%"), ("$limit", limit));
    }

    public IndexedType? GetType(string fullOrSimpleName)
    {
        if (string.IsNullOrWhiteSpace(fullOrSimpleName)) return null;
        var name = NormalizeTypeName(fullOrSimpleName);
        var exact = Query($"{TypeSelect} WHERE t.full_name = $n LIMIT 1", MapType, ("$n", name));
        if (exact.Count > 0) return exact[0];
        // C# source writes nested types as Outer.Inner; metadata uses Outer+Inner.
        var nested = Query($"{TypeSelect} WHERE replace(t.full_name, '+', '.') = $n LIMIT 1", MapType, ("$n", name));
        if (nested.Count > 0) return nested[0];
        var simple = name.Split('.', '+').Last();
        var bySimple = Query($"{TypeSelect} WHERE t.name = $n ORDER BY t.is_public DESC, a.include_nonpublic DESC LIMIT 2", MapType, ("$n", simple));
        return bySimple.Count > 0 && (name == simple || bySimple.Count == 1 || bySimple[0].FullName.Replace('+', '.').EndsWith(name)) ? bySimple[0] : null;
    }

    public static string NormalizeTypeName(string name)
    {
        var n = name.Trim();
        if (n.StartsWith("global::")) n = n[8..];
        var lt = n.IndexOf('<');
        if (lt > 0) n = n[..lt];
        return n;
    }

    private static IndexedMember MapMember(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.GetInt64(5) == 1, r.GetInt64(6) == 1, r.IsDBNull(7) ? 0 : (int)r.GetInt64(7));

    private const string MemberSelect = "SELECT t.full_name, m.kind, m.name, m.signature, m.return_type, m.is_static, m.is_public, m.param_count FROM members m JOIN types t ON t.id = m.type_id";

    public IReadOnlyList<IndexedMember> GetMembers(string typeFullName, string? memberName = null, int limit = 2000)
    {
        var type = GetType(typeFullName);
        if (type is null) return Array.Empty<IndexedMember>();
        return memberName is null
            ? Query($"{MemberSelect} WHERE t.full_name = $t ORDER BY m.kind, m.name LIMIT $l", MapMember, ("$t", type.FullName), ("$l", limit))
            : Query($"{MemberSelect} WHERE t.full_name = $t AND m.name = $n LIMIT $l", MapMember, ("$t", type.FullName), ("$n", memberName), ("$l", limit));
    }

    public IReadOnlyList<IndexedMember> SearchMembers(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<IndexedMember>();
        return Query($"""
            {MemberSelect}
            WHERE m.name LIKE $q ESCAPE '\'
            ORDER BY CASE WHEN m.name = $exact COLLATE NOCASE THEN 0 ELSE 1 END, length(m.name), t.full_name
            LIMIT $l
            """, MapMember, ("$q", Like(query.Trim())), ("$exact", query.Trim()), ("$l", limit));
    }

    public IReadOnlyList<IndexedMember> FindMemberInHierarchy(string typeFullName, string memberName)
    {
        var visited = new HashSet<string>();
        var type = GetType(typeFullName);
        var depth = 0;
        while (type != null && depth++ < 20 && visited.Add(type.FullName))
        {
            var found = Query($"{MemberSelect} WHERE t.full_name = $t AND m.name = $n", MapMember, ("$t", type.FullName), ("$n", memberName));
            if (found.Count > 0) return found;
            if (type.BaseType is null or "System.Object" or "object") break;
            type = GetType(type.BaseType);
        }
        return Array.Empty<IndexedMember>();
    }

    public IReadOnlyList<string> GetIndexedAssemblyNames() =>
        Query("SELECT DISTINCT name FROM assemblies ORDER BY name", r => r.GetString(0));

    public IReadOnlyList<IndexedXmlFile> GetXmlFiles() =>
        Query("SELECT rel_path, full_path, root, entry_count, size FROM xml_files ORDER BY rel_path",
            r => new IndexedXmlFile(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), (int)r.GetInt64(3), r.GetInt64(4)));

    public IReadOnlyList<IndexedXmlEntry> SearchXml(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<IndexedXmlEntry>();
        return Query("""
            SELECT f.rel_path, e.element, e.name, e.path, e.line FROM xml_entries e JOIN xml_files f ON f.id = e.file_id
            WHERE e.name LIKE $q ESCAPE '\' OR e.element = $exact
            ORDER BY CASE WHEN e.name = $exact COLLATE NOCASE THEN 0 ELSE 1 END, length(e.name)
            LIMIT $l
            """, r => new IndexedXmlEntry(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), (int)r.GetInt64(4)),
            ("$q", Like(query.Trim())), ("$exact", query.Trim()), ("$l", limit));
    }

    public IReadOnlyList<IndexedLocalization> SearchLocalization(string query, int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<IndexedLocalization>();
        return Query("SELECT key, file, english FROM localization WHERE key LIKE $q ESCAPE '\\' OR english LIKE $q ESCAPE '\\' LIMIT $l",
            r => new IndexedLocalization(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
            ("$q", Like(query.Trim())), ("$l", limit));
    }

    public bool LocalizationKeyExists(string key) =>
        Query("SELECT 1 FROM localization WHERE key = $k COLLATE NOCASE LIMIT 1", r => 1, ("$k", key)).Count > 0;
}
