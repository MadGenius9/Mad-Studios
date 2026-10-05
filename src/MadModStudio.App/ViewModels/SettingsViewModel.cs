using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Models;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Secrets;
using MadModStudio.App.Services;
using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Knowledge;

namespace MadModStudio.App.ViewModels;

/// <summary>One provider card in Settings → AI Providers.</summary>
public sealed partial class ProviderItem : ObservableObject
{
    public required IAIProvider Provider { get; init; }
    public string Id => Provider.Id;
    public string Name => Provider.DisplayName;
    public string Destination => Provider.Destination;
    public bool IsCustom => Provider.Id == ProviderIds.Custom;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private ConnectionState _state;

    public void Refresh()
    {
        var s = Provider.Status;
        State = !Provider.IsConfigured ? ConnectionState.NotConfigured : s.State == ConnectionState.NotConfigured ? ConnectionState.Unverified : s.State;
        Status = State switch
        {
            ConnectionState.NotConfigured => "NOT CONFIGURED",
            ConnectionState.Unverified => "Key stored — not verified (click Test Connection)",
            ConnectionState.Connected => $"CONNECTED — {s.Message} ({s.CheckedUtc?.LocalDateTime:t})",
            ConnectionState.Failed => $"CONNECTION FAILED — {s.Message}",
            _ => s.Message,
        };
    }
}

public sealed partial class TaskPreference : ObservableObject
{
    public AITaskType TaskType { get; init; }
    public IReadOnlyList<ModelChoice> Options { get; init; } = Array.Empty<ModelChoice>();
    [ObservableProperty] private ModelChoice _selected = ModelChoice.Auto;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISecretStore _secrets;
    private readonly IAIProviderRegistry _providers;
    private readonly IModelCatalog _catalog;
    private readonly AIPolicy _policy;
    private readonly ISettingsRepository _settings;
    private readonly IDialogService _dialogs;
    private readonly ModelProfileStore _profiles;

    [ObservableProperty] private ModelChoice? _defaultModel;
    [ObservableProperty] private RoutingMode _routingMode;
    [ObservableProperty] private AgentControlLevel _controlLevel;
    [ObservableProperty] private bool _allowCrossProvider;
    [ObservableProperty] private bool _allowSource;
    [ObservableProperty] private bool _allowLogs;
    [ObservableProperty] private bool _askBeforeEachRun;
    [ObservableProperty] private int _maxConcurrentAgents;
    [ObservableProperty] private int _maxRepairAttempts;
    [ObservableProperty] private int _maxModelsPerTask;
    [ObservableProperty] private bool _autoEscalate;
    [ObservableProperty] private int _failureThreshold;
    [ObservableProperty] private string _sessionBudget = "";
    [ObservableProperty] private string _projectBudget = "";
    [ObservableProperty] private string _taskBudget = "";
    [ObservableProperty] private string _customUrl = "";
    [ObservableProperty] private string _customName = "";

    public SettingsViewModel(ISecretStore secrets, IAIProviderRegistry providers, IModelCatalog catalog, AIPolicy policy, ISettingsRepository settings,
        IDialogService dialogs, AppPaths paths, ModelProfileStore profiles)
    {
        _secrets = secrets; _providers = providers; _catalog = catalog; _policy = policy; _settings = settings; _dialogs = dialogs; _profiles = profiles;
        Paths = paths;
    }

    public override string Title => "Settings";
    public AppPaths Paths { get; }
    public string SecretStoreDescription => _secrets.Description;
    public bool CanStoreKey => _secrets.CanWrite;
    public string ProfilesPath => _profiles.FilePath;
    public ObservableCollection<ProviderItem> Providers { get; } = new();
    public ObservableCollection<ModelChoice> ModelChoices { get; } = new();
    public ObservableCollection<TaskPreference> Preferences { get; } = new();
    public Array RoutingModes { get; } = Enum.GetValues(typeof(RoutingMode));
    public Array ControlLevels { get; } = Enum.GetValues(typeof(AgentControlLevel));

    public override Task OnNavigatedToAsync()
    {
        Providers.Clear();
        foreach (var p in _providers.All)
        {
            var item = new ProviderItem { Provider = p };
            item.Refresh();
            Providers.Add(item);
        }
        LoadModelChoices();
        RoutingMode = _policy.RoutingMode;
        ControlLevel = _policy.ControlLevel;
        AllowCrossProvider = _policy.AllowCrossProviderRouting;
        AllowSource = _policy.AllowSourceToExternal;
        AllowLogs = _policy.AllowLogsToExternal;
        AskBeforeEachRun = _policy.AskBeforeEachRun;
        MaxConcurrentAgents = _policy.MaxConcurrentAgents;
        MaxRepairAttempts = _policy.MaxRepairAttempts;
        MaxModelsPerTask = _policy.MaxModelsPerTask;
        AutoEscalate = _policy.AutoEscalate;
        FailureThreshold = _policy.EscalationFailureThreshold;
        SessionBudget = _policy.SessionBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        ProjectBudget = _policy.ProjectBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        TaskBudget = _policy.TaskBudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? "";
        CustomUrl = _policy.CustomEndpointUrl ?? "";
        CustomName = _policy.CustomEndpointName;
        return Task.CompletedTask;
    }

    private void LoadModelChoices()
    {
        ModelChoices.Clear();
        ModelChoices.Add(new ModelChoice(null, "(none)"));
        foreach (var m in _catalog.Models.Where(m => m.Available)) ModelChoices.Add(new ModelChoice(m.Key, m.ToString()));
        DefaultModel = ModelChoices.FirstOrDefault(c => c.Key == _policy.DefaultModel) ?? ModelChoices[0];
        Preferences.Clear();
        var options = new[] { ModelChoice.Auto }.Concat(ModelChoices.Skip(1)).ToList();
        foreach (var t in Enum.GetValues<AITaskType>())
            Preferences.Add(new TaskPreference { TaskType = t, Options = options, Selected = options.FirstOrDefault(o => o.Key != null && o.Key == _policy.TaskPreferences.GetValueOrDefault(t)) ?? ModelChoice.Auto });
    }

    /// <summary>Called by the view with the PasswordBox contents; keys are never bound to visible properties.</summary>
    public void SaveKey(ProviderItem item, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { ErrorMessage = "Enter a key first."; return; }
        try
        {
            _secrets.Set(SecretNames.ApiKey(item.Id), key.Trim());
            StatusMessage = $"{item.Name} key saved securely. Click Test Connection to verify it.";
            ErrorMessage = null;
        }
        catch (NotSupportedException ex) { ErrorMessage = ex.Message; }
        item.Refresh();
    }

    [RelayCommand]
    private void RemoveKey(ProviderItem? item)
    {
        if (item is null || !_dialogs.Confirm("Remove API key", $"Remove the stored {item.Name} key?")) return;
        try { _secrets.Delete(SecretNames.ApiKey(item.Id)); }
        catch (NotSupportedException ex) { ErrorMessage = ex.Message; }
        item.Refresh();
    }

    [RelayCommand]
    private Task TestConnection(ProviderItem? item) => item is null ? Task.CompletedTask : RunAsync($"Testing {item.Name}…", async () =>
    {
        await Task.Run(() => item.Provider.TestConnectionAsync());
        item.Refresh();
        if (item.State == ConnectionState.Connected)
        {
            await Task.Run(() => _catalog.RefreshAsync());
            LoadModelChoices();
        }
    });

    [RelayCommand]
    private Task RefreshModels() => RunAsync("Discovering models from configured providers…", async () =>
    {
        var results = await Task.Run(() => _catalog.RefreshAsync());
        LoadModelChoices();
        StatusMessage = results.Count == 0 ? "No providers are configured." : string.Join("  ", results.Select(r => r.Success ? $"{r.ProviderId}: {r.ModelCount} model(s)" : $"{r.ProviderId}: {r.Error}"));
    });

    [RelayCommand]
    private Task SaveAISettings() => RunAsync("Saving…", async () =>
    {
        static decimal? Money(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        _policy.DefaultModel = DefaultModel?.Key;
        _policy.DefaultProvider = ModelKey.Parse(DefaultModel?.Key)?.Provider;
        _policy.RoutingMode = RoutingMode;
        _policy.ControlLevel = ControlLevel;
        _policy.AllowCrossProviderRouting = AllowCrossProvider;
        _policy.AllowSourceToExternal = AllowSource;
        _policy.AllowLogsToExternal = AllowLogs;
        _policy.AskBeforeEachRun = AskBeforeEachRun;
        _policy.MaxConcurrentAgents = MaxConcurrentAgents;
        _policy.MaxRepairAttempts = MaxRepairAttempts;
        _policy.MaxModelsPerTask = MaxModelsPerTask;
        _policy.AutoEscalate = AutoEscalate;
        _policy.EscalationFailureThreshold = FailureThreshold;
        _policy.SessionBudgetUsd = Money(SessionBudget);
        _policy.ProjectBudgetUsd = Money(ProjectBudget);
        _policy.TaskBudgetUsd = Money(TaskBudget);
        _policy.CustomEndpointUrl = string.IsNullOrWhiteSpace(CustomUrl) ? null : CustomUrl.Trim();
        _policy.CustomEndpointName = string.IsNullOrWhiteSpace(CustomName) ? "Custom (OpenAI-compatible)" : CustomName.Trim();
        _policy.TaskPreferences = Preferences.Where(p => p.Selected.Key != null).ToDictionary(p => p.TaskType, p => p.Selected.Key!);
        await _policy.SaveAsync(_settings);
        foreach (var p in Providers) p.Refresh();
        StatusMessage = "AI settings saved.";
    });

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(Paths.Root);

    [RelayCommand]
    private void OpenProfilesFile() => _dialogs.OpenFolder(_profiles.FilePath);
}
