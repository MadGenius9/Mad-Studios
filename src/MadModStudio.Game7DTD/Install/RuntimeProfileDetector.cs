using MadModStudio.Core.Models;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Install;

public sealed record RuntimeProfile(string Description, string? CoreLibraryPath, string? MscorlibVersion, string? NetStandardVersion, string? AssemblyCSharpTargetFramework, IReadOnlyList<string> Evidence);

/// <summary>
/// Determines what runtime/API surface mod DLLs must target by reading the game's own managed assemblies. Mods are
/// compiled against exactly the assemblies the game ships, so no target framework is assumed.
/// </summary>
public sealed class RuntimeProfileDetector
{
    public RuntimeProfile Detect(string managedPath)
    {
        var evidence = new List<string>();
        string? mscorlibVer = null, nsVer = null, tfm = null, core = null;

        var mscorlib = Path.Combine(managedPath, "mscorlib.dll");
        var id = AssemblyInspector.ReadIdentity(mscorlib);
        if (id != null)
        {
            mscorlibVer = id.Value.Version.ToString();
            core = mscorlib;
            evidence.Add($"mscorlib.dll {mscorlibVer}");
        }
        var ns = AssemblyInspector.ReadIdentity(Path.Combine(managedPath, "netstandard.dll"));
        if (ns != null)
        {
            nsVer = ns.Value.Version.ToString();
            core ??= Path.Combine(managedPath, "netstandard.dll");
            evidence.Add($"netstandard.dll {nsVer} facade");
        }
        var sysRuntime = AssemblyInspector.ReadIdentity(Path.Combine(managedPath, "System.Runtime.dll"));
        if (sysRuntime != null) evidence.Add($"System.Runtime.dll {sysRuntime.Value.Version}");
        if (File.Exists(Path.Combine(managedPath, "System.Private.CoreLib.dll"))) evidence.Add("System.Private.CoreLib.dll present (CoreCLR-style runtime)");

        var asm = new AssemblyInspector().Inspect(Path.Combine(managedPath, "Assembly-CSharp.dll"),
            new AssemblyInspectionOptions { IncludeMembers = false, ComputeHash = false, CollectExternalReferences = false, IncludeNonPublic = false });
        if (asm.Success)
        {
            tfm = asm.TargetFramework;
            var refs = string.Join(", ", asm.References.Where(r => r.Name is "mscorlib" or "netstandard" or "System.Runtime").Select(r => r.ToString()));
            evidence.Add($"Assembly-CSharp references: {(refs.Length == 0 ? "(no core library reference found)" : refs)}");
            if (tfm != null) evidence.Add($"Assembly-CSharp TargetFramework attribute: {tfm}");
        }

        var coreClr = File.Exists(Path.Combine(managedPath, "System.Private.CoreLib.dll"));
        var description = coreClr
            ? $".NET (CoreCLR-style) runtime — System.Private.CoreLib present{(mscorlibVer != null ? $", mscorlib facade {mscorlibVer}" : "")}"
            : mscorlibVer != null
            ? $"Unity Mono-style runtime — mscorlib {mscorlibVer}{(nsVer != null ? $", netstandard {nsVer} facade" : "")}{(tfm != null ? $", game assembly built for {tfm}" : "")}"
            : nsVer != null ? $".NET Standard {nsVer} surface" : "Unknown runtime (no mscorlib.dll or netstandard.dll in Managed folder)";
        return new RuntimeProfile(description, core, mscorlibVer, nsVer, tfm, evidence);
    }

    public static CompilationSettings DefaultSettings(RuntimeProfile profile) => new()
    {
        ReferenceStrategy = "GameManagedFolder",
        // C# 9 is a conservative default that compiles to IL Unity's Mono runs; user can change it per profile.
        LanguageVersion = "9.0",
        DetectedTargetRuntime = profile.Description,
        DetectedCoreLibrary = profile.CoreLibraryPath,
    };
}
