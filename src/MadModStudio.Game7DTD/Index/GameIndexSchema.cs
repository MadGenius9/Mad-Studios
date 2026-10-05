namespace MadModStudio.Game7DTD.Index;

internal static class GameIndexSchema
{
    public const int Version = 1;

    public const string Create = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE assemblies (id INTEGER PRIMARY KEY, name TEXT NOT NULL, version TEXT, path TEXT NOT NULL, size INTEGER, mtime TEXT, include_nonpublic INTEGER);
        CREATE TABLE types (id INTEGER PRIMARY KEY, assembly_id INTEGER NOT NULL, namespace TEXT, name TEXT NOT NULL, full_name TEXT NOT NULL,
            kind TEXT, base_type TEXT, is_public INTEGER, interfaces TEXT, attributes TEXT);
        CREATE TABLE members (id INTEGER PRIMARY KEY, type_id INTEGER NOT NULL, kind TEXT, name TEXT NOT NULL, signature TEXT,
            return_type TEXT, is_static INTEGER, is_public INTEGER, param_count INTEGER);
        CREATE TABLE xml_files (id INTEGER PRIMARY KEY, rel_path TEXT NOT NULL, full_path TEXT NOT NULL, root TEXT, size INTEGER, entry_count INTEGER, error TEXT);
        CREATE TABLE xml_entries (id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, element TEXT, name TEXT, path TEXT, line INTEGER);
        CREATE TABLE localization (key TEXT NOT NULL, file TEXT, english TEXT);
        """;

    public const string Indexes = """
        CREATE INDEX ix_types_name ON types(name COLLATE NOCASE);
        CREATE INDEX ix_types_full ON types(full_name COLLATE NOCASE);
        CREATE INDEX ix_members_type ON members(type_id);
        CREATE INDEX ix_members_name ON members(name COLLATE NOCASE);
        CREATE INDEX ix_xml_entries_name ON xml_entries(name COLLATE NOCASE);
        CREATE INDEX ix_loc_key ON localization(key COLLATE NOCASE);
        """;
}
