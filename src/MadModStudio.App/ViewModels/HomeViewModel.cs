using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly ProjectService _projects;
    private readonly INavigator _nav;
    private readonly ImportWorkflow _import;

    [ObservableProperty] private string _prompt = "";

    public HomeViewModel(ProjectService projects, INavigator nav, ImportWorkflow import, AppState state)
    {
        _projects = projects;
        _nav = nav;
        _import = import;
        State = state;
    }

    public override string Title => "Home";
    public AppState State { get; }
    public ObservableCollection<ModProject> RecentProjects { get; } = new();
    public ObservableCollection<ModProject> RecentRepairs { get; } = new();

    public override async Task OnNavigatedToAsync()
    {
        var all = await _projects.ListAsync();
        RecentProjects.Clear();
        foreach (var p in all.Where(p => p.Origin != ProjectOrigin.Repair).Take(8)) RecentProjects.Add(p);
        RecentRepairs.Clear();
        foreach (var p in all.Where(p => p.Origin == ProjectOrigin.Repair).Take(6)) RecentRepairs.Add(p);
    }

    [RelayCommand]
    private Task StartBuilding() => _nav.StartNewModAsync(Prompt);

    [RelayCommand]
    private Task OpenProject(ModProject? p) => p is null ? Task.CompletedTask : _nav.OpenProjectAsync(p.Id);

    [RelayCommand]
    private Task ImportZip() => Import(false);

    [RelayCommand]
    private Task ImportFolder() => Import(true);

    private Task Import(bool folder) => RunAsync("Importing...", async () =>
    {
        var path = _import.PickSource(folder);
        if (path is null) return;
        var imported = await _import.ImportAsync(path);
        if (imported.Count > 0) await _nav.OpenProjectAsync(imported[0].Project.Id);
    });

    [RelayCommand]
    private Task Go(string key) => _nav.NavigateAsync(key);
}
