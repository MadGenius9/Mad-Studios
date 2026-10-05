using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
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

    public GameProfileService(IGameProfileRepository profiles, GameInstallLocator locator, GameVersionDetector versionDetector,
        RuntimeProfileDetector runtimeDetector, GameIndexBuilder indexBuilder, AppPaths paths, ILogger<GameProfileService>? log = null)
    {
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
        if (result.Success) profile.LastIndexedUtc = DateTimeOffset.UtcNow;
        await _profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        return result;
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
        // Only our own cache file is removed; nothing in the game installation is touched.
        if (profile.IndexPath != null)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var dir = Path.GetDirectoryName(profile.IndexPath);
            if (dir != null && Directory.Exists(dir) && dir.StartsWith(_paths.GameIndexes, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir, recursive: true);
        }
    }
}
