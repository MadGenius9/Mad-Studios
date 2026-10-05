using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Scanner;

namespace MadModStudio.App.ViewModels;

public sealed partial class BatchScannerViewModel : PageViewModel
{
    private readonly BatchModScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly ImportWorkflow _import;
    private readonly INavigator _nav;

    [ObservableProperty] private string _modsFolder = "";
    [ObservableProperty] private ScannedMod? _selected;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _progressText = "";

    public BatchScannerViewModel(BatchModScanner scanner, IDialogService dialogs, AppState state, ImportWorkflow import, INavigator nav)
    {
        _scanner = scanner;
        _dialogs = dialogs;
        _import = import;
        _nav = nav;
        State = state;
    }

    public override string Title => "Batch Scanner";
    public AppState State { get; }
    public ObservableCollection<ScannedMod> Rows { get; } = new();

    public override Task OnNavigatedToAsync()
    {
        if (string.IsNullOrEmpty(ModsFolder)) ModsFolder = State.CurrentProfile?.ModsPath ?? "";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Browse()
    {
        var p = _dialogs.PickFolder("Select a Mods folder to scan", ModsFolder);
        if (p != null) ModsFolder = p;
    }

    [RelayCommand]
    private Task Scan() => RunAsync("Scanning...", async () =>
    {
        if (!Directory.Exists(ModsFolder)) { ErrorMessage = "Choose an existing Mods folder."; return; }
        Rows.Clear();
        var progress = new Progress<(int Done, int Total, string Name)>(p => ProgressText = p.Total == 0 ? "" : $"{p.Done}/{p.Total} {p.Name}");
        var folder = ModsFolder;
        var profile = State.CurrentProfile;
        var rows = await Task.Run(() => _scanner.ScanAsync(folder, profile, progress));
        foreach (var r in rows) Rows.Add(r);
        StatusMessage = $"{rows.Count} mod(s): {rows.Count(r => r.Status == ScanStatus.Broken)} broken, {rows.Count(r => r.Status == ScanStatus.Warning)} with warnings, {rows.Count(r => r.Status == ScanStatus.ClientRequirementDetected)} need client install." +
            (profile is null ? " No Game Profile selected: game-aware checks were skipped." : "");
        ProgressText = "Read-only scan: nothing in the Mods folder was modified.";
    });

    partial void OnSelectedChanged(ScannedMod? value)
    {
        if (value is null) { Details = ""; return; }
        var sb = new StringBuilder();
        sb.AppendLine($"{value.Name} {value.Version}");
        sb.AppendLine(value.FolderPath).AppendLine();
        if (value.Error != null) sb.AppendLine("ERROR: " + value.Error);
        if (value.Analysis is { } a)
        {
            sb.AppendLine(a.Summary());
            sb.AppendLine($"Server-side: {a.Side.Result}");
            foreach (var r in a.Side.Reasons) sb.AppendLine("  " + r);
            sb.AppendLine($"EAC: {a.Eac.Result}");
            foreach (var r in a.Eac.Reasons) sb.AppendLine("  " + r);
            foreach (var h in a.HarmonyPatches) sb.AppendLine($"Harmony: {h.PatchKind} {h.TargetDisplay}");
        }
        if (value.Validation is { } v)
        {
            sb.AppendLine().AppendLine("Validation:");
            foreach (var f in v.Findings.Where(f => f.Severity != Severity.Pass))
                sb.AppendLine($"  {f.Severity.ToString().ToUpperInvariant(),-7} {f.Message}{(f.FilePath != null ? $"  ({f.FilePath}{(f.Line != null ? ":" + f.Line : "")})" : "")}");
        }
        Details = sb.ToString();
    }

    [RelayCommand]
    private Task ImportSelected() => RunAsync("Importing...", async () =>
    {
        if (Selected is null) return;
        var imported = await _import.ImportAsync(Selected.FolderPath);
        if (imported.Count > 0) await _nav.OpenProjectAsync(imported[0].Project.Id);
    });
}
