using MadModStudio.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace MadModStudio.Persistence;

public sealed class SqliteKnowledgeRepository : IKnowledgeRepository
{
    private readonly AppDatabase _db;
    public SqliteKnowledgeRepository(AppDatabase db) => _db = db;

    private const string Columns = "id, project_id, kind, title, content, data_json, agent, provider, model, task_id, run_id, game_profile_id, game_fingerprint, verified, stale, source, created_utc";

    private static KnowledgeArtifact Map(SqliteDataReader r)
    {
        static string? S(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new KnowledgeArtifact
        {
            Id = r.GetInt64(0),
            ProjectId = S(r, 1) is { } p ? Guid.Parse(p) : null,
            Kind = Enum.TryParse<ArtifactKind>(r.GetString(2), out var k) ? k : ArtifactKind.Finding,
            Title = r.GetString(3),
            Content = r.GetString(4),
            DataJson = S(r, 5),
            Agent = S(r, 6),
            Provider = S(r, 7),
            Model = S(r, 8),
            TaskId = S(r, 9),
            RunId = S(r, 10),
            GameProfileId = S(r, 11) is { } g ? Guid.Parse(g) : null,
            GameFingerprint = S(r, 12),
            Verified = r.GetInt64(13) == 1,
            Stale = r.GetInt64(14) == 1,
            Source = r.GetString(15),
            CreatedUtc = Json.ParseIso(r.GetString(16)),
        };
    }

    public Task<KnowledgeArtifact> AddAsync(KnowledgeArtifact a, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO knowledge_artifacts(project_id, kind, title, content, data_json, agent, provider, model, task_id, run_id, game_profile_id, game_fingerprint, verified, stale, source, created_utc)
            VALUES($p,$k,$t,$c,$d,$a,$pr,$m,$task,$run,$g,$f,$v,$s,$src,$cr); SELECT last_insert_rowid();
            """;
        Bind(cmd, a);
        a.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(a);
    }

    public Task UpdateAsync(KnowledgeArtifact a, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE knowledge_artifacts SET project_id=$p, kind=$k, title=$t, content=$c, data_json=$d, agent=$a, provider=$pr, model=$m, task_id=$task,
                run_id=$run, game_profile_id=$g, game_fingerprint=$f, verified=$v, stale=$s, source=$src, created_utc=$cr WHERE id=$id
            """;
        Bind(cmd, a);
        cmd.Parameters.AddWithValue("$id", a.Id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static void Bind(SqliteCommand cmd, KnowledgeArtifact a)
    {
        object N(object? v) => v ?? DBNull.Value;
        cmd.Parameters.AddWithValue("$p", N(a.ProjectId?.ToString()));
        cmd.Parameters.AddWithValue("$k", a.Kind.ToString());
        cmd.Parameters.AddWithValue("$t", a.Title);
        cmd.Parameters.AddWithValue("$c", a.Content);
        cmd.Parameters.AddWithValue("$d", N(a.DataJson));
        cmd.Parameters.AddWithValue("$a", N(a.Agent));
        cmd.Parameters.AddWithValue("$pr", N(a.Provider));
        cmd.Parameters.AddWithValue("$m", N(a.Model));
        cmd.Parameters.AddWithValue("$task", N(a.TaskId));
        cmd.Parameters.AddWithValue("$run", N(a.RunId));
        cmd.Parameters.AddWithValue("$g", N(a.GameProfileId?.ToString()));
        cmd.Parameters.AddWithValue("$f", N(a.GameFingerprint));
        cmd.Parameters.AddWithValue("$v", a.Verified ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", a.Stale ? 1 : 0);
        cmd.Parameters.AddWithValue("$src", a.Source);
        cmd.Parameters.AddWithValue("$cr", Json.Iso(a.CreatedUtc));
    }

    public Task<IReadOnlyList<KnowledgeArtifact>> ListAsync(Guid projectId, ArtifactKind? kind = null, int limit = 500, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM knowledge_artifacts WHERE project_id = $p {(kind is null ? "" : "AND kind = $k")} ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$p", projectId.ToString());
        if (kind != null) cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<KnowledgeArtifact>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return Task.FromResult<IReadOnlyList<KnowledgeArtifact>>(list);
    }

    public Task<IReadOnlyList<KnowledgeArtifact>> ListGlobalAsync(Guid gameProfileId, ArtifactKind kind, int limit = 100, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM knowledge_artifacts WHERE project_id IS NULL AND game_profile_id = $g AND kind = $k ORDER BY id DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$g", gameProfileId.ToString());
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<KnowledgeArtifact>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Map(r));
        return Task.FromResult<IReadOnlyList<KnowledgeArtifact>>(list);
    }

    public Task MarkStaleAsync(Guid gameProfileId, string currentFingerprint, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE knowledge_artifacts SET stale = 1 WHERE game_profile_id = $g AND (game_fingerprint IS NULL OR game_fingerprint <> $f)";
        cmd.Parameters.AddWithValue("$g", gameProfileId.ToString());
        cmd.Parameters.AddWithValue("$f", currentFingerprint);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListProjectIdsAsync(Guid gameProfileId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT project_id FROM knowledge_artifacts WHERE game_profile_id = $g AND project_id IS NOT NULL";
        cmd.Parameters.AddWithValue("$g", gameProfileId.ToString());
        var list = new List<Guid>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Guid.Parse(r.GetString(0)));
        return Task.FromResult<IReadOnlyList<Guid>>(list);
    }

    public Task<ProjectKnowledgeRecord> GetProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM project_knowledge WHERE project_id = $p";
        cmd.Parameters.AddWithValue("$p", projectId.ToString());
        var s = cmd.ExecuteScalar() as string;
        return Task.FromResult(s is null ? new ProjectKnowledgeRecord { ProjectId = projectId } : Json.De<ProjectKnowledgeRecord>(s));
    }

    public Task SaveProjectAsync(ProjectKnowledgeRecord record, CancellationToken ct = default)
    {
        record.UpdatedUtc = DateTimeOffset.UtcNow;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO project_knowledge(project_id, data, updated_utc) VALUES($p,$d,$u) ON CONFLICT(project_id) DO UPDATE SET data = excluded.data, updated_utc = excluded.updated_utc";
        cmd.Parameters.AddWithValue("$p", record.ProjectId.ToString());
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        cmd.Parameters.AddWithValue("$u", Json.Iso(record.UpdatedUtc));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task SaveTaskAsync(AgentTaskRecord task, CancellationToken ct = default)
    {
        task.UpdatedUtc = DateTimeOffset.UtcNow;
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO agent_tasks(id, run_id, project_id, data, updated_utc) VALUES($i,$r,$p,$d,$u) ON CONFLICT(run_id, id) DO UPDATE SET data = excluded.data, updated_utc = excluded.updated_utc";
        cmd.Parameters.AddWithValue("$i", task.Id);
        cmd.Parameters.AddWithValue("$r", task.RunId);
        cmd.Parameters.AddWithValue("$p", task.ProjectId.ToString());
        lock (task) cmd.Parameters.AddWithValue("$d", Json.Ser(task));
        cmd.Parameters.AddWithValue("$u", Json.Iso(task.UpdatedUtc));
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AgentTaskRecord>> ListTasksAsync(Guid projectId, string? runId = null, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT data FROM agent_tasks WHERE project_id = $p {(runId is null ? "" : "AND run_id = $r")} ORDER BY updated_utc";
        cmd.Parameters.AddWithValue("$p", projectId.ToString());
        if (runId != null) cmd.Parameters.AddWithValue("$r", runId);
        var list = new List<AgentTaskRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Json.De<AgentTaskRecord>(r.GetString(0)));
        return Task.FromResult<IReadOnlyList<AgentTaskRecord>>(list);
    }
}

public sealed class SqliteModelPerformanceRepository : IModelPerformanceRepository
{
    private readonly AppDatabase _db;
    public SqliteModelPerformanceRepository(AppDatabase db) => _db = db;

    public Task<ModelPerformanceRecord> AddAsync(ModelPerformanceRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO model_performance(project_id, provider, model, task_type, data, started_utc) VALUES($p,$pr,$m,$t,$d,$s); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$p", (object?)record.ProjectId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pr", record.Provider);
        cmd.Parameters.AddWithValue("$m", record.Model);
        cmd.Parameters.AddWithValue("$t", record.TaskType.ToString());
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        cmd.Parameters.AddWithValue("$s", Json.Iso(record.StartedUtc));
        record.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.FromResult(record);
    }

    public Task UpdateAsync(ModelPerformanceRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE model_performance SET data = $d WHERE id = $id";
        cmd.Parameters.AddWithValue("$d", Json.Ser(record));
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ModelPerformanceRecord>> ListAsync(Guid? projectId = null, int limit = 5000, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, data FROM model_performance {(projectId is null ? "" : "WHERE project_id = $p")} ORDER BY id DESC LIMIT $l";
        if (projectId != null) cmd.Parameters.AddWithValue("$p", projectId.ToString());
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<ModelPerformanceRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rec = Json.De<ModelPerformanceRecord>(r.GetString(1));
            rec.Id = r.GetInt64(0);
            list.Add(rec);
        }
        return Task.FromResult<IReadOnlyList<ModelPerformanceRecord>>(list);
    }

    public async Task MarkRevertedByRevisionAsync(Guid projectId, long restoredToRevisionId, CancellationToken ct = default)
    {
        // Restoring to a revision at or before an AI change's "before" snapshot reverts that change.
        foreach (var r in await ListAsync(projectId, ct: ct))
        {
            if (r.BeforeRevisionId is { } before && restoredToRevisionId <= before && !r.UserReverted)
            {
                r.UserReverted = true;
                await UpdateAsync(r, ct);
            }
        }
    }
}

/// <summary>AI spend ledger. Costs are stored as invariant decimal text so no precision is lost.</summary>
public sealed class SqliteAISpendRepository : IAISpendRepository
{
    private readonly AppDatabase _db;
    public SqliteAISpendRepository(AppDatabase db) => _db = db;

    public Task AddAsync(AISpendRecord record, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ai_spend(project_id, provider, model, agent, input_tokens, output_tokens, cost_usd, utc)
            VALUES($p, $prov, $m, $a, $i, $o, $c, $u); SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$p", (object?)record.ProjectId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prov", record.Provider);
        cmd.Parameters.AddWithValue("$m", record.Model);
        cmd.Parameters.AddWithValue("$a", record.Agent.ToString());
        cmd.Parameters.AddWithValue("$i", record.InputTokens);
        cmd.Parameters.AddWithValue("$o", record.OutputTokens);
        cmd.Parameters.AddWithValue("$c", (object?)record.CostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", Json.Iso(record.Utc));
        record.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AISpendRecord>> ListAsync(DateTimeOffset? sinceUtc = null, Guid? projectId = null, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (sinceUtc is { } since) { where.Add("utc >= $s"); cmd.Parameters.AddWithValue("$s", Json.Iso(since)); }
        if (projectId is { } pid) { where.Add("project_id = $p"); cmd.Parameters.AddWithValue("$p", pid.ToString()); }
        cmd.CommandText = "SELECT id, project_id, provider, model, agent, input_tokens, output_tokens, cost_usd, utc FROM ai_spend"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY id DESC";
        var list = new List<AISpendRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AISpendRecord
            {
                Id = r.GetInt64(0),
                ProjectId = r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)),
                Provider = r.GetString(2),
                Model = r.GetString(3),
                Agent = Enum.TryParse<AgentKind>(r.GetString(4), out var a) ? a : AgentKind.Lead,
                InputTokens = r.GetInt64(5),
                OutputTokens = r.GetInt64(6),
                CostUsd = r.IsDBNull(7) ? null : decimal.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
                Utc = Json.ParseIso(r.GetString(8)),
            });
        return Task.FromResult<IReadOnlyList<AISpendRecord>>(list);
    }

    public async Task<decimal> ProjectTotalAsync(Guid projectId, CancellationToken ct = default) =>
        (await ListAsync(null, projectId, ct).ConfigureAwait(false)).Sum(r => r.CostUsd ?? 0m);
}
