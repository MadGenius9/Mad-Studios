using CommunityToolkit.Mvvm.ComponentModel;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Install;

namespace MadModStudio.App.ViewModels;

/// <summary>Shared UI state: the current Game Profile.</summary>
public sealed partial class AppState : ObservableObject
{
    private const string CurrentProfileKey = "ui.currentProfile";
    private readonly GameProfileService _profiles;
    private readonly ISettingsRepository _settings;

    [ObservableProperty] private GameProfile? _currentProfile;

    public AppState(GameProfileService profiles, ISettingsRepository settings)
    {
        _profiles = profiles;
        _settings = settings;
    }

    public async Task RefreshAsync()
    {
        var all = await _profiles.ListAsync();
        var id = await _settings.GetAsync(CurrentProfileKey);
        CurrentProfile = all.FirstOrDefault(p => p.Id.ToString() == id) ?? all.FirstOrDefault();
    }

    public async Task SetCurrentAsync(GameProfile? profile)
    {
        await _settings.SetAsync(CurrentProfileKey, profile?.Id.ToString());
        CurrentProfile = profile;
    }
}
