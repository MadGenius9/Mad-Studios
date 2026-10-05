using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Secrets;
using MadModStudio.App.Services;
using MadModStudio.Core;
using MadModStudio.Core.Abstractions;

namespace MadModStudio.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISecretStore _secrets;
    private readonly IAIProvider _provider;
    private readonly AIOptions _options;
    private readonly ISettingsRepository _settings;
    private readonly IDialogService _dialogs;

    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private int _maxAttempts;

    public SettingsViewModel(ISecretStore secrets, IAIProvider provider, AIOptions options, ISettingsRepository settings, IDialogService dialogs, AppPaths paths)
    {
        _secrets = secrets;
        _provider = provider;
        _options = options;
        _settings = settings;
        _dialogs = dialogs;
        Paths = paths;
    }

    public override string Title => "Settings";
    public AppPaths Paths { get; }
    public string SecretStoreDescription => _secrets.Description;
    public string ProviderName => _provider.DisplayName;
    public bool CanStoreKey => _secrets.CanWrite;

    public override Task OnNavigatedToAsync()
    {
        Model = _options.Model;
        MaxAttempts = _options.MaxAutoRepairAttempts;
        UpdateKeyStatus();
        return Task.CompletedTask;
    }

    private void UpdateKeyStatus() =>
        KeyStatus = _provider.IsConfigured ? "An API key is stored (hidden)." : "No API key configured. AI features are disabled until one is added.";

    /// <summary>Called from the view with the PasswordBox contents; the key is never bound to a visible property.</summary>
    public void SaveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) { ErrorMessage = "Enter a key first."; return; }
        try
        {
            _secrets.Set(AnthropicProvider.SecretName, key.Trim());
            StatusMessage = "API key saved securely.";
            ErrorMessage = null;
        }
        catch (NotSupportedException ex) { ErrorMessage = ex.Message; }
        UpdateKeyStatus();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        if (!_dialogs.Confirm("Remove API key", "Remove the stored API key?")) return;
        try { _secrets.Delete(AnthropicProvider.SecretName); StatusMessage = "API key removed."; }
        catch (NotSupportedException ex) { ErrorMessage = ex.Message; }
        UpdateKeyStatus();
    }

    [RelayCommand]
    private Task SaveAISettings() => RunAsync("Saving...", async () =>
    {
        _options.Model = string.IsNullOrWhiteSpace(Model) ? AnthropicProvider.DefaultModelId : Model.Trim();
        _options.MaxAutoRepairAttempts = Math.Clamp(MaxAttempts, 1, 10);
        await _options.SaveAsync(_settings);
        Model = _options.Model;
        MaxAttempts = _options.MaxAutoRepairAttempts;
        StatusMessage = "AI settings saved.";
    });

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(Paths.Root);
}
