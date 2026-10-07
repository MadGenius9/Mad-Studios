using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Validation;

namespace MadModStudio.Game7DTD.Scanner;

public enum ScanStatus { Compatible, Warning, Broken, Unknown, ClientRequirementDetected }

public sealed class ScannedMod
{
    public string FolderPath { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Version { get; init; }
    public ModType Type { get; init; }
    public int XmlCount { get; init; }
    public int DllCount { get; init; }
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public ScanStatus Status { get; init; }
    public ModAnalysisReport? Analysis { get; init; }
    public ValidationReport? Validation { get; init; }
    public string? Error { get; init; }
}

/// <summary>Read-only analysis of every mod in a Mods folder. Nothing in the folder is modified.</summary>
public sealed class BatchModScanner
{
    private readonly ModAnalyzer _analyzer;
    private readonly IEnumerable<IModValidator> _validators;
    private readonly GameProfileService _profiles;

    public BatchModScanner(ModAnalyzer analyzer, IEnumerable<IModValidator> validators, GameProfileService profiles)
    {
        _analyzer = analyzer;
        _validators = validators;
        _profiles = profiles;
    }

    public async Task<IReadOnlyList<ScannedMod>> ScanAsync(string modsFolder, GameProfile? profile, IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(modsFolder)) throw new DirectoryNotFoundException($"Mods folder not found: {modsFolder}");
        var index = _profiles.GetIndex(profile);
        var roots = Directory.GetDirectories(modsFolder)
            .Select(d => File.Exists(Path.Combine(d, ModInfoFile.FileName)) ? d : ModInfoFile.FindAll(d, 2).Select(Path.GetDirectoryName).FirstOrDefault() ?? d)
            .Distinct().OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
        var results = new List<ScannedMod>();
        // Exclude validators that only make sense in a build.
        var validators = _validators.Where(v => v is not CompilerResultValidator and not PackageStructureValidator and not ServerSideValidator).ToList();
        for (var i = 0; i < roots.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var root = roots[i];
            progress?.Report((i, roots.Count, Path.GetFileName(root)));
            try
            {
                var analysis = await Task.Run(() => _analyzer.Analyze(root, root, index, profile), ct).ConfigureAwait(false);
                var ctx = new ValidationContext { ModRootPath = root, GameProfile = profile, GameIndex = index };
                ctx.Items[HarmonyTargetValidator.SourcePatchesKey] = analysis.HarmonyPatches.Where(p => p.Origin == "Source").ToList();
                var validation = await new ValidationRunner(validators).RunAsync(ctx, ct).ConfigureAwait(false);
                var status = validation.HasErrors ? ScanStatus.Broken
                    : analysis.Side.Result == SideRequirement.ClientRequired ? ScanStatus.ClientRequirementDetected
                    : validation.WarningCount > 0 ? ScanStatus.Warning
                    : index is null && analysis.Dlls.Count > 0 ? ScanStatus.Unknown
                    : ScanStatus.Compatible;
                results.Add(new ScannedMod
                {
                    FolderPath = root,
                    Name = analysis.ModInfo?.EffectiveName ?? Path.GetFileName(root),
                    Version = analysis.ModInfo?.Version,
                    Type = analysis.ModType,
                    XmlCount = analysis.XmlFiles.Count,
                    DllCount = analysis.Dlls.Count,
                    Errors = validation.ErrorCount,
                    Warnings = validation.WarningCount,
                    Status = status,
                    Analysis = analysis,
                    Validation = validation,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new ScannedMod { FolderPath = root, Name = Path.GetFileName(root), Status = ScanStatus.Unknown, Error = ex.Message });
            }
        }
        progress?.Report((roots.Count, roots.Count, ""));
        return results;
    }

    /// <summary>Conflicts between the scanned mods (XML patches checked against the profile's game config).</summary>
    public static IReadOnlyList<ModConflict> FindConflicts(IReadOnlyList<ScannedMod> mods, GameProfile? profile, CancellationToken ct = default) =>
        ModConflictAnalyzer.Analyze(mods.Select(m => new ConflictInput(m.FolderPath, m.Name, m.Analysis)).ToList(), profile?.ConfigPath, ct);
}
