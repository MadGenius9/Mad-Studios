using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Install;

public sealed class DetectedInstall
{
    public string InstallPath { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public string? DataFolder { get; init; }
    public string? ManagedPath { get; init; }
    public string? AssemblyCSharpPath { get; init; }
    public string? ConfigPath { get; init; }
    public string? ModsPath { get; init; }
    public bool ModsFolderExists { get; init; }
    public string? HarmonyAssemblyPath { get; init; }
    public bool IsDedicatedServer { get; init; }
}

public sealed class InstallValidation
{
    public bool IsValid => Errors.Count == 0 && Install != null;
    public DetectedInstall? Install { get; init; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Validates a user-selected folder as a 7 Days to Die installation by discovering its structure rather than assuming
/// fixed paths. Accepts the install root or a folder inside it (e.g. the Managed folder).
/// </summary>
public sealed class GameInstallLocator
{
    public InstallValidation Validate(string? selectedPath)
    {
        var v = new InstallValidation();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            v.Errors.Add("No folder was selected.");
            return v;
        }
        string full;
        try { full = Path.GetFullPath(selectedPath.Trim().Trim('"')); }
        catch (Exception ex) { v.Errors.Add($"The path is not valid: {ex.Message}"); return v; }
        if (!Directory.Exists(full))
        {
            v.Errors.Add($"The folder does not exist: {full}");
            return v;
        }

        // Walk up a few levels so the user can pick e.g. ...\7DaysToDie_Data\Managed.
        string? root = null; string? dataFolder = null;
        var candidate = full;
        for (var i = 0; i < 4 && candidate != null; i++)
        {
            dataFolder = FindDataFolder(candidate);
            if (dataFolder != null) { root = candidate; break; }
            candidate = Path.GetDirectoryName(candidate);
        }
        if (root is null || dataFolder is null)
        {
            v.Errors.Add("No Unity data folder containing Managed/Assembly-CSharp.dll was found. Select the folder that contains the game executable (for example \"7 Days To Die\").");
            return v;
        }
        if (!string.Equals(root, full, StringComparison.OrdinalIgnoreCase))
            v.Notes.Add($"Using install root {root} (detected from the selected folder).");

        var managed = Path.Combine(dataFolder, "Managed");
        var asm = Path.Combine(managed, "Assembly-CSharp.dll");
        if (!AssemblyInspector.IsManagedAssembly(asm))
            v.Errors.Add($"Assembly-CSharp.dll exists but could not be read as a managed assembly: {asm}");

        var dataName = Path.GetFileName(dataFolder);
        var exeBase = dataName.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) ? dataName[..^5] : dataName;
        var exe = new[] { exeBase + ".exe", exeBase + ".x86_64", exeBase, exeBase + ".app" }
            .Select(n => Path.Combine(root, n)).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        var isServer = exeBase.Contains("Server", StringComparison.OrdinalIgnoreCase);

        var config = FindConfigFolder(root);
        if (config is null)
            v.Errors.Add("Could not find the game's XML configuration folder (expected a folder containing blocks.xml / items.xml, usually Data/Config).");

        var mods = Path.Combine(root, "Mods");
        var modsExists = Directory.Exists(mods);
        if (!modsExists)
            v.Warnings.Add($"No Mods folder exists yet at {mods}. It will be used as the default deploy location once created.");

        var harmony = FindHarmony(root, managed);
        if (harmony is null)
            v.Warnings.Add("0Harmony.dll was not found in the Mods folder or Managed folder. Harmony-based mods will need a Harmony reference added in the Game Profile's compiler settings.");

        if (exe is null) v.Warnings.Add($"Game executable '{exeBase}' was not found next to {dataName}; continuing because the managed assemblies were found.");

        if (!exeBase.Contains("7DaysToDie", StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")))
            v.Errors.Add("This does not look like a 7 Days to Die installation.");
        else if (!exeBase.Contains("7DaysToDie", StringComparison.OrdinalIgnoreCase))
            v.Warnings.Add($"The Unity player is named '{exeBase}', not 7DaysToDie. Make sure this is a 7 Days to Die installation.");

        return new InstallValidation
        {
            Install = new DetectedInstall
            {
                InstallPath = root,
                ExecutablePath = exe,
                DataFolder = dataFolder,
                ManagedPath = managed,
                AssemblyCSharpPath = asm,
                ConfigPath = config,
                ModsPath = mods,
                ModsFolderExists = modsExists,
                HarmonyAssemblyPath = harmony,
                IsDedicatedServer = isServer,
            },
        }.CopyMessages(v);
    }

    private static string? FindDataFolder(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir, "*_Data")
                .OrderBy(d => Path.GetFileName(d).Contains("Server", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Managed", "Assembly-CSharp.dll")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindConfigFolder(string root)
    {
        var preferred = Path.Combine(root, "Data", "Config");
        if (IsConfigFolder(preferred)) return preferred;
        // Bounded search for a folder containing the core config files.
        try
        {
            foreach (var blocks in Directory.EnumerateFiles(root, "blocks.xml", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
            {
                var dir = Path.GetDirectoryName(blocks)!;
                if (dir.Contains(Path.DirectorySeparatorChar + "Mods" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsConfigFolder(dir)) return dir;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsConfigFolder(string dir) =>
        Directory.Exists(dir) && (File.Exists(Path.Combine(dir, "blocks.xml")) || File.Exists(Path.Combine(dir, "items.xml")));

    private static string? FindHarmony(string root, string managed)
    {
        var mods = Path.Combine(root, "Mods");
        if (Directory.Exists(mods))
        {
            try
            {
                var found = Directory.EnumerateFiles(mods, "0Harmony.dll", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .OrderBy(p => p.Contains("TFP", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var inManaged = Path.Combine(managed, "0Harmony.dll");
        return File.Exists(inManaged) ? inManaged : null;
    }
}

internal static class InstallValidationExtensions
{
    public static InstallValidation CopyMessages(this InstallValidation target, InstallValidation source)
    {
        target.Errors.AddRange(source.Errors);
        target.Warnings.AddRange(source.Warnings);
        target.Notes.AddRange(source.Notes);
        return target;
    }
}
