using System.Diagnostics;
using System.Security.Cryptography;
using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Mods;
using MadModStudio.Game7DTD.Scanner;
using Microsoft.Extensions.Logging;

namespace MadModStudio.Game7DTD.Deploy;

public sealed class DeployException : Exception
{
    public DeployException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DeployResult
{
    public DeploymentRecord? Record { get; set; }
    public List<string> Warnings { get; } = new();
    public string Message { get; set; } = "";
}

public sealed class UndoResult
{
    public bool Undone { get; init; }
    /// <summary>Files that were changed or added in the game folder after the deployment (undo refuses unless forced).</summary>
    public IReadOnlyList<string> ModifiedFiles { get; init; } = Array.Empty<string>();
    public string Message { get; init; } = "";
}

/// <summary>
/// "Deploy to Game": copies a built package into the game's Mods folder only when the user asks. Whatever was in the
/// target folder is backed up first and every deployment can be undone. Nothing outside the profile's Mods folder is
/// ever written, and nothing is deployed while the game or server is running.
/// </summary>
public sealed class ModDeployService
{
    private readonly IDeploymentRepository _deployments;
    private readonly IBuildRecordRepository _builds;
    private readonly AppPaths _paths;
    private readonly ILogger<ModDeployService> _log;

    public ModDeployService(IDeploymentRepository deployments, IBuildRecordRepository builds, AppPaths paths, ILogger<ModDeployService> log)
    {
        _deployments = deployments;
        _builds = builds;
        _paths = paths;
        _log = log;
    }

    /// <summary>Returns names of running game/server processes. Replaceable for tests.</summary>
    public Func<IReadOnlyList<string>> RunningGameProcesses { get; set; } = DetectRunningGame;

    public Task<IReadOnlyList<DeploymentRecord>> ListAsync(ModProject project, CancellationToken ct = default) => _deployments.ListAsync(project.Id, ct);

    /// <summary>The newest clean package (build succeeded, not packaged with errors, file still present), or null.</summary>
    public async Task<BuildRecord?> LatestDeployableBuildAsync(ModProject project, CancellationToken ct = default) =>
        (await _builds.ListAsync(project.Id, ct).ConfigureAwait(false))
            .FirstOrDefault(b => b.Succeeded && !b.PackagedWithErrors && b.PackagePath != null && File.Exists(b.PackagePath));

    public async Task<DeployResult> DeployAsync(ModProject project, GameProfile profile, string? packagePath = null, CancellationToken ct = default)
    {
        var modsPath = ModsFolder(profile);
        var running = RunningGameProcesses();
        if (running.Count > 0)
            throw new DeployException($"The game appears to be running ({string.Join(", ", running)}). Close it before deploying: files may be locked and the game only loads mods at start-up.");

        string version;
        if (packagePath is null)
        {
            var build = await LatestDeployableBuildAsync(project, ct).ConfigureAwait(false)
                ?? throw new DeployException("No clean package to deploy. Build the project first: only builds that compiled, validated and packaged without errors are deployed.");
            packagePath = build.PackagePath!;
            version = build.Version;
        }
        else
        {
            if (!File.Exists(packagePath)) throw new DeployException($"Package not found: {packagePath}");
            version = project.Version;
        }

        var result = new DeployResult();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var work = Path.Combine(_paths.Deployments, project.Id.ToString("N"), stamp);
        var staging = Path.Combine(work, "staging");
        try
        {
            // Treat the package like any archive: safe extraction, then require exactly one mod folder with ModInfo.xml.
            SafeZip.Extract(packagePath, staging);
            var roots = Directory.GetDirectories(staging);
            if (roots.Length != 1 || Directory.GetFiles(staging).Length > 0)
                throw new DeployException("The package must contain exactly one top-level mod folder.");
            var modSource = roots[0];
            var folderName = Path.GetFileName(modSource);
            var info = ModInfoFile.TryParse(Path.Combine(modSource, ModInfoFile.FileName), out var infoError)
                ?? throw new DeployException($"The package's {ModInfoFile.FileName} is missing or invalid: {infoError}");
            if (info.Version != null) version = info.Version;

            var target = InsideModsFolder(modsPath, folderName);
            foreach (var other in OtherFoldersWithSameModName(modsPath, folderName, info.Name))
                result.Warnings.Add($"Another folder in Mods declares the same mod name '{info.Name}': {Path.GetFileName(other)}. The game may load both.");

            string? backup = null;
            if (Directory.Exists(target))
            {
                backup = Path.Combine(work, "backup", folderName);
                CopyDirectory(target, backup);
            }

            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                CopyDirectory(modSource, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Put the game folder back the way it was.
                TryDelete(target);
                if (backup != null) CopyDirectory(backup, target);
                throw new DeployException($"Writing to the game's Mods folder failed; the previous state was restored. {ex.Message}", ex);
            }

            result.Record = await _deployments.AddAsync(new DeploymentRecord
            {
                ProjectId = project.Id,
                GameProfileId = profile.Id,
                Version = version,
                PackagePath = packagePath,
                TargetPath = target,
                BackupPath = backup,
                Manifest = HashTree(target),
            }, ct).ConfigureAwait(false);
            _log.LogInformation("Deployed {Project} {Version} to {Target} (backup: {Backup})", project.Name, version, target, backup ?? "none");
            result.Warnings.AddRange(ConflictsWithInstalledMods(modsPath, folderName, profile));
            result.Message = backup is null
                ? $"Deployed {folderName} {version} to {target}."
                : $"Deployed {folderName} {version} to {target}. The previous folder was backed up and can be restored with Undo.";
            return result;
        }
        catch (ZipSafetyException ex)
        {
            throw new DeployException($"The package could not be extracted safely: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Removes the deployed folder and restores what was there before. Refuses when files were changed since, unless forced.</summary>
    public async Task<UndoResult> UndoAsync(DeploymentRecord record, GameProfile profile, bool force = false, CancellationToken ct = default)
    {
        if (record.IsUndone) return new UndoResult { Message = "This deployment was already undone." };
        // Any project may have deployed into the same folder since (e.g. a batch safe fix of the same mod).
        var newer = (await _deployments.ListAllAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(d => d.Id > record.Id && !d.IsUndone && string.Equals(d.TargetPath, record.TargetPath, StringComparison.OrdinalIgnoreCase));
        if (newer != null)
            return new UndoResult { Message = $"A newer deployment ({newer.Version}, {newer.DeployedUtc.LocalDateTime:g}) replaced this one. Undo that one first." };
        var running = RunningGameProcesses();
        if (running.Count > 0)
            throw new DeployException($"The game appears to be running ({string.Join(", ", running)}). Close it before undoing a deployment.");

        // Re-check containment: never delete anything outside the profile's Mods folder.
        var target = InsideModsFolder(ModsFolder(profile), Path.GetFileName(record.TargetPath));
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(record.TargetPath), StringComparison.OrdinalIgnoreCase))
            throw new DeployException("The recorded target is not inside this profile's Mods folder; nothing was changed.");

        var modified = Directory.Exists(target) ? ModifiedSince(record.Manifest, HashTree(target)) : Array.Empty<string>();
        if (modified.Count > 0 && !force)
            return new UndoResult { ModifiedFiles = modified, Message = $"{modified.Count} file(s) in {target} changed after the deployment. Undo again with force to discard them." };

        if (record.BackupPath != null && !Directory.Exists(record.BackupPath))
            throw new DeployException($"The backup for this deployment is missing ({record.BackupPath}); nothing was changed.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        if (record.BackupPath != null) CopyDirectory(record.BackupPath, target);
        record.UndoneUtc = DateTimeOffset.UtcNow;
        await _deployments.UpdateAsync(record, ct).ConfigureAwait(false);
        _log.LogInformation("Undid deployment {Id} at {Target}", record.Id, target);
        return new UndoResult
        {
            Undone = true,
            ModifiedFiles = modified,
            Message = record.BackupPath != null ? $"Restored the previous {Path.GetFileName(target)} folder." : $"Removed {target} (nothing was there before the deployment).",
        };
    }

    /// <summary>XML and localization clashes between the deployed mod and the other mods in the Mods folder.</summary>
    private IEnumerable<string> ConflictsWithInstalledMods(string modsPath, string folderName, GameProfile profile)
    {
        try
        {
            var inputs = Directory.GetDirectories(modsPath).Select(d => new ConflictInput(d, Path.GetFileName(d), null)).ToList();
            return ModConflictAnalyzer.Analyze(inputs, profile.ConfigPath)
                .Where(c => c.Severity != Severity.Info && c.Mods.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                .Select(c => $"Conflict: {c.Summary}. {c.Detail}")
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Conflict check after deployment failed");
            return Array.Empty<string>();
        }
    }

    private static string ModsFolder(GameProfile profile)
    {
        var mods = profile.ModsPath;
        if (string.IsNullOrWhiteSpace(mods))
        {
            if (string.IsNullOrWhiteSpace(profile.InstallPath) || !Directory.Exists(profile.InstallPath))
                throw new DeployException("The game profile has no valid install path.");
            mods = Path.Combine(profile.InstallPath, "Mods");
        }
        Directory.CreateDirectory(mods);
        return Path.GetFullPath(mods);
    }

    /// <summary>Resolves a mod folder name inside the Mods folder, rejecting anything that could escape it.</summary>
    public static string InsideModsFolder(string modsPath, string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName) || folderName is "." or ".." || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || folderName.Contains('/') || folderName.Contains('\\'))
            throw new DeployException($"'{folderName}' is not a valid mod folder name.");
        var root = Path.GetFullPath(modsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, folderName));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new DeployException($"'{folderName}' would be outside the Mods folder.");
        return target;
    }

    private static IEnumerable<string> OtherFoldersWithSameModName(string modsPath, string folderName, string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName)) yield break;
        foreach (var dir in Directory.GetDirectories(modsPath))
        {
            if (string.Equals(Path.GetFileName(dir), folderName, StringComparison.OrdinalIgnoreCase)) continue;
            var other = ModInfoFile.TryParse(Path.Combine(dir, ModInfoFile.FileName), out _);
            if (other != null && string.Equals(other.Name, modName, StringComparison.OrdinalIgnoreCase)) yield return dir;
        }
    }

    internal static Dictionary<string, string> HashTree(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var s = File.OpenRead(file);
            map[Path.GetRelativePath(root, file).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(s));
        }
        return map;
    }

    private static IReadOnlyList<string> ModifiedSince(Dictionary<string, string> deployed, Dictionary<string, string> now)
    {
        var changed = new List<string>();
        foreach (var (path, hash) in now)
            if (!deployed.TryGetValue(path, out var original) || original != hash) changed.Add(path);
        changed.AddRange(deployed.Keys.Where(k => !now.ContainsKey(k)).Select(k => k + " (deleted)"));
        return changed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static IReadOnlyList<string> DetectRunningGame()
    {
        try
        {
            return Process.GetProcesses()
                .Select(p => { try { return p.ProcessName; } catch (InvalidOperationException) { return ""; } finally { p.Dispose(); } })
                .Where(n => n.StartsWith("7DaysToDie", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return Array.Empty<string>();
        }
    }
}
