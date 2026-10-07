namespace MadModStudio.Core.Knowledge;

public interface IKnowledgeRepository
{
    Task<KnowledgeArtifact> AddAsync(KnowledgeArtifact artifact, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeArtifact>> ListAsync(Guid projectId, ArtifactKind? kind = null, int limit = 500, CancellationToken ct = default);
    /// <summary>Artifacts not tied to a project (success patterns) for a game profile.</summary>
    Task<IReadOnlyList<KnowledgeArtifact>> ListGlobalAsync(Guid gameProfileId, ArtifactKind kind, int limit = 100, CancellationToken ct = default);
    Task MarkStaleAsync(Guid gameProfileId, string currentFingerprint, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListProjectIdsAsync(Guid gameProfileId, CancellationToken ct = default);
    Task UpdateAsync(KnowledgeArtifact artifact, CancellationToken ct = default);

    Task<ProjectKnowledgeRecord> GetProjectAsync(Guid projectId, CancellationToken ct = default);
    Task SaveProjectAsync(ProjectKnowledgeRecord record, CancellationToken ct = default);

    Task SaveTaskAsync(AgentTaskRecord task, CancellationToken ct = default);
    Task<IReadOnlyList<AgentTaskRecord>> ListTasksAsync(Guid projectId, string? runId = null, CancellationToken ct = default);
}

public interface IModelPerformanceRepository
{
    Task<ModelPerformanceRecord> AddAsync(ModelPerformanceRecord record, CancellationToken ct = default);
    Task UpdateAsync(ModelPerformanceRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<ModelPerformanceRecord>> ListAsync(Guid? projectId = null, int limit = 5000, CancellationToken ct = default);
    Task MarkRevertedByRevisionAsync(Guid projectId, long restoredToRevisionId, CancellationToken ct = default);
}

public interface IAISpendRepository
{
    Task AddAsync(AISpendRecord record, CancellationToken ct = default);
    /// <summary>Records since <paramref name="sinceUtc"/> (all when null), newest first.</summary>
    Task<IReadOnlyList<AISpendRecord>> ListAsync(DateTimeOffset? sinceUtc = null, Guid? projectId = null, CancellationToken ct = default);
    /// <summary>Sum of known costs for a project (unknown-cost calls are not counted).</summary>
    Task<decimal> ProjectTotalAsync(Guid projectId, CancellationToken ct = default);
}

/// <summary>Notified when a game profile's assemblies change (e.g. after a 7DTD update).</summary>
public interface IGameUpdateListener
{
    Task OnGameAssembliesChangedAsync(Guid gameProfileId, string? previousFingerprint, string newFingerprint, CancellationToken ct = default);
}
