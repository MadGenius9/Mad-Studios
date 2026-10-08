using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.Game7DTD.Install;

public sealed class ProfileCreationResult
{
    public GameProfile? Profile { get; init; }
    public InstallValidation Validation { get; init; } = new();
    public GameVersionInfo? Version { get; init; }
    public RuntimeProfile? Runtime { get; init; }
}

/// <summary>Creates, refreshes and indexes 7 Days to Die game profiles.</summary>
public sealed class GameProfileService
{
    private readonly IGameProfileRepository _profiles;
    private readonly GameInstallLocator _locator;
    private readonly GameVersionDetector _versionDetector;
    private readonly RuntimeProfileDetector _runtimeDetector;
    private readonly GameIndexBuilder _indexBuilder;
    private readonly AppPaths _paths;
    private readonly ILogger<GameProfileService> _log;
    private readonly Dictionary<Guid, SqliteGameKnowledgeIndex> _indexCache = new();
    private readonly IEnumerable<Core.Knowledge.IGameUpdateListener> _updateListeners;

    public GameProfileService(IGameProfileRepository profiles, GameInstallLocator locator, GameVersionDetector versionDetector,
        RuntimeProfileDetector runtimeDetector, GameIndexBuilder indexBuilder, AppPaths paths, ILogger<GameProfileService>? log = null,
        IEnumerable<Core.Knowledge.IGameUpdateListener>? updateListeners = null)
    {
        _updateListeners = updateListeners ?? Array.Empty<Core.Knowledge.IGameUpdateListener>();
        _profiles = profiles;
        _locator = locator;
        _versionDetector = versionDetector;
        _runtimeDetector = runtimeDetector;
        _indexBuilder = indexBuilder;
        _paths = paths;
        _log = log ?? NullLogger<GameProfileService>.Instance;
    }

    public InstallValidation ValidatePath(string path) => _locator.Validate(path);

    /// <summary>Validates the folder, detects version/runtime and saves a new profile. Does not index (call <see cref="ReindexAsync"/>).</summary>
    public async Task<ProfileCreationResult> CreateProfileAsync(string installPath, string? name = null, CancellationToken ct = default)
    {
        var validation = _locator.Validate(installPath);
        if (!validation.IsValid) return new ProfileCreationResult { Validation = validation };
        var install = validation.Install!;

        var version = await Task.Run(() => _versionDetector.Detect(install), ct).ConfigureAwait(false);
        var runtime = _runtimeDetector.Detect(install.ManagedPath!);
        var profile = new GameProfile
        {
            Name = name ?? $"7 Days to Die{(install.IsDedicatedServer ? " Dedicated Server" : "")}{(version.Version != null ? " " + version.Version : "")}",
            GameVersion = version.Version,
            GameVersionSource = version.Source,
            UnityVersion = version.UnityVersion,
            InstallPath = install.InstallPath,
            ExecutablePath = install.ExecutablePath,
            ManagedPath = install.ManagedPath,
            ConfigPath = install.ConfigPath,
            ModsPath = install.ModsPath,
            HarmonyAssemblyPath = install.HarmonyAssemblyPath,
            IsDedicatedServer = install.IsDedicatedServer,
            Compilation = RuntimeProfileDetector.DefaultSettings(runtime),
            IndexStatus = IndexStatus.NotIndexed,
        };
        profile.IndexPath = _paths.GameIndexPath(profile.Id);
        await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        _log.LogInformation("Created game profile {Name} at {Path}", profile.Name, profile.InstallPath);
        return new ProfileCreationResult { Profile = profile, Validation = validation, Version = version, Runtime = runtime };
    }

    // A full game install is far larger than the defaults meant for mod archives.
    private static readonly ZipExtractionLimits GameZipLimits = new()
    {
        MaxTotalUncompressedBytes = 64L * 1024 * 1024 * 1024,
        MaxEntryUncompressedBytes = 16L * 1024 * 1024 * 1024,
        MaxEntries = 500_000,
    };

    /// <summary>
    /// Extracts a ZIP of the game (or a dedicated server) into Mad Mod Studio's own data folder, then creates a profile from it.
    /// The extracted copy is owned by the profile and removed with it. On any failure nothing is left behind.
    /// </summary>
    public Task<ProfileCreationResult> CreateProfileFromZipAsync(string zipPath, string? name = null, CancellationToken ct = default) =>
        CreateProfileFromZipsAsync(new[] { zipPath }, name, ct);

    /// <summary>
    /// Like <see cref="CreateProfileFromZipAsync(string,string?,CancellationToken)"/> but takes several ZIPs that together
    /// make up the install, e.g. one with the game's DLLs (Managed.zip) and one with its config XML files (Config.zip).
    /// </summary>
    public async Task<ProfileCreationResult> CreateProfileFromZipsAsync(IReadOnlyList<string> zipPaths, string? name = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(_paths.GameInstalls, Guid.NewGuid().ToString("N"));
        var label = string.Join(", ", zipPaths.Select(Path.GetFileName));
        try
        {
            var files = 0;
            foreach (var zip in zipPaths)
                files += (await Task.Run(() => SafeZip.Extract(zip, dest, GameZipLimits), ct).ConfigureAwait(false)).FilesExtracted;
            // Standard layout first; otherwise accept "DLLs + config files" laid out any which way.
            var root = _locator.FindInstallRootBelow(dest) ?? NormalizeLooseLayout(dest);
            if (root is null)
            {
                var none = new InstallValidation();
                none.Errors.Add($"No 7 Days to Die installation was found in {label} ({files:N0} files extracted): there is no Assembly-CSharp.dll. " +
                                "A profile needs the game's DLLs (the Managed folder) and its config XML files (blocks.xml, items.xml, ...). " +
                                "If they are in separate ZIPs, select all of them together (Ctrl+click).");
                DeleteExtracted(dest);
                return new ProfileCreationResult { Validation = none };
            }
            var result = await CreateProfileAsync(root, name, ct).ConfigureAwait(false);
            if (result.Profile is null) DeleteExtracted(dest);
            else _log.LogInformation("Extracted {Zips} ({Files} files) to {Dest}", label, files, dest);
            return result;
        }
        catch (ZipSafetyException ex)
        {
            DeleteExtracted(dest);
            var v = new InstallValidation();
            v.Errors.Add(ex.Message);
            return new ProfileCreationResult { Validation = v };
        }
        catch
        {
            DeleteExtracted(dest);
            throw;
        }
    }

    private static readonly string[] DllExts = { ".dll", ".pdb", ".mdb" };
    private static readonly string[] ConfigExts = { ".xml", ".txt" };

    /// <summary>
    /// For ZIPs holding only the game's DLLs and config files (no 7DaysToDie_Data/Data folders): finds the folder with
    /// Assembly-CSharp.dll and the folder with blocks.xml and rebuilds the layout the rest of the app expects
    /// (7DaysToDie_Data/Managed and Data/Config) in a subfolder of <paramref name="dest"/>. Returns that root, or null.
    /// </summary>
    private static string? NormalizeLooseLayout(string dest)
    {
        static int Depth(string p) => p.Count(c => c == Path.DirectorySeparatorChar);
        var asm = Directory.EnumerateFiles(dest, "Assembly-CSharp.dll", SearchOption.AllDirectories).OrderBy(Depth).FirstOrDefault();
        if (asm is null) return null;
        var managedSrc = Path.GetDirectoryName(asm)!;
        var configSrc = Directory.EnumerateFiles(dest, "blocks.xml", SearchOption.AllDirectories)
            .Select(f => Path.GetDirectoryName(f)!)
            .OrderBy(d => File.Exists(Path.Combine(d, "items.xml")) ? 0 : 1).ThenBy(Depth)
            .FirstOrDefault();

        var root = Path.Combine(dest, "_layout");
        var managedDst = Path.Combine(root, "7DaysToDie_Data", "Managed");
        var configDst = Path.Combine(root, "Data", "Config");
        MoveContents(managedSrc, managedDst, DllExts, root, dest);
        if (configSrc != null) MoveContents(configSrc, configDst, ConfigExts, root, dest);
        return root;
    }

    /// <summary>
    /// Moves <paramref name="src"/>'s contents into <paramref name="dst"/>. When src is the extraction root itself only
    /// files with the given extensions move (it may hold unrelated files); otherwise everything does, including subfolders.
    /// </summary>
    private static void MoveContents(string src, string dst, string[] looseExts, string skipRoot, string extractionRoot)
    {
        Directory.CreateDirectory(dst);
        var isTop = string.Equals(Path.GetFullPath(src), Path.GetFullPath(extractionRoot), StringComparison.OrdinalIgnoreCase);
        foreach (var entry in Directory.GetFileSystemEntries(src))
        {
            if (string.Equals(Path.GetFullPath(entry), Path.GetFullPath(skipRoot), StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dst, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (!isTop) Directory.Move(entry, target);
            }
            else if (!isTop || looseExts.Contains(Path.GetExtension(entry), StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(target)) File.Move(entry, target);
            }
        }
    }

    /// <summary>True when the profile's install is a copy this app extracted from a ZIP (so it is ours to delete).</summary>
    public bool IsManagedCopy(GameProfile profile) => ManagedCopyRoot(profile.InstallPath) != null;

    private string? ManagedCopyRoot(string? installPath)
    {
        if (string.IsNullOrEmpty(installPath)) return null;
        var baseDir = Path.GetFullPath(_paths.GameInstalls).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(installPath);
        if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return null;
        var first = full[baseDir.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null ? null : Path.Combine(baseDir, first);
    }

    private void DeleteExtracted(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not remove extracted game files at {Dir}", dir);
        }
    }

    public async Task<IndexBuildResult> ReindexAsync(GameProfile profile, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        // Re-validate first: the install may have moved or been updated.
        var validation = _locator.Validate(profile.InstallPath);
        if (!validation.IsValid)
        {
            profile.IndexStatus = IndexStatus.Failed;
            profile.IndexError = string.Join(" ", validation.Errors);
            await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
            return new IndexBuildResult { Error = profile.IndexError };
        }
        var install = validation.Install!;
        profile.ManagedPath = install.ManagedPath;
        profile.ConfigPath = install.ConfigPath;
        profile.ModsPath = install.ModsPath;
        profile.HarmonyAssemblyPath ??= install.HarmonyAssemblyPath;
        progress?.Report(new IndexProgress("Detecting game version...", 0, 0));
        var version = await Task.Run(() => _versionDetector.Detect(install), ct).ConfigureAwait(false);
        profile.GameVersion = version.Version;
        profile.GameVersionSource = version.Source;
        profile.UnityVersion = version.UnityVersion;
        var runtime = _runtimeDetector.Detect(install.ManagedPath!);
        profile.Compilation.DetectedTargetRuntime = runtime.Description;
        profile.Compilation.DetectedCoreLibrary = runtime.CoreLibraryPath;

        profile.IndexStatus = IndexStatus.Indexing;
        profile.IndexError = null;
        profile.IndexPath ??= _paths.GameIndexPath(profile.Id);
        await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        lock (_indexCache) _indexCache.Remove(profile.Id);

        IndexBuildResult result;
        try
        {
            result = await _indexBuilder.BuildAsync(profile, profile.IndexPath, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            profile.IndexStatus = File.Exists(profile.IndexPath) ? IndexStatus.Stale : IndexStatus.NotIndexed;
            profile.IndexError = "Indexing was cancelled.";
            await _profiles.SaveAsync(profile, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        profile.IndexStatus = result.Success ? IndexStatus.Indexed : IndexStatus.Failed;
        profile.IndexError = result.Error;
        string? previous = null, current = null;
        if (result.Success)
        {
            profile.LastIndexedUtc = DateTimeOffset.UtcNow;
            previous = profile.AssemblyFingerprint;
            current = ComputeFingerprint(profile);
            profile.AssemblyFingerprint = current;
        }
        await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        if (current != null && previous != current)
        {
            // Game update (or first index): knowledge produced against other assemblies must be re-verified.
            // Listeners run after the new index state is saved so they can query it.
            foreach (var l in _updateListeners)
                await l.OnGameAssembliesChangedAsync(profile.Id, previous, current, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>MVID + size of the primary game assemblies: changes whenever the game is updated.</summary>
    public static string? ComputeFingerprint(GameProfile profile)
    {
        if (profile.ManagedPath is null) return null;
        var parts = new List<string>();
        foreach (var name in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
        {
            var path = Path.Combine(profile.ManagedPath, name);
            if (!File.Exists(path)) continue;
            var report = new MadModStudio.ModAnalysis.Assemblies.AssemblyInspector().Inspect(path,
                new MadModStudio.ModAnalysis.Assemblies.AssemblyInspectionOptions { IncludeMembers = false, ComputeHash = false, CollectExternalReferences = false, IncludeNonPublic = false });
            parts.Add($"{name}:{report.Mvid}:{new FileInfo(path).Length}");
        }
        return parts.Count == 0 ? null : string.Join("|", parts);
    }

    /// <summary>Returns the knowledge index for a profile, or null if it hasn't been built.</summary>
    public IGameKnowledgeIndex? GetIndex(GameProfile? profile)
    {
        if (profile?.IndexPath is null || !File.Exists(profile.IndexPath) || profile.IndexStatus == IndexStatus.Indexing) return null;
        lock (_indexCache)
        {
            if (!_indexCache.TryGetValue(profile.Id, out var idx))
            {
                idx = new SqliteGameKnowledgeIndex(profile.IndexPath);
                _indexCache[profile.Id] = idx;
            }
            return idx;
        }
    }

    public Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken ct = default) => _profiles.ListAsync(ct);
    public Task<GameProfile?> GetAsync(Guid id, CancellationToken ct = default) => _profiles.GetAsync(id, ct);
    public Task SaveAsync(GameProfile profile, CancellationToken ct = default) => _profiles.SaveAsync(profile, ct);

    public async Task DeleteAsync(GameProfile profile, CancellationToken ct = default)
    {
        lock (_indexCache) _indexCache.Remove(profile.Id);
        await _profiles.DeleteAsync(profile.Id, ct).ConfigureAwait(false);
        // Only our own cache file is removed; a game installation the user pointed us at is never touched.
        // The exception is a copy we extracted from a ZIP, which lives in our data folder and belongs to the profile.
        var managedCopy = ManagedCopyRoot(profile.InstallPath);
        if (managedCopy != null) DeleteExtracted(managedCopy);
        if (profile.IndexPath != null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(profile.IndexPath);
            if (dir != null && Directory.Exists(dir) && dir.StartsWith(_paths.GameIndexes, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir, recursive: true);
        }
    }
}
