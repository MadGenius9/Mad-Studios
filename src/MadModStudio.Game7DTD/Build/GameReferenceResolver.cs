using MadModStudio.Core.Models;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Build;

public sealed record ResolvedReferences(IReadOnlyList<string> Paths, IReadOnlyList<string> Notes);

/// <summary>
/// Builds the compiler reference set from the user's own game installation (never from redistributed copies).
/// </summary>
public sealed class GameReferenceResolver
{
    public ResolvedReferences Resolve(GameProfile profile, IEnumerable<string>? modLocalDlls = null, IEnumerable<string>? additional = null, string? excludeAssemblyName = null)
    {
        var notes = new List<string>();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(profile.Compilation.ExcludedReferences, StringComparer.OrdinalIgnoreCase);

        void Add(string path, string origin)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (excludeAssemblyName != null && name.Equals(excludeAssemblyName, StringComparison.OrdinalIgnoreCase)) return;
            if (excluded.Contains(Path.GetFileName(path))) return;
            if (byName.ContainsKey(name)) return; // first source wins: game > harmony > extras > mod-local
            if (!AssemblyInspector.IsManagedAssembly(path)) return;
            byName[name] = path;
        }

        if (profile.ManagedPath != null && Directory.Exists(profile.ManagedPath))
        {
            var count = 0;
            foreach (var dll in Directory.GetFiles(profile.ManagedPath, "*.dll"))
            {
                Add(dll, "game");
                count++;
            }
            notes.Add($"Game Managed folder: {profile.ManagedPath} ({count} assemblies)");
        }
        else notes.Add("Game Managed folder is missing — no game references available.");

        if (profile.HarmonyAssemblyPath != null && File.Exists(profile.HarmonyAssemblyPath))
        {
            Add(profile.HarmonyAssemblyPath, "harmony");
            notes.Add($"Harmony: {profile.HarmonyAssemblyPath}");
        }
        foreach (var extra in profile.Compilation.ExtraReferencePaths.Concat(additional ?? Array.Empty<string>()))
        {
            if (File.Exists(extra)) { Add(extra, "extra"); notes.Add($"Extra reference: {extra}"); }
            else notes.Add($"Extra reference not found (skipped): {extra}");
        }
        foreach (var local in modLocalDlls ?? Array.Empty<string>())
        {
            if (File.Exists(local)) Add(local, "mod");
        }
        return new ResolvedReferences(byName.Values.ToList(), notes);
    }
}
