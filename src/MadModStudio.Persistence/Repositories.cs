using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace MadModStudio.Persistence;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Ser<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T De<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
    public static string Iso(DateTimeOffset d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class SqliteGameProfileRepository : IGameProfileRepository
{
    private readonly AppDatabase _db;
    public SqliteGameProfileRepository(AppDatabase db) => _db = db;

    public Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM game_profiles ORDER BY created_utc";
        var list = new List<GameProfile>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Json.De<GameProfile>(r.GetString(0)));
        return Task.FromResult<IReadOnlyList<GameProfile>>(list);
    }

    public Task<GameProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM game_profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var s = cmd.ExecuteScalar() as string;
        return Task.FromResult(s is null ? null : Json.De<GameProfile>(s));
    }

    public Task SaveAsync(GameProfile profile, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO game_profiles(id, name, data, created_utc) VALUES($id, $name, $data, $created)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, data = excluded.data
            """;
        cmd.Parameters.AddWithValue("$id", profile.Id.ToString());
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$data", Json.Ser(profile));
        cmd.Parameters.AddWithValue("$created", Json.Iso(profile.CreatedUtc));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM game_profiles WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class SqliteProjectRepository : IProjectRepository
{
    private readonly AppDatabase _db;
    public SqliteProjectRepository(AppDatabase db) => _db = db;

    public Task<IReadOnlyList<ModProject>> ListAsync(CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM projects ORDER BY modified_utc DESC";
        var list = new List<ModProject>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Json.De<ModProject>(r.GetString(0)));
        return Task.FromResult<IReadOnlyList<ModProject>>(list);
    }

    public Task<ModProject?> GetAsync(Guid id, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        var s = cmd.ExecuteScalar() as string;
        return Task.FromResult(s is null ? null : Json.De<ModProject>(s));
    }

    public Task SaveAsync(ModProject project, CancellationToken ct = default)
    {
        project.ModifiedUtc = DateTimeOffset.UtcNow;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects(id, name, game_profile_id, data, modified_utc) VALUES($id, $name, $gp, $data, $mod)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, game_profile_id = excluded.game_profile_id,
                data = excluded.data, modified_utc = excluded.modified_utc
            """;
        cmd.Parameters.AddWithValue("$id", project.Id.ToString());
        cmd.Parameters.AddWithValue("$name", project.Name);
        cmd.Parameters.AddWithValue("$gp", (object?)project.GameProfileId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$data", Json.Ser(project));
        cmd.Parameters.AddWithValue("$mod", Json.Iso(project.ModifiedUtc));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class SqliteRevisionRepository : IRevisionRepository
{
    private readonly AppDatabase _db;
    public SqliteRevisionRepository(AppDatabase db) => _db = db;

    public Task<IReadOnlyList<RevisionRecord>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, commit_id, parent_commit_id, timestamp_utc, action, reason, changed_files, build_status, metadata
            FROM revisions WHERE project_id = $p ORDER BY id DESC
            """;
        cmd.Parameters.AddWithValue("$p", projectId.ToString());
        var list = new List<RevisionRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RevisionRecord
            {
                Id = r.GetInt64(0),
                ProjectId = Guid.Parse(r.GetString(1)),
                CommitId = r.GetString(2),
                ParentCommitId = r.IsDBNull(3) ? null : r.GetString(3),
                TimestampUtc = Json.ParseIso(r.GetString(4)),
                Action = r.GetString(5),
                Reason = r.IsDBNull(6) ? null : r.GetString(6),
                ChangedFiles = Json.De<List<string>>(r.GetString(7)),
                BuildStatus = r.IsDBNull(8) ? null : r.GetString(8),
                Metadata = r.IsDBNull(9) ? new() : Json.De<Dictionary<string, string>>(r.GetString(9)),
            });
        }
        return Task.FromResult<IReadOnlyList<RevisionRecord>>(list);
    }

    public Task<RevisionRecord> AddAsync(RevisionRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO revisions(project_id, commit_id, parent_commit_id, timestamp_utc, action, reason, changed_files, build_status, metadata)
            VALUES($p, $c, $pc, $t, $a, $r, $f, $b, $m);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", record.ProjectId.ToString());
        cmd.Parameters.AddWithValue("$c", record.CommitId);
        cmd.Parameters.AddWithValue("$pc", (object?)record.ParentCommitId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", Json.Iso(record.TimestampUtc));
        cmd.Parameters.AddWithValue("$a", record.Action);
        cmd.Parameters.AddWithValue("$r", (object?)record.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", Json.Ser(record.ChangedFiles));
        cmd.Parameters.AddWithValue("$b", (object?)record.BuildStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", record.Metadata.Count == 0 ? DBNull.Value : Json.Ser(record.Metadata));
        record.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(record);
    }

    public Task UpdateBuildStatusAsync(long revisionId, string buildStatus, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE revisions SET build_status = $b WHERE id = $id";
        cmd.Parameters.AddWithValue("$b", buildStatus);
        cmd.Parameters.AddWithValue("$id", revisionId);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class SqliteBuildRecordRepository : IBuildRecordRepository
{
    private readonly AppDatabase _db;
    public SqliteBuildRecordRepository(AppDatabase db) => _db = db;

    public Task<IReadOnlyList<BuildRecord>> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, data FROM builds WHERE project_id = $p ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$p", projectId.ToString());
        var list = new List<BuildRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var b = Json.De<BuildRecord>(r.GetString(1));
            b.Id = r.GetInt64(0);
            list.Add(b);
        }
        return Task.FromResult<IReadOnlyList<BuildRecord>>(list);
    }

    public Task<BuildRecord> AddAsync(BuildRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO builds(project_id, started_utc, data) VALUES($p, $s, $d); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", record.ProjectId.ToString());
        cmd.Parameters.AddWithValue("$s", Json.Iso(record.StartedUtc));
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        record.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(record);
    }
}

public sealed class SqliteDeploymentRepository : IDeploymentRepository
{
    private readonly AppDatabase _db;
    public SqliteDeploymentRepository(AppDatabase db) => _db = db;

    public Task<IReadOnlyList<DeploymentRecord>> ListAsync(Guid projectId, CancellationToken ct = default) => Query(projectId);

    public Task<IReadOnlyList<DeploymentRecord>> ListAllAsync(CancellationToken ct = default) => Query(null);

    private Task<IReadOnlyList<DeploymentRecord>> Query(Guid? projectId)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = projectId is null
            ? "SELECT id, data FROM deployments ORDER BY id DESC"
            : "SELECT id, data FROM deployments WHERE project_id = $p ORDER BY id DESC";
        if (projectId is not null) cmd.Parameters.AddWithValue("$p", projectId.Value.ToString());
        var list = new List<DeploymentRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = Json.De<DeploymentRecord>(r.GetString(1));
            d.Id = r.GetInt64(0);
            list.Add(d);
        }
        return Task.FromResult<IReadOnlyList<DeploymentRecord>>(list);
    }

    public Task<DeploymentRecord> AddAsync(DeploymentRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO deployments(project_id, deployed_utc, data) VALUES($p, $t, $d); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", record.ProjectId.ToString());
        cmd.Parameters.AddWithValue("$t", Json.Iso(record.DeployedUtc));
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        record.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(record);
    }

    public Task UpdateAsync(DeploymentRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE deployments SET data = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}

public sealed class SqliteSettingsRepository : ISettingsRepository
{
    private readonly AppDatabase _db;
    public SqliteSettingsRepository(AppDatabase db) => _db = db;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return Task.FromResult(cmd.ExecuteScalar() as string);
    }

    public Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
}
