using Microsoft.Data.Sqlite;

namespace MadModStudio.Persistence;

/// <summary>
/// The application metadata database (profiles, projects, revisions, builds, settings). Large data such as game
/// assemblies or the game knowledge index is never stored here.
/// </summary>
public sealed class AppDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public AppDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        Migrate();
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }
        using var get = c.CreateCommand();
        get.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(get.ExecuteScalar());
        if (version >= SchemaVersion) return;

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS game_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                data TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                game_profile_id TEXT NULL,
                data TEXT NOT NULL,
                modified_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS revisions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                commit_id TEXT NOT NULL,
                parent_commit_id TEXT NULL,
                timestamp_utc TEXT NOT NULL,
                action TEXT NOT NULL,
                reason TEXT NULL,
                changed_files TEXT NOT NULL,
                build_status TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_revisions_project ON revisions(project_id, id);
            CREATE TABLE IF NOT EXISTS builds (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                started_utc TEXT NOT NULL,
                data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_builds_project ON builds(project_id, id);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
