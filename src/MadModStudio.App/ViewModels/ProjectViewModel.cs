using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using MadModStudio.AI;
using MadModStudio.AI.Engines;
using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Core.Pipeline;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.Game7DTD.Repair;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Comparison;
using MadModStudio.ModAnalysis.Decompilation;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.App.ViewModels;

public sealed partial class FileNode : ObservableObject
{
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool IsFolder { get; init; }
    public ObservableCollection<FileNode> Children { get; } = new();
    public string Glyph => IsFolder ? "" : Path.GetExtension(Name).ToLowerInvariant() switch
    {
        ".cs" => "",
        ".xml" => "",
        ".dll" => "",
        _ => "",
    };
}

public sealed partial class StageItem : ObservableObject
{
    public PipelineStage Stage { get; init; }
    public string Name { get; init; } = "";
    [ObservableProperty] private StageStatus _status = StageStatus.Pending;
    [ObservableProperty] private string _message = "";
}

public sealed partial class ProjectViewModel : PageViewModel
{
    private readonly ProjectService _projects;
    private readonly GameProfileService _profiles;
    private readonly ModAnalyzer _analyzer;
    private readonly ModBuildPipeline _pipeline;
    private readonly AssemblyInspector _inspector;
    private readonly DecompilerService _decompiler;
    private readonly VersionComparer _comparer;
    private readonly RepairService _repair;
    private readonly AIRepairEngine _ai;
    private readonly IAIProvider _provider;
    private readonly AIOptions _aiOptions;
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private ModProject? _project;
    [ObservableProperty] private GameProfile? _profile;
    [ObservableProperty] private Guid? _selectedProfileId;
    [ObservableProperty] private int _selectedTab;

    // Editor
    [ObservableProperty] private FileNode? _selectedFile;
    [ObservableProperty] private TextDocument _editorDocument = new();
    [ObservableProperty] private IHighlightingDefinition? _editorHighlighting;
    [ObservableProperty] private bool _isEditorReadOnly = true;
    [ObservableProperty] private string _editorTitle = "No file open";
    [ObservableProperty] private string _sourceKind = "";
    [ObservableProperty] private bool _isDecompiledView;
    [ObservableProperty] private bool _isDirty;
    private string? _openPath;
    private string _savedText = "";

    // Analysis
    [ObservableProperty] private ModAnalysisReport? _analysis;
    [ObservableProperty] private string _analysisSummary = "";
    [ObservableProperty] private DllReport? _selectedDll;
    [ObservableProperty] private TypeReport? _selectedType;
    [ObservableProperty] private string _compareText = "";

    // Build
    [ObservableProperty] private BuildConfiguration _configuration = BuildConfiguration.Release;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string? _lastPackagePath;
    [ObservableProperty] private string _buildSummary = "";
    [ObservableProperty] private string _compilerLog = "";

    // AI
    [ObservableProperty] private string _aiInstructions = "";

    public ProjectViewModel(ProjectService projects, GameProfileService profiles, ModAnalyzer analyzer, ModBuildPipeline pipeline,
        AssemblyInspector inspector, DecompilerService decompiler, VersionComparer comparer, RepairService repair, AIRepairEngine ai,
        IAIProvider provider, AIOptions aiOptions, IDialogService dialogs)
    {
        _projects = projects;
        _profiles = profiles;
        _analyzer = analyzer;
        _pipeline = pipeline;
        _inspector = inspector;
        _decompiler = decompiler;
        _comparer = comparer;
        _repair = repair;
        _ai = ai;
        _provider = provider;
        _aiOptions = aiOptions;
        _dialogs = dialogs;
        foreach (var (stage, name) in new[] { (PipelineStage.Analyze, "Analyze"), (PipelineStage.GenerateOrRepair, "Generate / Repair"), (PipelineStage.Compile, "Compile"), (PipelineStage.Validate, "Validate"), (PipelineStage.Package, "Package") })
            Stages.Add(new StageItem { Stage = stage, Name = name });
        EditorDocument.TextChanged += (_, _) => IsDirty = !IsEditorReadOnly && EditorDocument.Text != _savedText;
    }

    public override string Title => Project?.Name ?? "Project";
    public bool IsAIConfigured => _provider.IsConfigured;
    public int MaxAttempts => _aiOptions.MaxAutoRepairAttempts;
    public Array Configurations { get; } = Enum.GetValues(typeof(BuildConfiguration));

    public ObservableCollection<FileNode> FileTree { get; } = new();
    public ObservableCollection<GameProfile> Profiles { get; } = new();
    public ObservableCollection<StageItem> Stages { get; } = new();
    public ObservableCollection<string> PipelineEvents { get; } = new();
    public ObservableCollection<ModDiagnostic> Diagnostics { get; } = new();
    public ObservableCollection<ValidationFinding> Findings { get; } = new();
    public ObservableCollection<RevisionRecord> Revisions { get; } = new();
    public ObservableCollection<BuildRecord> Builds { get; } = new();
    public ObservableCollection<HarmonyPatchInfo> HarmonyPatches { get; } = new();
    public ObservableCollection<string> AIEvents { get; } = new();
    public ObservableCollection<string> AttachedLogs { get; } = new();

    /// <summary>Raised so the view can scroll the editor to a line.</summary>
    public event Action<int>? GoToLineRequested;

    public async Task LoadAsync(Guid id)
    {
        await RunAsync("Loading project...", async () =>
        {
            Project = await _projects.GetAsync(id) ?? throw new InvalidOperationException("Project not found.");
            OnPropertyChanged(nameof(Title));
            Profiles.Clear();
            foreach (var p in await _profiles.ListAsync()) Profiles.Add(p);
            Profile = Profiles.FirstOrDefault(p => p.Id == Project.GameProfileId);
            SelectedProfileId = Profile?.Id;
            VersionText = Project.Version;
            ReloadTree();
            await ReloadHistoryAsync();
            await AnalyzeCoreAsync();
            AttachedLogs.Clear();
            var logDir = Path.Combine(Project.Workspace.Attachments, "logs");
            if (Directory.Exists(logDir)) foreach (var f in Directory.GetFiles(logDir)) AttachedLogs.Add(f);
            var firstCode = Flatten(FileTree).FirstOrDefault(n => n.Name.EndsWith(".cs")) ?? Flatten(FileTree).FirstOrDefault(n => n.Name == "ModInfo.xml");
            if (firstCode != null) SelectedFile = firstCode;
        });
    }

    private static IEnumerable<FileNode> Flatten(IEnumerable<FileNode> nodes) =>
        nodes.SelectMany(n => n.IsFolder ? Flatten(n.Children) : new[] { n });

    private void ReloadTree()
    {
        FileTree.Clear();
        if (Project is null) return;
        var root = new FileNode { Name = Project.ModFolderName, IsFolder = true };
        foreach (var rel in _projects.ListFiles(Project))
        {
            var parts = rel.Split('/');
            var cur = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var next = cur.Children.FirstOrDefault(c => c.IsFolder && c.Name == parts[i]);
                if (next is null)
                {
                    next = new FileNode { Name = parts[i], IsFolder = true, RelativePath = string.Join('/', parts.Take(i + 1)) };
                    cur.Children.Add(next);
                }
                cur = next;
            }
            cur.Children.Add(new FileNode { Name = parts[^1], RelativePath = rel });
        }
        Sort(root);
        foreach (var c in root.Children) FileTree.Add(c);
    }

    private static void Sort(FileNode n)
    {
        var sorted = n.Children.OrderBy(c => c.IsFolder ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        n.Children.Clear();
        foreach (var c in sorted) { Sort(c); n.Children.Add(c); }
    }

    private async Task ReloadHistoryAsync()
    {
        if (Project is null) return;
        Revisions.Clear();
        foreach (var r in await _projects.History.ListAsync(Project)) Revisions.Add(r);
    }

    // ---------------- Files / editor ----------------

    partial void OnSelectedFileChanged(FileNode? value)
    {
        if (value is null || value.IsFolder || Project is null) return;
        if (IsDirty && _openPath != null && !_dialogs.Confirm("Unsaved changes", $"Discard unsaved changes to {_openPath}?"))
            return;
        OpenFile(value.RelativePath);
    }

    private void OpenFile(string rel)
    {
        if (Project is null) return;
        var full = Path.Combine(Project.SourcePath, rel);
        if (!File.Exists(full)) { ErrorMessage = $"{rel} no longer exists."; return; }
        _openPath = rel;
        IsDecompiledView = false;
        if (rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || Core.IO.FileUtil.LooksBinary(full))
        {
            SetEditor("", null, readOnly: true, $"{rel} (binary)", "Binary file — use the DLL Inspector on the Analysis tab.");
            var dll = Analysis?.Dlls.FirstOrDefault(d => string.Equals(Path.Combine(Project.ModRootRelativePath, d.RelativePath).Replace('\\', '/').TrimStart('/'), rel, StringComparison.OrdinalIgnoreCase));
            if (dll != null) { SelectedDll = dll; SelectedTab = 3; }
            return;
        }
        var text = Core.IO.FileUtil.ReadTextLimited(full, 8 * 1024 * 1024, out var truncated);
        SetEditor(text, EditorHighlighting_.ForFile(rel), readOnly: truncated, rel,
            truncated ? "File too large to edit safely (showing first 8 MB, read-only)." : "Original source (editable; saving creates a revision)");
    }

    private void SetEditor(string text, IHighlightingDefinition? highlighting, bool readOnly, string title, string kind)
    {
        IsEditorReadOnly = true; // suppress dirty tracking while swapping text
        _savedText = text;
        EditorDocument.Text = text;
        EditorHighlighting = highlighting;
        IsEditorReadOnly = readOnly;
        EditorTitle = title;
        SourceKind = kind;
        IsDirty = false;
    }

    [RelayCommand]
    private Task SaveFile() => RunAsync("Saving...", async () =>
    {
        if (Project is null || _openPath is null || IsEditorReadOnly) return;
        var text = EditorDocument.Text;
        await _projects.SaveFileAsync(Project, _openPath, text, "Manual edit");
        _savedText = text;
        IsDirty = false;
        StatusMessage = $"Saved {_openPath} (revision recorded).";
        await ReloadHistoryAsync();
    });

    [RelayCommand]
    private Task NewFile() => RunAsync("Creating file...", async () =>
    {
        if (Project is null) return;
        var defaultPath = string.IsNullOrEmpty(Project.ModRootRelativePath) ? "Config/items.xml" : Project.ModRootRelativePath + "/Config/items.xml";
        var name = _dialogs.Prompt("New file", "Path of the new file relative to the project (e.g. Config/items.xml or Harmony/MyPatch.cs):", defaultPath);
        if (string.IsNullOrWhiteSpace(name)) return;
        var rel = name.Replace('\\', '/').Trim('/');
        if (Core.IO.PathSafety.ResolveUnderRoot(Project.SourcePath, rel) is null) { ErrorMessage = "That path is not allowed."; return; }
        if (File.Exists(Path.Combine(Project.SourcePath, rel))) { ErrorMessage = "That file already exists."; return; }
        var initial = rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && rel.StartsWith(Path.Combine(Project.ModRootRelativePath, "Config").Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase)
            ? "<configs>\n\n</configs>\n" : "";
        await _projects.SaveFileAsync(Project, rel, initial, "Created file");
        ReloadTree();
        await ReloadHistoryAsync();
        OpenFile(rel);
    });

    // ---------------- Analysis / DLL inspector ----------------

    [RelayCommand]
    private Task Analyze() => RunAsync("Analyzing...", AnalyzeCoreAsync);

    private async Task AnalyzeCoreAsync()
    {
        if (Project is null) return;
        var project = Project;
        var profile = Profile;
        var index = _profiles.GetIndex(profile);
        Analysis = await Task.Run(() => _analyzer.Analyze(project.ModRootPath, project.SourcePath, index, profile));
        HarmonyPatches.Clear();
        foreach (var h in Analysis.HarmonyPatches) HarmonyPatches.Add(h);
        var sb = new StringBuilder(Analysis.Summary());
        sb.AppendLine().AppendLine();
        if (Analysis.ModInfo is { } mi)
            sb.AppendLine($"ModInfo ({mi.Format}): Name={mi.Name}  DisplayName={mi.DisplayName}  Version={mi.Version}  Author={mi.Author}");
        else sb.AppendLine("ModInfo: " + (Analysis.ModInfoError ?? "missing"));
        if (Analysis.UnresolvedDependencies.Count > 0) sb.AppendLine("Unresolved dependencies: " + string.Join(", ", Analysis.UnresolvedDependencies));
        sb.AppendLine($"Files: {Analysis.TotalFiles}  Localization: {Analysis.LocalizationFiles.Count}  XUi: {Analysis.XuiFiles.Count}  Assets: {Analysis.AssetFiles.Count}");
        foreach (var f in Analysis.Findings) sb.AppendLine($"{f.Severity.ToString().ToUpperInvariant()}: {f.Message} {f.FilePath}");
        AnalysisSummary = sb.ToString();
        project.ModType = Analysis.ModType;
        project.SideRequirement = Analysis.Side.Result;
        project.EacCompatibility = Analysis.Eac.Result;
        OnPropertyChanged(nameof(Project));
        if (SelectedDll is null) SelectedDll = Analysis.Dlls.FirstOrDefault(d => d.Assembly.Success);
    }

    partial void OnSelectedDllChanged(DllReport? value) => SelectedType = value?.Assembly.Types.FirstOrDefault();

    [RelayCommand]
    private Task DecompileType() => RunAsync("Decompiling...", async () =>
    {
        if (SelectedDll is null || SelectedType is null || Project is null) return;
        var path = Path.Combine(Project.ModRootPath, SelectedDll.RelativePath);
        var type = SelectedType.FullName;
        var dirs = Profile?.ManagedPath is { } m ? new[] { m } : null;
        var r = await Task.Run(() => _decompiler.DecompileType(path, type, dirs));
        ShowDecompiled(r, $"{SelectedDll.RelativePath} → {type}");
    });

    [RelayCommand]
    private Task DecompileAssembly() => RunAsync("Decompiling assembly...", async () =>
    {
        if (SelectedDll is null || Project is null) return;
        var path = Path.Combine(Project.ModRootPath, SelectedDll.RelativePath);
        var dirs = Profile?.ManagedPath is { } m ? new[] { m } : null;
        var r = await Task.Run(() => _decompiler.DecompileAssembly(path, dirs));
        ShowDecompiled(r, SelectedDll.RelativePath);
    });

    private void ShowDecompiled(DecompiledSource r, string title)
    {
        if (!r.Success) { ErrorMessage = r.Error; return; }
        if (IsDirty && !_dialogs.Confirm("Unsaved changes", "Discard unsaved editor changes to show decompiled code?")) return;
        _openPath = null;
        SetEditor(r.Text, EditorHighlighting_.ForFile("x.cs"), readOnly: true, title, "DECOMPILED / RECONSTRUCTED SOURCE — not the original source code (read-only)");
        IsDecompiledView = true;
        SelectedTab = 1;
    }

    [RelayCommand]
    private Task CompareWithZip() => Compare(false);

    [RelayCommand]
    private Task CompareWithFolder() => Compare(true);

    private Task Compare(bool folder) => RunAsync("Comparing versions...", async () =>
    {
        if (Project is null) return;
        var other = folder ? _dialogs.PickFolder("Select the other version (mod folder)") : _dialogs.PickFile("Select the other version (ZIP)", ImportWorkflow.ZipFilter);
        if (other is null) return;
        var project = Project;
        CompareText = await Task.Run(() =>
        {
            var root = other;
            if (!folder)
            {
                root = Path.Combine(Path.GetTempPath(), "mms-compare-" + Guid.NewGuid().ToString("N"));
                Core.IO.SafeZip.Extract(other, root);
            }
            var mi = ModInfoFile.FindAll(root).FirstOrDefault();
            var otherRoot = mi is null ? root : Path.GetDirectoryName(mi)!;
            var cmp = _comparer.Compare(otherRoot, project.ModRootPath);
            return $"Comparing {other}\n  (old) → this project (new)\n\n" + cmp.Summarize(200_000);
        });
    });

    // ---------------- Overview / settings ----------------

    [RelayCommand]
    private Task SaveProjectSettings() => RunAsync("Saving...", async () =>
    {
        if (Project is null) return;
        Project.GameProfileId = SelectedProfileId;
        Profile = Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
        await _projects.SaveAsync(Project);
        StatusMessage = "Project settings saved.";
        await AnalyzeCoreAsync();
    });

    [RelayCommand]
    private void IncrementVersion()
    {
        var next = ModInfoFile.IncrementVersion(VersionText);
        if (next is null) ErrorMessage = $"'{VersionText}' has no numeric part to increment; type the new version.";
        else VersionText = next;
    }

    [RelayCommand]
    private Task ApplyVersion() => RunAsync("Setting version...", async () =>
    {
        if (Project is null || string.IsNullOrWhiteSpace(VersionText)) return;
        await _projects.SetVersionAsync(Project, VersionText.Trim());
        OnPropertyChanged(nameof(Project));
        StatusMessage = $"Version set to {Project.Version} (ModInfo.xml updated, revision recorded).";
        await ReloadHistoryAsync();
        if (_openPath?.EndsWith("ModInfo.xml", StringComparison.OrdinalIgnoreCase) == true) OpenFile(_openPath);
    });

    // ---------------- Build pipeline ----------------

    [RelayCommand]
    private Task BuildPackage() => Build(packageWithErrors: false, package: true);

    [RelayCommand]
    private Task PackageWithErrors()
    {
        if (!_dialogs.Confirm("Package With Errors", "Create a package even though validation or compilation reported errors?\n\nThe ZIP will be named *_WITH-ERRORS and will not be marked as a clean build.")) return Task.CompletedTask;
        return Build(packageWithErrors: true, package: true);
    }

    [RelayCommand]
    private Task CompileAndValidate() => Build(packageWithErrors: false, package: false);

    private Task Build(bool packageWithErrors, bool package) => RunAsync("Building...", async () =>
    {
        if (Project is null) return;
        if (IsDirty && _dialogs.Confirm("Unsaved changes", "Save the open file before building?")) await SaveFileCoreAsync();
        foreach (var s in Stages) { s.Status = StageStatus.Pending; s.Message = ""; }
        PipelineEvents.Clear();
        Diagnostics.Clear();
        Findings.Clear();
        var progress = new Progress<PipelineEvent>(e =>
        {
            var st = Stages.First(s => s.Stage == e.Stage);
            st.Status = e.Status;
            st.Message = e.Message;
            PipelineEvents.Add($"{e.TimestampUtc.LocalDateTime:HH:mm:ss}  {e.Stage,-16} {e.Status,-9} {e.Message}");
        });
        var project = Project;
        var options = new BuildOptions
        {
            Configuration = Configuration,
            NewVersion = VersionText != project.Version && !string.IsNullOrWhiteSpace(VersionText) ? VersionText.Trim() : null,
            PackageWithErrors = packageWithErrors,
            Package = package,
        };
        var result = await Task.Run(() => _pipeline.RunAsync(project, options, progress));
        foreach (var d in result.AllDiagnostics.Where(d => d.Severity >= Severity.Warning)) Diagnostics.Add(d.ToModDiagnostic());
        foreach (var f in result.Validation?.Findings ?? Array.Empty<ValidationFinding>()) Findings.Add(f);
        foreach (var f in result.PackageFindings.Where(f => f.Severity != Severity.Pass)) Findings.Add(f);
        foreach (var f in result.Validation?.Findings.Where(f => f.Severity >= Severity.Warning) ?? Enumerable.Empty<ValidationFinding>()) Diagnostics.Add(f.ToDiagnostic());
        CompilerLog = string.Join("\n\n", result.Compiles.Select(c => c.Result.Log));
        LastPackagePath = result.Package?.Success == true ? result.Package.ZipPath : LastPackagePath;
        BuildSummary = result.Summary;
        VersionText = project.Version;
        OnPropertyChanged(nameof(Project));
        if (result.Analysis != null) { Analysis = result.Analysis; }
        await ReloadHistoryAsync();
        ReloadTree();
    });

    private async Task SaveFileCoreAsync()
    {
        if (Project is null || _openPath is null || IsEditorReadOnly) return;
        await _projects.SaveFileAsync(Project, _openPath, EditorDocument.Text, "Manual edit (saved before build)");
        _savedText = EditorDocument.Text;
        IsDirty = false;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (LastPackagePath != null) _dialogs.OpenFolder(LastPackagePath);
        else if (Project != null) _dialogs.OpenFolder(Project.Workspace.Output);
    }

    [RelayCommand]
    private void OpenDiagnostic(ModDiagnostic? d)
    {
        if (d?.FilePath is null || Project is null) return;
        var candidates = new[] { d.FilePath, Path.Combine(Project.ModRootRelativePath, d.FilePath).Replace('\\', '/').TrimStart('/') };
        var rel = candidates.FirstOrDefault(c => File.Exists(Path.Combine(Project.SourcePath, c)));
        if (rel is null) return;
        SelectedTab = 1;
        OpenFile(rel);
        if (d.Line is { } line) GoToLineRequested?.Invoke(line);
    }

    // ---------------- History ----------------

    [RelayCommand]
    private Task Restore(RevisionRecord? rev) => RunAsync("Restoring revision...", async () =>
    {
        if (rev is null || Project is null) return;
        if (!_dialogs.Confirm("Restore This Revision", $"Restore the project to '{rev.Action}' ({rev.TimestampUtc.LocalDateTime:g})?\n\nThe current state is saved as a revision first, so this can be undone.")) return;
        await _projects.History.RestoreAsync(Project, rev);
        StatusMessage = "Revision restored.";
        ReloadTree();
        await ReloadHistoryAsync();
        if (_openPath != null) OpenFile(_openPath);
        await AnalyzeCoreAsync();
    });

    // ---------------- Logs / AI ----------------

    [RelayCommand]
    private Task AttachLog() => RunAsync("Attaching log...", async () =>
    {
        if (Project is null) return;
        foreach (var f in _dialogs.PickFiles("Attach client or server log", "Logs (*.txt;*.log)|*.txt;*.log|All files|*.*"))
            AttachedLogs.Add(await _projects.AttachFileAsync(Project, f, "logs"));
    });

    [RelayCommand]
    private Task DiagnoseLogs() => RunAsync("Diagnosing...", async () =>
    {
        if (Project is null) return;
        var inputs = new RepairInputs();
        foreach (var l in AttachedLogs) inputs.Logs.Add((l, l.Contains("server", StringComparison.OrdinalIgnoreCase) ? "server" : "client"));
        var project = Project;
        var d = await Task.Run(() => _repair.DiagnoseAsync(project, inputs));
        _lastDiagnosis = d;
        AIEvents.Clear();
        foreach (var line in d.Report.Split('\n')) AIEvents.Add(line.TrimEnd('\r'));
    });

    private Diagnosis? _lastDiagnosis;

    [RelayCommand]
    private Task AIRepair() => RunAsync("AI repair in progress...", async () =>
    {
        if (Project is null) return;
        if (IsDirty && _dialogs.Confirm("Unsaved changes", "Save the open file before AI repair?")) await SaveFileCoreAsync();
        _cts = new CancellationTokenSource();
        AIEvents.Clear();
        var progress = new Progress<AIEvent>(e => { if (e.Kind != AIEventKind.Thinking) AIEvents.Add($"{e.TimestampUtc.LocalDateTime:HH:mm:ss} [{e.Kind}] {e.Message}"); });
        var project = Project;
        var options = new RepairRequestOptions { MaxAttempts = _aiOptions.MaxAutoRepairAttempts, UserInstructions = AiInstructions, Diagnosis = _lastDiagnosis };
        var outcome = await Task.Run(() => _ai.RepairAsync(project, options, progress, _cts.Token));
        StatusMessage = outcome.StopReason;
        foreach (var a in outcome.Attempts)
            AIEvents.Add($"Attempt {a.Number}: {a.Summary}\n  files: {string.Join(", ", a.ChangedFiles)} → compile {(a.CompileSucceededAfter ? "OK" : "failed")}, {a.ValidationErrorsAfter} validation error(s)");
        if (outcome.FinalBuild != null)
        {
            Diagnostics.Clear();
            foreach (var d in outcome.FinalBuild.AllDiagnostics.Where(d => d.Severity >= Severity.Warning)) Diagnostics.Add(d.ToModDiagnostic());
        }
        ReloadTree();
        await ReloadHistoryAsync();
        if (_openPath != null) OpenFile(_openPath);
    });

    [RelayCommand]
    private void CancelOperation() => _cts?.Cancel();
}

/// <summary>Alias to avoid clashing with the EditorHighlighting property name.</summary>
internal static class EditorHighlighting_
{
    public static IHighlightingDefinition? ForFile(string path) => Services.EditorHighlighting.ForFile(path);
}
