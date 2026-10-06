using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Deploy;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.Game7DTD.Scanner;

namespace MadModStudio.App.ViewModels;

/// <summary>A scanned mod plus its planned safe fixes and the outcome of fixing a copy of it.</summary>
public sealed partial class ScanRow : ObservableObject
{
    public ScanRow(ScannedMod mod)
    {
        Mod = mod;
        SafeFixes = SafeFixService.Plan(mod.FolderPath);
    }

    public ScannedMod Mod { get; }
    public IReadOnlyList<SafeFix> SafeFixes { get; }
    public string Name => Mod.Name;
    public string? Version => Mod.Version;
    public ModType Type => Mod.Type;
    public int XmlCount => Mod.XmlCount;
    public int DllCount => Mod.DllCount;
    public int Errors => Mod.Errors;
    public int Warnings => Mod.Warnings;
    public ScanStatus Status => Mod.Status;
    public string SafeFixText => SafeFixes.Count == 0 ? "—" : $"{SafeFixes.Count} available";
    [ObservableProperty] private SafeFixCopyResult? _fixResult;
    [ObservableProperty] private string _fixStatus = "";
}

public sealed partial class BatchScannerViewModel : PageViewModel
{
    private readonly BatchModScanner _scanner;
    private readonly IDialogService _dialogs;
    private readonly ImportWorkflow _import;
    private readonly INavigator _nav;
    private readonly SafeFixService _safeFixes;
    private readonly ModDeployService _deploy;

    [ObservableProperty] private string _modsFolder = "";
    [ObservableProperty] private ScanRow? _selected;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _progressText = "";

    public BatchScannerViewModel(BatchModScanner scanner, IDialogService dialogs, AppState state, ImportWorkflow import, INavigator nav,
        SafeFixService safeFixes, ModDeployService deploy)
    {
        _safeFixes = safeFixes;
        _deploy = deploy;
        _scanner = scanner;
        _dialogs = dialogs;
        _import = import;
        _nav = nav;
        State = state;
    }

    public override string Title => "Batch Scanner";
    public AppState State { get; }
    public ObservableCollection<ScanRow> Rows { get; } = new();
    public bool HasSafeFixes => Rows.Any(r => r.SafeFixes.Count > 0 && r.FixResult is null);
    public bool HasFixedPackages => Rows.Any(r => r.FixResult?.Packaged == true);

    private void RefreshFixState()
    {
        OnPropertyChanged(nameof(HasSafeFixes));
        OnPropertyChanged(nameof(HasFixedPackages));
        if (Selected != null) OnSelectedChanged(Selected);
    }

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
        foreach (var r in rows) Rows.Add(new ScanRow(r));
        var fixable = Rows.Count(r => r.SafeFixes.Count > 0);
        StatusMessage = $"{rows.Count} mod(s): {rows.Count(r => r.Status == ScanStatus.Broken)} broken, {rows.Count(r => r.Status == ScanStatus.Warning)} with warnings, {rows.Count(r => r.Status == ScanStatus.ClientRequirementDetected)} need client install, {fixable} with safe fixes." +
            (profile is null ? " No Game Profile selected: game-aware checks were skipped." : "");
        RefreshFixState();
        ProgressText = "Read-only scan: nothing in the Mods folder was modified.";
    });

    partial void OnSelectedChanged(ScanRow? value)
    {
        if (value is null) { Details = ""; return; }
        var row = value;
        var mod = row.Mod;
        var sb = new StringBuilder();
        if (row.SafeFixes.Count > 0)
        {
            sb.AppendLine("SAFE FIXES (applied to an imported copy, never to this folder):");
            foreach (var f in row.SafeFixes) sb.AppendLine("  • " + f.Description);
            if (row.FixStatus.Length > 0) sb.AppendLine("  Result: " + row.FixStatus);
            sb.AppendLine();
        }
        sb.AppendLine($"{mod.Name} {mod.Version}");
        sb.AppendLine(mod.FolderPath).AppendLine();
        if (mod.Error != null) sb.AppendLine("ERROR: " + mod.Error);
        if (mod.Analysis is { } a)
        {
            sb.AppendLine(a.Summary());
            sb.AppendLine($"Server-side: {a.Side.Result}");
            foreach (var r in a.Side.Reasons) sb.AppendLine("  " + r);
            sb.AppendLine($"EAC: {a.Eac.Result}");
            foreach (var r in a.Eac.Reasons) sb.AppendLine("  " + r);
            foreach (var h in a.HarmonyPatches) sb.AppendLine($"Harmony: {h.PatchKind} {h.TargetDisplay}");
        }
        if (mod.Validation is { } v)
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
        var imported = await _import.ImportAsync(Selected.Mod.FolderPath);
        if (imported.Count > 0) await _nav.OpenProjectAsync(imported[0].Project.Id);
    });

    /// <summary>Imports a copy of every mod with safe fixes, fixes it (revision before/after) and rebuilds it. The Mods folder is not modified.</summary>
    [RelayCommand]
    private Task RepairAllSafeFixes() => RunAsync("Applying safe fixes to copies...", async () =>
    {
        var targets = Rows.Where(r => r.SafeFixes.Count > 0 && r.FixResult is null).ToList();
        if (targets.Count == 0) { StatusMessage = "No safe fixes to apply."; return; }
        if (!_dialogs.Confirm("Repair All Safe Fixes",
                $"{targets.Count} mod(s) have safe fixes ({targets.Sum(t => t.SafeFixes.Count)} fix(es) in total).\n\n" +
                "Each mod is imported as a new project (a copy), fixed with a revision before and after, and rebuilt and validated. " +
                "Nothing in the Mods folder is changed; use Deploy Fixed Mods afterwards to install the results (with backup and undo).")) return;
        var profileId = State.CurrentProfile?.Id;
        for (var i = 0; i < targets.Count; i++)
        {
            var row = targets[i];
            ProgressText = $"{i + 1}/{targets.Count} {row.Name}";
            var result = await Task.Run(() => _safeFixes.FixCopyAsync(row.Mod.FolderPath, profileId));
            row.FixResult = result;
            row.FixStatus = result.Error != null ? "FAILED: " + result.Error
                : result.Packaged ? $"Fixed ({result.Applied.Count}) · builds and validates · ready to deploy"
                : $"Fixed ({result.Applied.Count}) · still not clean: {result.Build?.Summary}";
        }
        var ok = targets.Count(t => t.FixResult?.Packaged == true);
        StatusMessage = $"Safe fixes applied to {targets.Count} copy/copies: {ok} build and validate cleanly. Fixed copies are in My Mods.";
        ProgressText = "The Mods folder was not modified.";
        RefreshFixState();
    });

    /// <summary>Installs the clean fixed copies into the game profile's Mods folder (each with backup and undo).</summary>
    [RelayCommand]
    private Task DeployFixedMods() => RunAsync("Deploying fixed mods...", async () =>
    {
        var profile = State.CurrentProfile;
        if (profile is null) { ErrorMessage = "Select a Game Profile first."; return; }
        var profileMods = Path.GetFullPath(profile.ModsPath ?? Path.Combine(profile.InstallPath, "Mods")).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(profileMods, Path.GetFullPath(ModsFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = $"The scanned folder is not the current Game Profile's Mods folder ({profileMods}). Fixed mods are only deployed into the profile's Mods folder.";
            return;
        }
        var ready = Rows.Where(r => r.FixResult?.Packaged == true && r.FixResult.Project != null).ToList();
        if (ready.Count == 0) { StatusMessage = "No fixed mods are ready to deploy."; return; }
        if (!_dialogs.Confirm("Deploy Fixed Mods",
                $"Replace {ready.Count} mod folder(s) in\n{profileMods}\nwith their fixed copies?\n\n{string.Join("\n", ready.Select(r => "  • " + r.Name))}\n\n" +
                "Each original folder is backed up first; every deployment can be undone from the project's Build tab.")) return;
        var done = 0;
        foreach (var row in ready)
        {
            var project = row.FixResult!.Project!;
            try
            {
                var r = await Task.Run(() => _deploy.DeployAsync(project, profile));
                row.FixStatus = "Deployed · " + r.Message + (r.Warnings.Count > 0 ? "  WARNING: " + string.Join("  ", r.Warnings) : "");
                done++;
            }
            catch (DeployException ex)
            {
                row.FixStatus = "Deploy failed: " + ex.Message;
            }
        }
        StatusMessage = $"Deployed {done} of {ready.Count} fixed mod(s). Undo is available per project (My Mods → project → Build). Re-scan to see the new state.";
        RefreshFixState();
    });
}
