using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.AI;
using MadModStudio.AI.Engines;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;

namespace MadModStudio.App.ViewModels;

public sealed record LogInput(string Path, string Kind)
{
    public string Display => $"[{Kind}] {System.IO.Path.GetFileName(Path)}";
}

/// <summary>Repair Mode: import broken mod + optional logs/working version → diagnosis → edit/AI repair → build.</summary>
public sealed partial class RepairViewModel : PageViewModel
{
    private readonly ImportWorkflow _import;
    private readonly IDialogService _dialogs;
    private readonly ProjectService _projects;
    private readonly RepairService _repair;
    private readonly AIRepairEngine _ai;
    private readonly IAIProvider _provider;
    private readonly AIOptions _options;
    private readonly INavigator _nav;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _brokenPath = "";
    [ObservableProperty] private string _workingPath = "";
    [ObservableProperty] private string _diagnosisText = "";
    [ObservableProperty] private ModProject? _project;
    [ObservableProperty] private Diagnosis? _diagnosis;
    [ObservableProperty] private LogInput? _selectedLog;

    public RepairViewModel(ImportWorkflow import, IDialogService dialogs, ProjectService projects, RepairService repair, AIRepairEngine ai, IAIProvider provider, AIOptions options, INavigator nav, AppState state)
    {
        _import = import;
        _dialogs = dialogs;
        _projects = projects;
        _repair = repair;
        _ai = ai;
        _provider = provider;
        _options = options;
        _nav = nav;
        State = state;
    }

    public override string Title => "Repair Mod";
    public AppState State { get; }
    public bool IsAIConfigured => _provider.IsConfigured;
    public ObservableCollection<LogInput> Logs { get; } = new();
    public ObservableCollection<string> DependencyMods { get; } = new();
    public ObservableCollection<string> Events { get; } = new();

    [RelayCommand] private void BrowseBrokenZip() { if (_dialogs.PickFile("Broken mod ZIP", ImportWorkflow.ZipFilter) is { } p) BrokenPath = p; }
    [RelayCommand] private void BrowseBrokenFolder() { if (_dialogs.PickFolder("Broken mod folder") is { } p) BrokenPath = p; }
    [RelayCommand] private void BrowseWorkingZip() { if (_dialogs.PickFile("Last known working version (ZIP)", ImportWorkflow.ZipFilter) is { } p) WorkingPath = p; }
    [RelayCommand] private void BrowseWorkingFolder() { if (_dialogs.PickFolder("Last known working version (folder)") is { } p) WorkingPath = p; }
    [RelayCommand] private void AddClientLog() { foreach (var f in _dialogs.PickFiles("Client log", "Logs (*.txt;*.log)|*.txt;*.log|All files|*.*")) Logs.Add(new LogInput(f, "client")); }
    [RelayCommand] private void AddServerLog() { foreach (var f in _dialogs.PickFiles("Server log", "Logs (*.txt;*.log)|*.txt;*.log|All files|*.*")) Logs.Add(new LogInput(f, "server")); }
    [RelayCommand] private void RemoveLog() { if (SelectedLog != null) Logs.Remove(SelectedLog); }
    [RelayCommand] private void AddDependencyMod() { if (_dialogs.PickFolder("Dependency mod folder (its DLLs will be referenced when compiling)") is { } p) DependencyMods.Add(p); }

    [RelayCommand]
    private Task Diagnose() => RunAsync("Importing and diagnosing...", async () =>
    {
        if (string.IsNullOrWhiteSpace(BrokenPath)) { ErrorMessage = "Select the broken mod (ZIP or folder)."; return; }
        Events.Clear();
        var imported = await _import.ImportAsync(BrokenPath, ProjectOrigin.Repair);
        if (imported.Count == 0) return;
        var project = imported[0].Project;
        // Dependency mods' DLLs become compile references (never packaged).
        foreach (var dep in DependencyMods)
            project.AdditionalReferencePaths.AddRange(Directory.GetFiles(dep, "*.dll", SearchOption.AllDirectories));
        await _projects.SaveAsync(project);

        var inputs = new RepairInputs { WorkingVersionPath = string.IsNullOrWhiteSpace(WorkingPath) ? null : WorkingPath };
        foreach (var l in Logs)
        {
            var copy = await _projects.AttachFileAsync(project, l.Path, "logs");
            inputs.Logs.Add((copy, l.Kind));
        }
        inputs.DependencyMods.AddRange(DependencyMods);
        var progress = new Progress<string>(s => Events.Add(s));
        Project = project;
        Diagnosis = await Task.Run(() => _repair.DiagnoseAsync(project, inputs, progress));
        DiagnosisText = Diagnosis.Report;
        StatusMessage = "Diagnosis complete. Review it, open the project to edit, or run AI Repair.";
    });

    [RelayCommand]
    private Task OpenProject() => Project is null ? Task.CompletedTask : _nav.OpenProjectAsync(Project.Id);

    [RelayCommand]
    private Task AIRepair() => RunAsync("AI repair in progress...", async () =>
    {
        if (Project is null) return;
        _cts = new CancellationTokenSource();
        var progress = new Progress<AIEvent>(e => { if (e.Kind != AIEventKind.Thinking) Events.Add($"[{e.Kind}] {e.Message}"); });
        var project = Project;
        var options = new RepairRequestOptions { MaxAttempts = _options.MaxAutoRepairAttempts, Diagnosis = Diagnosis };
        var outcome = await Task.Run(() => _ai.RepairAsync(project, options, progress, _cts.Token));
        StatusMessage = outcome.StopReason;
        foreach (var a in outcome.Attempts)
            Events.Add($"Attempt {a.Number}: {a.Summary} → compile {(a.CompileSucceededAfter ? "OK" : "failed")}, {a.ValidationErrorsAfter} validation error(s)");
    });

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
