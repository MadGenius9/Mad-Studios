using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Harmony;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>Resolves each Harmony patch's target type/method against the installed game's index.</summary>
public sealed class HarmonyTargetValidator : ValidatorBase
{
    public const string SourcePatchesKey = "harmony.sourcePatches";
    private readonly AssemblyInspector _inspector;

    public HarmonyTargetValidator(AssemblyInspector inspector) => _inspector = inspector;

    public override string Id => "harmony-targets";
    public override string DisplayName => "Harmony targets";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var patches = new List<HarmonyPatchInfo>();
        if (ctx.Items.TryGetValue(SourcePatchesKey, out var src) && src is IEnumerable<HarmonyPatchInfo> sp) patches.AddRange(sp);
        foreach (var d in ModFiles(ctx).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var r = _inspector.Inspect(Path.Combine(ctx.ModRootPath, d), new AssemblyInspectionOptions { IncludeMembers = false, ComputeHash = false, CollectExternalReferences = false });
            // Compiled patches from this build duplicate the source patches; prefer source (has file/line).
            if (r.Success && !patches.Any(p => p.Origin == "Source"))
                patches.AddRange(r.HarmonyPatches.Select(p => p with { File = d }));
        }
        if (patches.Count == 0) return;
        if (ctx.GameIndex is null)
        {
            findings.Add(F(Severity.Info, $"{patches.Count} Harmony patch(es) found, but the game index is not available to verify their targets."));
            return;
        }
        foreach (var p in patches)
        {
            ct.ThrowIfCancellationRequested();
            Check(p, ctx.GameIndex, findings);
        }
    }

    public void Check(HarmonyPatchInfo p, IGameKnowledgeIndex index, List<ValidationFinding> findings)
    {
        var where = p.PatchMethod is null ? p.PatchClass : $"{p.PatchClass}.{p.PatchMethod}";
        if (p.IsDynamicTarget)
        {
            findings.Add(F(Severity.Info, $"{where}: target is computed at runtime (TargetMethod); it cannot be verified statically.", p.File, p.Line));
            return;
        }
        if (p.TargetType is null)
        {
            findings.Add(F(Severity.Warning, $"{where}: Harmony patch has no target type that could be determined statically.", p.File, p.Line));
            return;
        }
        var type = index.GetType(p.TargetType);
        if (type is null)
        {
            var similar = index.SearchTypes(p.TargetType.Split('.', '+').Last(), 3).Select(t => t.FullName).ToList();
            findings.Add(F(Severity.Warning, $"{where}: target type '{p.TargetType}' was not found in the installed game's indexed assemblies.{(similar.Count > 0 ? " Similar: " + string.Join(", ", similar) : "")}", p.File, p.Line));
            return;
        }
        string? memberName = p.MethodType switch
        {
            "Constructor" => ".ctor",
            "StaticConstructor" => ".cctor",
            _ => p.TargetMethod,
        };
        if (memberName is null)
        {
            findings.Add(F(Severity.Warning, $"{where}: target method name could not be determined (type {type.FullName} resolved).", p.File, p.Line));
            return;
        }
        var members = index.FindMemberInHierarchy(type.FullName, memberName);
        if (p.MethodType is "Getter" or "Setter")
        {
            if (!members.Any(m => m.Kind == "Property"))
                findings.Add(F(Severity.Error, $"{where}: property '{type.FullName}.{memberName}' does not exist in the installed game; Harmony will fail to patch it.", p.File, p.Line));
            return;
        }
        var methods = members.Where(m => m.Kind is "Method" or "Constructor").ToList();
        if (methods.Count == 0)
        {
            var similar = index.GetMembers(type.FullName).Where(m => m.Kind == "Method" && Similar(m.Name, memberName)).Select(m => m.Name).Distinct().Take(5).ToList();
            findings.Add(F(Severity.Error, $"{where}: method '{type.FullName}.{memberName}' does not exist in the installed game; Harmony will throw 'Undefined target method'.{(similar.Count > 0 ? " Similar methods: " + string.Join(", ", similar) : "")}", p.File, p.Line));
            return;
        }
        if (p.ArgumentTypes is { } args && !methods.Any(m => m.ParameterCount == args.Count))
        {
            findings.Add(F(Severity.Error, $"{where}: no overload of '{type.FullName}.{memberName}' takes {args.Count} parameter(s). Available: {string.Join("; ", methods.Select(m => m.Signature).Take(4))}", p.File, p.Line));
            return;
        }
        if (p.ArgumentTypes is null && methods.Count > 1 && methods.Select(m => m.ParameterCount).Distinct().Count() > 1)
            findings.Add(F(Severity.Warning, $"{where}: '{type.FullName}.{memberName}' has {methods.Count} overloads; without argument types Harmony throws an ambiguous match error.", p.File, p.Line));
    }

    private static bool Similar(string a, string b)
    {
        if (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        return Levenshtein(a.ToLowerInvariant(), b.ToLowerInvariant()) <= Math.Max(2, b.Length / 4);
    }

    private static int Levenshtein(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= t.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[s.Length, t.Length];
    }
}
