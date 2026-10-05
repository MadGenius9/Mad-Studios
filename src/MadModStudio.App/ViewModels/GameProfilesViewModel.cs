using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Index;
using MadModStudio.Game7DTD.Install;

namespace MadModStudio.App.ViewModels;

public sealed partial class GameProfilesViewModel : PageViewModel
{
    private readonly GameProfileService _profiles;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private GameProfile? _selected;
    [ObservableProperty] private string _indexProgress = "";
    [ObservableProperty] private double _indexPercent;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private string _searchResults = "";
    [ObservableProperty] private string _languageVersion = "";
    [ObservableProperty] private string _extraReferences = "";
    [ObservableProperty] private string _excludedReferences = "";
    [ObservableProperty] private string _indexSummary = "";

    public GameProfilesViewModel(GameProfileService profiles, IDialogService dialogs, AppState state)
    {
        _profiles = profiles;
        _dialogs = dialogs;
        State = state;
    }

    public override string Title => "Game Profiles";
    public AppState State { get; }
    public ObservableCollection<GameProfile> Profiles { get; } = new();
    public ObservableCollection<string> ValidationMessages { get; } = new();

    public override async Task OnNavigatedToAsync() => await ReloadAsync(State.CurrentProfile?.Id);

    private async Task ReloadAsync(Guid? select)
    {
        Profiles.Clear();
        foreach (var p in await _profiles.ListAsync()) Profiles.Add(p);
        Selected = Profiles.FirstOrDefault(p => p.Id == select) ?? Profiles.FirstOrDefault();
        StatusMessage = Profiles.Count == 0 ? "Add your 7 Days to Die installation to get started." : null;
    }

    partial void OnSelectedChanged(GameProfile? value)
    {
        LanguageVersion = value?.Compilation.LanguageVersion ?? "";
        ExtraReferences = value is null ? "" : string.Join(Environment.NewLine, value.Compilation.ExtraReferencePaths);
        ExcludedReferences = value is null ? "" : string.Join(Environment.NewLine, value.Compilation.ExcludedReferences);
        SearchResults = "";
        UpdateIndexSummary();
    }

    private void UpdateIndexSummary()
    {
        var idx = _profiles.GetIndex(Selected);
        if (idx is null) { IndexSummary = Selected is null ? "" : "Not indexed yet."; return; }
        var s = idx.GetSummary();
        IndexSummary = $"{s.Assemblies} assemblies · {s.Types:N0} types · {s.Members:N0} members · {s.XmlFiles} XML files · {s.XmlEntries:N0} XML entries · {s.LocalizationKeys:N0} localization keys";
    }

    [RelayCommand]
    private Task AddInstallation() => RunAsync("Validating installation...", async () =>
    {
        var path = _dialogs.PickFolder("Select your 7 Days to Die installation folder");
        if (path is null) return;
        ValidationMessages.Clear();
        var result = await _profiles.CreateProfileAsync(path);
        foreach (var e in result.Validation.Errors) ValidationMessages.Add("ERROR: " + e);
        foreach (var w in result.Validation.Warnings) ValidationMessages.Add("WARNING: " + w);
        foreach (var n in result.Validation.Notes) ValidationMessages.Add("NOTE: " + n);
        if (result.Profile is null)
        {
            ErrorMessage = "That folder is not a usable 7 Days to Die installation. See the messages below.";
            return;
        }
        if (State.CurrentProfile is null) await State.SetCurrentAsync(result.Profile);
        await ReloadAsync(result.Profile.Id);
        await IndexAsync(result.Profile);
    });

    [RelayCommand]
    private Task Reindex() => Selected is null ? Task.CompletedTask : RunAsync("Indexing...", () => IndexAsync(Selected));

    private async Task IndexAsync(GameProfile profile)
    {
        _cts = new CancellationTokenSource();
        var progress = new Progress<IndexProgress>(p =>
        {
            IndexProgress = p.Message;
            IndexPercent = p.Total > 0 ? 100.0 * p.Current / p.Total : 0;
        });
        var r = await Task.Run(() => _profiles.ReindexAsync(profile, progress, _cts.Token));
        IndexProgress = r.Success
            ? $"Indexed in {r.Duration.TotalSeconds:F1}s{(r.Warnings.Count > 0 ? $" ({r.Warnings.Count} warning(s))" : "")}."
            : "Indexing failed: " + r.Error;
        foreach (var w in r.Warnings.Take(20)) ValidationMessages.Add("WARNING: " + w);
        IndexPercent = 0;
        await ReloadAsync(profile.Id);
        if (State.CurrentProfile?.Id == profile.Id) await State.SetCurrentAsync(Selected);
    }

    [RelayCommand]
    private void CancelIndex() => _cts?.Cancel();

    [RelayCommand]
    private Task MakeCurrent() => Selected is null ? Task.CompletedTask : State.SetCurrentAsync(Selected);

    [RelayCommand]
    private Task Delete() => RunAsync("Removing profile...", async () =>
    {
        if (Selected is null) return;
        if (!_dialogs.Confirm("Remove Game Profile", $"Remove '{Selected.Name}'? Only Mad Mod Studio's index cache is deleted; your game installation is not touched.")) return;
        var wasCurrent = State.CurrentProfile?.Id == Selected.Id;
        await _profiles.DeleteAsync(Selected);
        await ReloadAsync(null);
        if (wasCurrent) await State.SetCurrentAsync(Profiles.FirstOrDefault());
    });

    [RelayCommand]
    private Task SaveCompilerSettings() => RunAsync("Saving...", async () =>
    {
        if (Selected is null) return;
        Selected.Compilation.LanguageVersion = string.IsNullOrWhiteSpace(LanguageVersion) ? "9.0" : LanguageVersion.Trim();
        Selected.Compilation.ExtraReferencePaths = Lines(ExtraReferences);
        Selected.Compilation.ExcludedReferences = Lines(ExcludedReferences);
        await _profiles.SaveAsync(Selected);
        StatusMessage = "Compiler settings saved.";
    });

    private static List<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    [RelayCommand]
    private void Search()
    {
        var idx = _profiles.GetIndex(Selected);
        if (idx is null) { SearchResults = "This profile has not been indexed. Click Reindex Game."; return; }
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        var sb = new StringBuilder();
        var types = idx.SearchTypes(SearchQuery, 30);
        foreach (var t in types)
        {
            sb.AppendLine($"{t.Kind} {t.FullName} : {t.BaseType}   [{t.Assembly}]");
            if (types.Count <= 5)
                foreach (var m in idx.GetMembers(t.FullName, limit: 300)) sb.AppendLine($"      {(m.IsPublic ? "public " : "")}{m.Signature}");
        }
        var members = idx.SearchMembers(SearchQuery, 50);
        if (members.Count > 0)
        {
            sb.AppendLine().AppendLine("— Members —");
            foreach (var m in members) sb.AppendLine($"{m.DeclaringType}: {m.Signature}");
        }
        var xml = idx.SearchXml(SearchQuery, 40);
        if (xml.Count > 0)
        {
            sb.AppendLine().AppendLine("— XML —");
            foreach (var x in xml) sb.AppendLine($"{x.File}:{x.Line}  {x.Path}");
        }
        var loc = idx.SearchLocalization(SearchQuery, 20);
        if (loc.Count > 0)
        {
            sb.AppendLine().AppendLine("— Localization —");
            foreach (var l in loc) sb.AppendLine($"{l.Key} = {l.English}");
        }
        SearchResults = sb.Length == 0 ? "No matches in this installation." : sb.ToString();
    }

    [RelayCommand]
    private void OpenInstallFolder()
    {
        if (Selected != null) _dialogs.OpenFolder(Selected.InstallPath);
    }
}
