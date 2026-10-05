using MadModStudio.App.Services;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Projects;

namespace MadModStudio.App.ViewModels;

/// <summary>Shared "Import Mod" interaction used by several pages.</summary>
public sealed class ImportWorkflow
{
    public const string ZipFilter = "Mod archives (*.zip)|*.zip";
    private readonly IDialogService _dialogs;
    private readonly ProjectService _projects;
    private readonly AppState _state;

    public ImportWorkflow(IDialogService dialogs, ProjectService projects, AppState state)
    {
        _dialogs = dialogs;
        _projects = projects;
        _state = state;
    }

    public string? PickSource(bool folder) => folder
        ? _dialogs.PickFolder("Select the mod folder to import")
        : _dialogs.PickFile("Select a mod ZIP to import", ZipFilter);

    /// <summary>Imports and returns the created projects; shows warnings. The original is never modified.</summary>
    public async Task<IReadOnlyList<ImportedMod>> ImportAsync(string path, ProjectOrigin origin = ProjectOrigin.Imported)
    {
        IReadOnlyList<ImportedMod> result;
        try
        {
            result = await Task.Run(() => _projects.ImportAsync(path, _state.CurrentProfile?.Id, origin));
        }
        catch (ImportException ex)
        {
            _dialogs.Error("Import failed", ex.Message);
            return Array.Empty<ImportedMod>();
        }
        var warnings = result.SelectMany(r => r.Warnings).Distinct().ToList();
        if (warnings.Count > 0) _dialogs.Info("Imported with notes", string.Join("\n", warnings));
        return result;
    }
}
