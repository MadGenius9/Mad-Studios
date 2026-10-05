using MadModStudio.Core.Knowledge;

namespace MadModStudio.AI.Routing;

public interface IModelPerformanceTracker
{
    Task<ModelPerformanceRecord> StartAsync(string provider, string model, AgentKind agent, AITaskType taskType, Guid? projectId, string? taskId, CancellationToken ct = default);
    Task CompleteAsync(ModelPerformanceRecord record, CancellationToken ct = default);
    /// <summary>Stats computed only from real recorded task history.</summary>
    Task<IReadOnlyList<ModelTaskStats>> GetStatsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModelPerformanceRecord>> GetRecordsAsync(Guid? projectId = null, CancellationToken ct = default);
}

public sealed class ModelPerformanceTracker : IModelPerformanceTracker
{
    private readonly IModelPerformanceRepository _repo;
    public ModelPerformanceTracker(IModelPerformanceRepository repo) => _repo = repo;

    public Task<ModelPerformanceRecord> StartAsync(string provider, string model, AgentKind agent, AITaskType taskType, Guid? projectId, string? taskId, CancellationToken ct = default) =>
        _repo.AddAsync(new ModelPerformanceRecord { Provider = provider, Model = model, Agent = agent, TaskType = taskType, ProjectId = projectId, TaskId = taskId, StartedUtc = DateTimeOffset.UtcNow }, ct);

    public Task CompleteAsync(ModelPerformanceRecord record, CancellationToken ct = default) => _repo.UpdateAsync(record, ct);

    public Task<IReadOnlyList<ModelPerformanceRecord>> GetRecordsAsync(Guid? projectId = null, CancellationToken ct = default) => _repo.ListAsync(projectId, ct: ct);

    public async Task<IReadOnlyList<ModelTaskStats>> GetStatsAsync(CancellationToken ct = default)
    {
        var records = await _repo.ListAsync(ct: ct).ConfigureAwait(false);
        return Aggregate(records);
    }

    public static IReadOnlyList<ModelTaskStats> Aggregate(IEnumerable<ModelPerformanceRecord> records) =>
        records.Where(r => r.Outcome is TaskOutcome.Success or TaskOutcome.Failure)
            .GroupBy(r => (r.Provider, r.Model, r.TaskType))
            .Select(g => new ModelTaskStats(g.Key.Provider, g.Key.Model, g.Key.TaskType,
                g.Count(),
                // A change the user reverted is not a success, whatever the tools said at the time.
                g.Count(r => r.Outcome == TaskOutcome.Success && !r.UserReverted),
                g.Count(r => r.CompilePassed == true), g.Count(r => r.CompilePassed != null),
                g.Count(r => r.ValidationPassed == true), g.Count(r => r.ValidationPassed != null),
                g.Count(r => r.UserReverted),
                g.Average(r => (double)r.LatencyMs)))
            .ToList();
}
