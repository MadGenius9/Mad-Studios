using System.Text.RegularExpressions;
using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;
using MadModStudio.Game7DTD.Mods;

namespace MadModStudio.Game7DTD.Validation;

public sealed class ModInfoValidator : ValidatorBase
{
    public override string Id => "modinfo";
    public override string DisplayName => "ModInfo structure";

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var path = Path.Combine(ctx.ModRootPath, ModInfoFile.FileName);
        if (!File.Exists(path))
        {
            var nested = ModInfoFile.FindAll(ctx.ModRootPath, 3).FirstOrDefault();
            findings.Add(F(Severity.Error, nested is null
                ? "ModInfo.xml is missing. The game will not load this folder as a mod."
                : $"ModInfo.xml is not at the mod root (found at {Path.GetRelativePath(ctx.ModRootPath, nested)}). The mod folder hierarchy is wrong.", ModInfoFile.FileName));
            return;
        }
        var info = ModInfoFile.TryParse(path, out var error);
        if (info is null)
        {
            findings.Add(F(Severity.Error, error ?? "ModInfo.xml could not be parsed.", ModInfoFile.FileName));
            return;
        }
        if (info.Format == ModInfoFormat.Unknown)
        {
            findings.Add(F(Severity.Error, "ModInfo.xml contains no recognised fields (expected Name, Version, ... elements with value attributes).", ModInfoFile.FileName));
            return;
        }
        if (info.Format == ModInfoFormat.V1)
            findings.Add(F(Severity.Warning, "ModInfo.xml uses the legacy <ModInfo> wrapper layout. Current game versions expect fields directly under <xml> (Name, DisplayName, Version, ...).", ModInfoFile.FileName));
        if (string.IsNullOrWhiteSpace(info.Name))
            findings.Add(F(Severity.Error, "ModInfo.xml has no Name value.", ModInfoFile.FileName));
        else if (info.Format == ModInfoFormat.V2 && !Regex.IsMatch(info.Name, @"^[A-Za-z0-9_\-.]+$"))
            findings.Add(F(Severity.Warning, $"ModInfo Name '{info.Name}' contains spaces or special characters. Name is the internal identifier; use DisplayName for the friendly name.", ModInfoFile.FileName));
        if (info.Format == ModInfoFormat.V2 && string.IsNullOrWhiteSpace(info.DisplayName))
            findings.Add(F(Severity.Info, "ModInfo.xml has no DisplayName.", ModInfoFile.FileName));
        if (string.IsNullOrWhiteSpace(info.Version))
            findings.Add(F(Severity.Error, "ModInfo.xml has no Version value.", ModInfoFile.FileName));
        else if (!System.Version.TryParse(info.Version, out _))
            findings.Add(F(Severity.Warning, $"Version '{info.Version}' is not a numeric version (e.g. 1.0.9). The game may not parse it.", ModInfoFile.FileName));
        if (string.IsNullOrWhiteSpace(info.Author))
            findings.Add(F(Severity.Info, "ModInfo.xml has no Author.", ModInfoFile.FileName));
        if (ctx.Project != null && info.Version != null && ctx.Project.Version != info.Version)
            findings.Add(F(Severity.Info, $"Project version ({ctx.Project.Version}) differs from ModInfo version ({info.Version}).", ModInfoFile.FileName));
    }
}
