using Microsoft.Data.Sqlite;

namespace MadModStudio.Persistence;

/// <summary>
/// The application metadata database (profiles, projects, revisions, builds, settings). Large data such as game
/// assemblies or the game knowledge index is never stored here.
/// </summary>
public sealed class AppDatabase
{
    private const int SchemaVersion = 2;
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
        if (version < 1) MigrateV1(c);
        if (version < 2) MigrateV2(c);
    }

    private static void MigrateV2(SqliteConnection c)
    {
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            ALTER TABLE revisions ADD COLUMN metadata TEXT NULL;
            CREATE TABLE IF NOT EXISTS knowledge_artifacts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                data_json TEXT NULL,
                agent TEXT NULL,
                provider TEXT NULL,
                model TEXT NULL,
                task_id TEXT NULL,
                run_id TEXT NULL,
                game_profile_id TEXT NULL,
                game_fingerprint TEXT NULL,
                verified INTEGER NOT NULL DEFAULT 0,
                stale INTEGER NOT NULL DEFAULT 0,
                source TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_project ON knowledge_artifacts(project_id, kind, id);
            CREATE INDEX IF NOT EXISTS ix_artifacts_profile ON knowledge_artifacts(game_profile_id, kind);
            CREATE TABLE IF NOT EXISTS project_knowledge (
                project_id TEXT PRIMARY KEY,
                data TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_tasks (
                id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                data TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (run_id, id)
            );
            CREATE INDEX IF NOT EXISTS ix_agent_tasks_project ON agent_tasks(project_id, updated_utc);
            CREATE TABLE IF NOT EXISTS model_performance (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                project_id TEXT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                task_type TEXT NOT NULL,
                data TEXT NOT NULL,
                started_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_perf_model ON model_performance(provider, model, task_type);
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void MigrateV1(SqliteConnection c)
    {
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
        cmd.CommandText = "PRAGMA user_version = 1;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
}
