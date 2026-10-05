namespace MadModStudio.Core.Models;

/// <summary>
/// One installed copy of a game (initially 7 Days to Die). Only paths and index metadata are stored;
/// proprietary game assemblies are never copied.
/// </summary>
public sealed class GameProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string GameId { get; set; } = "7dtd";
    public string GameName { get; set; } = "7 Days to Die";
    public string? GameVersion { get; set; }
    /// <summary>Human readable explanation of where <see cref="GameVersion"/> came from.</summary>
    public string? GameVersionSource { get; set; }
    public string? UnityVersion { get; set; }
    public string InstallPath { get; set; } = "";
    public string? ExecutablePath { get; set; }
    public string? ModsPath { get; set; }
    public string? ManagedPath { get; set; }
    public string? ConfigPath { get; set; }
    public string? HarmonyAssemblyPath { get; set; }
    public bool IsDedicatedServer { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastIndexedUtc { get; set; }
    public IndexStatus IndexStatus { get; set; } = IndexStatus.NotIndexed;
    public string? IndexError { get; set; }
    /// <summary>Path to the per-profile knowledge index (filesystem cache, not the app database).</summary>
    public string? IndexPath { get; set; }
    public CompilationSettings Compilation { get; set; } = new();
}

/// <summary>
/// How mod DLLs are compiled for this game installation. Detected from the install and user-editable.
/// Kept separate from the runtime used by Mad Mod Studio itself.
/// </summary>
public sealed class CompilationSettings
{
    /// <summary>"GameManagedFolder": reference every managed assembly that ships in the game's Managed folder.</summary>
    public string ReferenceStrategy { get; set; } = "GameManagedFolder";
    public string LanguageVersion { get; set; } = "9.0";
    /// <summary>Description of the detected target runtime, e.g. "Unity Mono / mscorlib 4.0.0.0".</summary>
    public string? DetectedTargetRuntime { get; set; }
    public string? DetectedCoreLibrary { get; set; }
    public List<string> ExtraReferencePaths { get; set; } = new();
    /// <summary>File names (e.g. "UnityEngine.UI.dll") excluded from automatic references.</summary>
    public List<string> ExcludedReferences { get; set; } = new();
    public List<string> PreprocessorSymbols { get; set; } = new();
    public bool AllowUnsafe { get; set; }
}
