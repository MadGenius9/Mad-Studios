using MadModStudio.Core.IO;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

public abstract class ValidatorBase : IModValidator
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public virtual bool IsApplicable(ValidationContext context) => true;

    protected static bool HasDlls(ValidationContext ctx) =>
        Directory.Exists(ctx.ModRootPath) && Directory.EnumerateFiles(ctx.ModRootPath, "*.dll", SearchOption.AllDirectories).Any();

    public Task<IReadOnlyList<ValidationFinding>> ValidateAsync(ValidationContext context, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var findings = new List<ValidationFinding>();
            Validate(context, findings, ct);
            return (IReadOnlyList<ValidationFinding>)findings;
        }, ct);

    protected abstract void Validate(ValidationContext context, List<ValidationFinding> findings, CancellationToken ct);

    protected ValidationFinding F(Severity s, string message, string? file = null, int? line = null, string? evidence = null) =>
        new(Id, s, message, file, line, evidence);

    /// <summary>Major game version from a profile's version text such as "V 3.30 (b18)", or null if unknown.</summary>
    protected static int? GameMajorVersion(GameProfile? profile) =>
        profile?.GameVersion is { } v && System.Text.RegularExpressions.Regex.Match(v, @"(\d+)\.\d+") is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    protected static IEnumerable<string> ModFiles(ValidationContext ctx) =>
        FileUtil.EnumerateRelativeFiles(ctx.ModRootPath, new FileExclusionRules { IncludeSource = true, IncludePdb = true });
}
