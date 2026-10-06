using MadModStudio.Core.Models;

namespace MadModStudio.Core.Abstractions;

public interface IGameProfileRepository
{
    Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken ct = default);
    Task<GameProfile?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(GameProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IProjectRepository
{
    Task<IReadOnlyList<ModProject>> ListAsync(CancellationToken ct = default);
    Task<ModProject?> GetAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(ModProject project, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

public interface IRevisionRepository
{
    Task<IReadOnlyList<RevisionRecord>> ListAsync(Guid projectId, CancellationToken ct = default);
    Task<RevisionRecord> AddAsync(RevisionRecord record, CancellationToken ct = default);
    Task UpdateBuildStatusAsync(long revisionId, string buildStatus, CancellationToken ct = default);
}

public interface IBuildRecordRepository
{
    Task<IReadOnlyList<BuildRecord>> ListAsync(Guid projectId, CancellationToken ct = default);
    Task<BuildRecord> AddAsync(BuildRecord record, CancellationToken ct = default);
}

public interface IDeploymentRepository
{
    Task<IReadOnlyList<DeploymentRecord>> ListAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>All deployments of every project, newest first.</summary>
    Task<IReadOnlyList<DeploymentRecord>> ListAllAsync(CancellationToken ct = default);
    Task<DeploymentRecord> AddAsync(DeploymentRecord record, CancellationToken ct = default);
    Task UpdateAsync(DeploymentRecord record, CancellationToken ct = default);
}

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string? value, CancellationToken ct = default);
}
