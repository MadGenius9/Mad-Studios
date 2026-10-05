using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI.Models;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;

namespace MadModStudio.App.ViewModels;

public sealed record PerformanceRow(string Model, string TaskType, string SuccessRate, string Counts, string Compile, string Validation, int Reverted, string AvgLatency);

/// <summary>Discovered models and the model performance dashboard (computed only from real task history).</summary>
public sealed partial class AIModelsViewModel : PageViewModel
{
    private readonly IModelCatalog _catalog;
    private readonly IModelPerformanceTracker _performance;

    public AIModelsViewModel(IModelCatalog catalog, IModelPerformanceTracker performance)
    {
        _catalog = catalog;
        _performance = performance;
    }

    public override string Title => "AI Models";
    public ObservableCollection<ModelInfo> Models { get; } = new();
    public ObservableCollection<PerformanceRow> Performance { get; } = new();

    public override async Task OnNavigatedToAsync()
    {
        Models.Clear();
        foreach (var m in _catalog.Models) Models.Add(m);
        Performance.Clear();
        var stats = await _performance.GetStatsAsync();
        foreach (var s in stats.OrderBy(s => s.Provider).ThenBy(s => s.Model).ThenBy(s => s.TaskType))
            Performance.Add(new PerformanceRow($"{s.Provider} / {s.Model}", s.TaskType.ToString(),
                s.HasEnoughData ? $"{s.SuccessRate:P0}" : "INSUFFICIENT DATA",
                $"{s.Successes} of {s.Total}",
                s.CompileAttempts == 0 ? "—" : $"{s.CompilePasses}/{s.CompileAttempts}",
                s.ValidationAttempts == 0 ? "—" : $"{s.ValidationPasses}/{s.ValidationAttempts}",
                s.Reverted, $"{s.AvgLatencyMs / 1000:0.0}s"));
        StatusMessage = stats.Count == 0
            ? "INSUFFICIENT DATA — no AI tasks have been recorded yet. Percentages appear after at least 3 tasks of a type per model."
            : $"From {stats.Sum(s => s.Total)} recorded task(s). Percentages need at least {ModelTaskStats.MinimumSamples} tasks of a type.";
        if (Models.Count == 0) StatusMessage += "  No models discovered yet: configure a provider in Settings → AI Providers, then Refresh Models.";
    }

    [RelayCommand]
    private Task Refresh() => RunAsync("Discovering models…", async () =>
    {
        var results = await Task.Run(() => _catalog.RefreshAsync());
        await OnNavigatedToAsync();
        if (results.Any(r => !r.Success)) ErrorMessage = string.Join("  ", results.Where(r => !r.Success).Select(r => $"{r.ProviderId}: {r.Error}"));
    });
}
