using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI.Models;
using MadModStudio.AI.Routing;
using MadModStudio.Core.Knowledge;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

public sealed record PerformanceRow(string Model, string TaskType, string SuccessRate, string Counts, string Compile, string Validation, int Reverted, string AvgLatency);

/// <summary>Spend for one period or group. Unknown-cost calls are counted separately, never estimated.</summary>
public sealed record SpendRow(string Label, int Calls, long InputTokens, long OutputTokens, string Cost, int UnknownCostCalls);

/// <summary>Discovered models, prices, AI spend and the model performance dashboard — all from real data only.</summary>
public sealed partial class AIModelsViewModel : PageViewModel
{
    private readonly IModelCatalog _catalog;
    private readonly IModelPerformanceTracker _performance;
    private readonly IAISpendRepository _spend;
    private readonly ModelProfileStore _profiles;
    private readonly ProjectService _projects;

    [ObservableProperty] private ModelInfo? _selectedModel;
    [ObservableProperty] private string _priceInput = "";
    [ObservableProperty] private string _priceOutput = "";

    public AIModelsViewModel(IModelCatalog catalog, IModelPerformanceTracker performance, IAISpendRepository spend, ModelProfileStore profiles, ProjectService projects)
    {
        _catalog = catalog;
        _performance = performance;
        _spend = spend;
        _profiles = profiles;
        _projects = projects;
    }

    public override string Title => "AI Models";
    public ObservableCollection<ModelInfo> Models { get; } = new();
    public ObservableCollection<PerformanceRow> Performance { get; } = new();
    public ObservableCollection<SpendRow> SpendByPeriod { get; } = new();
    public ObservableCollection<SpendRow> SpendByModel { get; } = new();
    public ObservableCollection<SpendRow> SpendByProject { get; } = new();

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        PriceInput = value?.InputCostPerMTok?.ToString(CultureInfo.InvariantCulture) ?? "";
        PriceOutput = value?.OutputCostPerMTok?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    public override async Task OnNavigatedToAsync()
    {
        var selectedKey = SelectedModel?.Key;
        Models.Clear();
        foreach (var m in _catalog.Models) Models.Add(m);
        SelectedModel = Models.FirstOrDefault(m => m.Key == selectedKey);

        Performance.Clear();
        var stats = await _performance.GetStatsAsync();
        foreach (var s in stats.OrderBy(s => s.Provider).ThenBy(s => s.Model).ThenBy(s => s.TaskType))
            Performance.Add(new PerformanceRow($"{s.Provider} / {s.Model}", s.TaskType.ToString(),
                s.HasEnoughData ? $"{s.SuccessRate:P0}" : "INSUFFICIENT DATA",
                $"{s.Successes} of {s.Total}",
                s.CompileAttempts == 0 ? "—" : $"{s.CompilePasses}/{s.CompileAttempts}",
                s.ValidationAttempts == 0 ? "—" : $"{s.ValidationPasses}/{s.ValidationAttempts}",
                s.Reverted, $"{s.AvgLatencyMs / 1000:0.0}s"));

        await LoadSpendAsync();
        StatusMessage = stats.Count == 0
            ? "INSUFFICIENT DATA — no AI tasks have been recorded yet. Percentages appear after at least 3 tasks of a type per model."
            : $"From {stats.Sum(s => s.Total)} recorded task(s). Percentages need at least {ModelTaskStats.MinimumSamples} tasks of a type.";
        if (Models.Count == 0) StatusMessage += "  No models discovered yet: configure a provider in Settings → AI Providers, then Refresh Models.";
    }

    private async Task LoadSpendAsync()
    {
        var all = await _spend.ListAsync();
        var now = DateTimeOffset.UtcNow;
        SpendByPeriod.Clear();
        SpendByPeriod.Add(Summarize("Today", all.Where(r => r.Utc.LocalDateTime.Date == DateTime.Now.Date)));
        SpendByPeriod.Add(Summarize("Last 7 days", all.Where(r => r.Utc >= now.AddDays(-7))));
        SpendByPeriod.Add(Summarize("Last 30 days", all.Where(r => r.Utc >= now.AddDays(-30))));
        SpendByPeriod.Add(Summarize("All time", all));

        SpendByModel.Clear();
        foreach (var g in all.GroupBy(r => (r.Provider, r.Model)).OrderByDescending(g => g.Sum(r => r.CostUsd ?? 0)))
            SpendByModel.Add(Summarize(_catalog.Find(g.Key.Provider, g.Key.Model)?.ToString() ?? $"{g.Key.Provider} / {g.Key.Model}", g));

        var names = (await _projects.ListAsync()).ToDictionary(p => p.Id, p => p.Name);
        SpendByProject.Clear();
        foreach (var g in all.GroupBy(r => r.ProjectId).OrderByDescending(g => g.Sum(r => r.CostUsd ?? 0)))
            SpendByProject.Add(Summarize(g.Key is { } id ? names.GetValueOrDefault(id, "(deleted project)") : "(no project)", g));
    }

    private static SpendRow Summarize(string label, IEnumerable<AISpendRecord> records)
    {
        var list = records.ToList();
        var known = list.Where(r => r.CostUsd != null).ToList();
        var unknown = list.Count - known.Count;
        var cost = list.Count == 0 ? "$0.00"
            : known.Count == 0 ? "cost unknown"
            : $"${known.Sum(r => r.CostUsd!.Value):0.00}{(unknown > 0 ? " + unknown" : "")}";
        return new SpendRow(label, list.Count, list.Sum(r => r.InputTokens), list.Sum(r => r.OutputTokens), cost, unknown);
    }

    [RelayCommand]
    private Task Refresh() => RunAsync("Discovering models…", async () =>
    {
        var results = await Task.Run(() => _catalog.RefreshAsync());
        await OnNavigatedToAsync();
        if (results.Any(r => !r.Success)) ErrorMessage = string.Join("  ", results.Where(r => !r.Success).Select(r => $"{r.ProviderId}: {r.Error}"));
    });

    /// <summary>Saves the price into model-profiles.json. Prices drive cost estimates and budgets; leaving them blank keeps "cost unknown".</summary>
    [RelayCommand]
    private Task SavePrice() => RunAsync("Saving price…", async () =>
    {
        if (SelectedModel is null) { ErrorMessage = "Select a model first."; return; }
        static bool TryMoney(string s, out decimal? v)
        {
            v = null;
            if (string.IsNullOrWhiteSpace(s)) return true;
            var ok = decimal.TryParse(s.Trim().TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0;
            if (ok) v = d;
            return ok;
        }
        if (!TryMoney(PriceInput, out var input) || !TryMoney(PriceOutput, out var output))
        {
            ErrorMessage = "Enter prices as numbers in USD per million tokens (e.g. 2.5), or leave both blank to clear.";
            return;
        }
        if ((input is null) != (output is null)) { ErrorMessage = "Enter both the input and the output price, or leave both blank."; return; }
        var model = SelectedModel;
        _profiles.SetPrice(model.ProviderId, model.ModelId, input, output);
        _catalog.Rebuild();
        await OnNavigatedToAsync();
        StatusMessage = input is null
            ? $"Price cleared for {model}: its cost is shown as unknown."
            : $"Price saved for {model}: ${input}/${output} per million input/output tokens. Applies to future cost estimates and budgets.";
    });
}
