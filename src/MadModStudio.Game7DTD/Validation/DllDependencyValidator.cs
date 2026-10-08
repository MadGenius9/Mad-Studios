using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>Checks that every assembly referenced by the mod's DLLs can be found in the game, the mod, Harmony or other installed mods.</summary>
public sealed class DllDependencyValidator : ValidatorBase
{
    private readonly AssemblyInspector _inspector;
    public DllDependencyValidator(AssemblyInspector inspector) => _inspector = inspector;

    public override string Id => "dll-dependencies";
    public override string DisplayName => "DLL dependency resolution";
    public override bool IsApplicable(ValidationContext context) => HasDlls(context);

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var dlls = ModFiles(ctx).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dlls.Count == 0) return;
        var managed = ctx.GameProfile?.ManagedPath;
        var managedVersions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        if (managed != null && Directory.Exists(managed))
            foreach (var f in Directory.GetFiles(managed, "*.dll"))
                if (AssemblyInspector.ReadIdentity(f) is { } id) managedVersions[id.Name] = id.Version;
        if (ctx.GameProfile?.HarmonyAssemblyPath is { } h && AssemblyInspector.ReadIdentity(h) is { } hid) managedVersions[hid.Name] = hid.Version;

        var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in dlls)
            if (AssemblyInspector.ReadIdentity(Path.Combine(ctx.ModRootPath, d)) is { } id) local[id.Name] = d;

        var otherMods = OtherModAssemblies(ctx);

        foreach (var d in dlls)
        {
            ct.ThrowIfCancellationRequested();
            var identity = AssemblyInspector.ReadIdentity(Path.Combine(ctx.ModRootPath, d));
            if (identity is null)
            {
                findings.Add(F(Severity.Info, "Not a managed .NET assembly (native or resource DLL); dependencies not checked.", d));
                continue;
            }
            foreach (var r in identity.Value.References)
            {
                if (local.ContainsKey(r.Name)) continue;
                if (managedVersions.TryGetValue(r.Name, out var available))
                {
                    if (Version.TryParse(r.Version, out var wanted) && wanted > available && !IsUnifiedFramework(r.Name))
                        findings.Add(F(Severity.Warning, $"References {r.Name} {r.Version} but the game provides {available}.", d));
                    continue;
                }
                if (managed is null)
                {
                    findings.Add(F(Severity.Info, $"Reference '{r.Name}' not checked (no Game Profile).", d));
                    continue;
                }
                if (r.Name == "0Harmony")
                {
                    // The game ships Harmony itself (Mods/0_TFP_Harmony); a profile without that folder (e.g. from a ZIP) just can't see it.
                    findings.Add(F(Severity.Info, $"References 0Harmony {r.Version}, which the game ships in Mods/0_TFP_Harmony; that folder is not part of this Game Profile, so the version was not compared.", d));
                    continue;
                }
                if (otherMods.TryGetValue(r.Name, out var modPath))
                {
                    findings.Add(F(Severity.Info, $"Depends on '{r.Name}', provided by another installed mod ({modPath}). That mod must be installed too.", d));
                    continue;
                }
                if (r.Name is "System.Runtime" or "System.Private.CoreLib" || (r.Name.StartsWith("System.") && Version.TryParse(r.Version, out var v) && v.Major >= 5))
                    findings.Add(F(Severity.Error, $"References {r.Name} {r.Version}, which the game's runtime does not provide. The DLL was probably compiled for .NET Core/.NET 5+ instead of the game's runtime and will fail to load.", d));
                else
                    findings.Add(F(Severity.Warning, $"Unresolved dependency '{r.Name}, Version={r.Version}': not in the game, this mod, or other installed mods. The DLL may fail to load.", d));
            }
        }
    }

    private static bool IsUnifiedFramework(string name) => name is "mscorlib" or "netstandard" || name.StartsWith("System");

    private static Dictionary<string, string> OtherModAssemblies(ValidationContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mods = ctx.GameProfile?.ModsPath;
        if (mods is null || !Directory.Exists(mods)) return result;
        try
        {
            foreach (var f in Directory.EnumerateFiles(mods, "*.dll", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }))
                if (AssemblyInspector.ReadIdentity(f) is { } id) result.TryAdd(id.Name, Path.GetRelativePath(mods, f));
        }
        catch (IOException) { }
        return result;
    }
}
