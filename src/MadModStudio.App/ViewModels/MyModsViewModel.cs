using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

public sealed partial class MyModsViewModel : PageViewModel
{
    private readonly ProjectService _projects;
    private readonly INavigator _nav;
    private readonly ImportWorkflow _import;
    private readonly IDialogService _dialogs;

    public MyModsViewModel(ProjectService projects, INavigator nav, ImportWorkflow import, IDialogService dialogs)
    {
        _projects = projects;
        _nav = nav;
        _import = import;
        _dialogs = dialogs;
    }

    public override string Title => "My Mods";
    public ObservableCollection<ModProject> Projects { get; } = new();

    public override async Task OnNavigatedToAsync()
    {
        Projects.Clear();
        foreach (var p in await _projects.ListAsync()) Projects.Add(p);
        StatusMessage = Projects.Count == 0 ? "No projects yet. Import a mod or create a new one." : $"{Projects.Count} project(s)";
    }

    [RelayCommand]
    private Task Open(ModProject? p) => p is null ? Task.CompletedTask : _nav.OpenProjectAsync(p.Id);

    [RelayCommand]
    private Task ImportZip() => Import(false);

    [RelayCommand]
    private Task ImportFolder() => Import(true);

    private Task Import(bool folder) => RunAsync("Importing...", async () =>
    {
        var path = _import.PickSource(folder);
        if (path is null) return;
        var imported = await _import.ImportAsync(path);
        await OnNavigatedToAsync();
        if (imported.Count == 1) await _nav.OpenProjectAsync(imported[0].Project.Id);
    });

    [RelayCommand]
    private Task Delete(ModProject? p) => RunAsync("Deleting...", async () =>
    {
        if (p is null) return;
        if (!_dialogs.Confirm("Delete project", $"Delete the project '{p.Name}' and its workspace (history, builds, packages)?\n\nThe original file you imported from is not affected.")) return;
        await _projects.DeleteAsync(p);
        await OnNavigatedToAsync();
    });

    [RelayCommand]
    private void OpenWorkspace(ModProject? p)
    {
        if (p != null) _dialogs.OpenFolder(p.WorkspacePath);
    }
}
