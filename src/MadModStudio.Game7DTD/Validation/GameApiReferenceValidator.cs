using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.ModAnalysis.Assemblies;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Compares every type/method/field a mod DLL uses from the game's assemblies against the installed game's index.
/// Missing members are exactly what produces MissingMethodException / MissingFieldException / TypeLoadException
/// after a game update.
/// </summary>
public sealed class GameApiReferenceValidator : ValidatorBase
{
    private readonly AssemblyInspector _inspector;
    public GameApiReferenceValidator(AssemblyInspector inspector) => _inspector = inspector;

    public override string Id => "game-api";
    public override string DisplayName => "Game API references";
    public override bool IsApplicable(ValidationContext context) => HasDlls(context);

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var index = ctx.GameIndex;
        var dlls = ModFiles(ctx).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dlls.Count == 0) return;
        if (index is null)
        {
            findings.Add(F(Severity.Info, "Game index not available; DLL API references were not checked. Reindex the Game Profile to enable this check."));
            return;
        }
        var indexed = index.GetIndexedAssemblyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var d in dlls)
        {
            ct.ThrowIfCancellationRequested();
            var report = _inspector.Inspect(Path.Combine(ctx.ModRootPath, d), new AssemblyInspectionOptions { IncludeMembers = false, ComputeHash = false });
            if (!report.Success) continue;
            var problems = 0;
            var checkedCount = 0;
            var missingTypes = new HashSet<string>();
            foreach (var t in report.ExternalTypes.Where(t => indexed.Contains(t.Assembly)))
            {
                checkedCount++;
                if (index.GetType(t.FullName) is null && missingTypes.Add(t.FullName) && ++problems <= 40)
                    findings.Add(F(Severity.Error, $"Uses type '{t.FullName}' from {t.Assembly}, which does not exist in the installed game (TypeLoadException likely).", d));
            }
            foreach (var m in report.ExternalMembers.Where(m => indexed.Contains(m.Assembly) && m.Kind != "Unknown"))
            {
                if (missingTypes.Contains(m.DeclaringType)) continue;
                checkedCount++;
                var type = index.GetType(m.DeclaringType);
                if (type is null) continue; // generic instantiations etc. resolved via type check above
                var candidates = index.FindMemberInHierarchy(type.FullName, m.Name);
                if (m.Kind == "Field")
                {
                    // Auto-properties / fields: accept any member with that name that is a field.
                    if (!candidates.Any(c => c.Kind == "Field") && ++problems <= 40)
                        findings.Add(F(Severity.Error, $"Field '{m.DeclaringType}.{m.Name}' does not exist in the installed game (MissingFieldException likely).", d, evidence: m.Signature));
                    continue;
                }
                var methods = candidates.Where(c => c.Kind is "Method" or "Constructor").ToList();
                if (methods.Count == 0)
                {
                    // Property accessors are indexed as properties.
                    if ((m.Name.StartsWith("get_") || m.Name.StartsWith("set_")) && index.FindMemberInHierarchy(type.FullName, m.Name[4..]).Any(c => c.Kind == "Property")) continue;
                    if (m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
                    if (++problems <= 40)
                        findings.Add(F(Severity.Error, $"Method '{m.DeclaringType}.{m.Name}' does not exist in the installed game (MissingMethodException likely).", d, evidence: m.Signature));
                }
                else if (!methods.Any(c => c.ParameterCount == m.ParameterCount) && ++problems <= 40)
                {
                    findings.Add(F(Severity.Error,
                        $"'{m.DeclaringType}.{m.Name}' exists but no overload takes {m.ParameterCount} parameter(s); the game has: {string.Join("; ", methods.Select(x => x.Signature).Take(4))}.", d, evidence: m.Signature));
                }
            }
            if (problems > 40) findings.Add(F(Severity.Error, $"{problems - 40} more missing game API references in this DLL.", d));
            if (problems == 0 && checkedCount > 0)
                findings.Add(F(Severity.Pass, $"All {checkedCount} game API references resolve against the installed game.", d));
        }
    }
}
