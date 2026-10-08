using MadModStudio.Core.Models;
using MadModStudio.Core.Validation;

namespace MadModStudio.Game7DTD.Validation;

/// <summary>
/// Catches mods written for a different config layout than the installed game uses. V3.0 renamed Localization.txt to
/// Localization.csv and split Config/XUi into XUi_InGame, XUi_Menu and XUi_Common; a mod built for the old layout loads
/// without errors but its text and UI patches silently do nothing. Decided from what the game's Config folder actually
/// contains (not from a version number), so it keeps working for later layout changes in either direction.
/// </summary>
public sealed class GameLayoutCompatibilityValidator : ValidatorBase
{
    private static readonly string[] NewUiFolders = { "XUi_InGame", "XUi_Menu", "XUi_Common" };

    public override string Id => "game-layout";
    public override string DisplayName => "Game version layout (Localization / XUi folders)";

    /// <summary>True when the game's Config uses the V3.0 split-XUi layout (and has no legacy Config/XUi).</summary>
    public static bool GameUsesSplitXui(string gameConfig) =>
        NewUiFolders.Any(d => Directory.Exists(Path.Combine(gameConfig, d))) && !Directory.Exists(Path.Combine(gameConfig, "XUi"));

    protected override void Validate(ValidationContext ctx, List<ValidationFinding> findings, CancellationToken ct)
    {
        var gameConfig = ctx.GameProfile?.ConfigPath;
        if (gameConfig is null || !Directory.Exists(gameConfig)) return; // reported by the XPath validator
        var files = ModFiles(ctx).ToList();
        bool Has(string rel) => files.Any(f => f.Equals(rel, StringComparison.OrdinalIgnoreCase));

        // Localization.txt (before V3.0) vs Localization.csv (V3.0+).
        var gameCsv = File.Exists(Path.Combine(gameConfig, "Localization.csv"));
        var gameTxt = File.Exists(Path.Combine(gameConfig, "Localization.txt"));
        if (gameCsv && !gameTxt && Has("Config/Localization.txt"))
        {
            if (Has("Config/Localization.csv"))
                findings.Add(F(Severity.Info, "Config/Localization.txt is ignored by this game (it reads Localization.csv); the mod also ships a .csv, so the .txt can be deleted.", "Config/Localization.txt"));
            else
                findings.Add(F(Severity.Warning, "This mod ships Config/Localization.txt, but this game version reads Config/Localization.csv (renamed in V3.0). The .txt is ignored, so the mod's text will show as raw keys. Rename it to Localization.csv.", "Config/Localization.txt"));
        }
        else if (gameTxt && !gameCsv && Has("Config/Localization.csv") && !Has("Config/Localization.txt"))
            findings.Add(F(Severity.Warning, "This mod ships Config/Localization.csv, but this game version reads Config/Localization.txt. The .csv is ignored, so the mod's text will show as raw keys.", "Config/Localization.csv"));

        // Config/XUi (before V3.0) vs XUi_InGame / XUi_Menu / XUi_Common (V3.0+).
        var legacyUi = files.Where(f => f.StartsWith("Config/XUi/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (GameUsesSplitXui(gameConfig))
        {
            foreach (var f in legacyUi.Take(20))
            {
                var hint = Path.GetFileName(f).Equals("controls.xml", StringComparison.OrdinalIgnoreCase)
                    ? " controls.xml was also renamed templates.xml." : "";
                findings.Add(F(Severity.Warning,
                    "This game version has no Config/XUi folder (split in V3.0 into XUi_InGame, XUi_Menu and XUi_Common), so patches here do not apply. " +
                    "Move it to Config/XUi_InGame/ (HUD and in-game windows) or Config/XUi_Menu/ (main menu); shared styles and templates go in Config/XUi_Common/." + hint, f));
            }
            if (legacyUi.Count > 20) findings.Add(F(Severity.Warning, $"{legacyUi.Count - 20} more files under the legacy Config/XUi folder."));
        }
        else if (Directory.Exists(Path.Combine(gameConfig, "XUi")) && !NewUiFolders.Any(d => Directory.Exists(Path.Combine(gameConfig, d))))
        {
            foreach (var f in files.Where(f => NewUiFolders.Any(d => f.StartsWith("Config/" + d + "/", StringComparison.OrdinalIgnoreCase))).Take(20))
                findings.Add(F(Severity.Warning, "This game version uses a single Config/XUi folder; the XUi_InGame / XUi_Menu / XUi_Common folders (V3.0+) are not read.", f));
        }
    }
}
