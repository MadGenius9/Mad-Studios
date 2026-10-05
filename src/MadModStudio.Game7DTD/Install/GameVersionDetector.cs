using System.Text.RegularExpressions;
using MadModStudio.ModAnalysis.Assemblies;
using MadModStudio.ModAnalysis.Decompilation;

namespace MadModStudio.Game7DTD.Install;

public sealed record GameVersionInfo(string? Version, string Source, string? SteamBuildId, string? UnityVersion, IReadOnlyList<string> Evidence);

/// <summary>
/// Determines the installed game version from the installation itself. Every value records where it came from;
/// when nothing can be determined the version is reported as unknown rather than guessed.
/// </summary>
public sealed partial class GameVersionDetector
{
    private readonly DecompilerService _decompiler;

    public GameVersionDetector(DecompilerService decompiler) => _decompiler = decompiler;

    public GameVersionInfo Detect(DetectedInstall install)
    {
        var evidence = new List<string>();
        string? version = null; string source = "unknown";

        if (install.AssemblyCSharpPath != null && File.Exists(install.AssemblyCSharpPath))
        {
            var decompiled = _decompiler.DecompileType(install.AssemblyCSharpPath, "Constants", install.ManagedPath is null ? null : new[] { install.ManagedPath });
            if (decompiled.Success)
            {
                var parsed = ParseConstants(decompiled.Text);
                if (parsed != null)
                {
                    version = parsed;
                    source = "Assembly-CSharp.dll → Constants.cVersionInformation (static metadata/IL analysis)";
                    evidence.Add($"Constants.cVersionInformation = {parsed}");
                }
                else evidence.Add("Constants type found but version initializer was not recognised.");
            }
            else evidence.Add($"Could not analyse Constants type: {decompiled.Error}");

            var id = AssemblyInspector.ReadIdentity(install.AssemblyCSharpPath);
            if (id != null) evidence.Add($"Assembly-CSharp assembly version {id.Value.Version}");
        }

        var steam = ReadSteamBuildId(install.InstallPath, install.IsDedicatedServer);
        if (steam != null)
        {
            evidence.Add($"Steam build id {steam} (from appmanifest)");
            if (version == null) { version = $"Steam build {steam}"; source = "Steam appmanifest build id"; }
        }

        var unity = DetectUnityVersion(install.DataFolder);
        if (unity != null) evidence.Add($"Unity {unity}");

        return new GameVersionInfo(version, source, steam, unity, evidence);
    }

    public static string? ParseConstants(string decompiledConstants)
    {
        var m = VersionInfoRegex().Match(decompiledConstants);
        if (!m.Success) return null;
        var args = m.Groups["args"].Value.Split(',').Select(a => a.Trim()).ToList();
        string? release = null; var numbers = new List<string>();
        foreach (var a in args)
        {
            if (Regex.IsMatch(a, @"^\(?\w*\)?\d+$")) numbers.Add(Regex.Match(a, @"\d+$").Value);
            else if (a.Contains('.')) release = a.Split('.').Last();
            else if (release == null && Regex.IsMatch(a, @"^[A-Za-z_]\w*$")) release = a;
        }
        if (numbers.Count == 0) return null;
        var core = numbers.Count >= 3 ? $"{numbers[0]}.{numbers[1]} (b{numbers[2]})" : string.Join('.', numbers);
        return release is null ? core : $"{release} {core}";
    }

    [GeneratedRegex(@"cVersionInformation\s*=\s*new\s+[\w.]*VersionInformation\s*\((?<args>[^;]*?)\)\s*;")]
    private static partial Regex VersionInfoRegex();

    [GeneratedRegex("\"buildid\"\\s+\"(\\d+)\"")]
    private static partial Regex BuildIdRegex();

    [GeneratedRegex(@"20\d\d\.\d+\.\d+[abfp]\d+")]
    private static partial Regex UnityVersionRegex();

    private static string? ReadSteamBuildId(string installPath, bool server)
    {
        // <steamapps>/common/<game>  ->  <steamapps>/appmanifest_<appid>.acf
        var common = Path.GetDirectoryName(installPath);
        var steamapps = common is null ? null : Path.GetDirectoryName(common);
        if (steamapps is null || !Path.GetFileName(common!).Equals("common", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var appId in server ? new[] { "294420", "251570" } : new[] { "251570", "294420" })
        {
            var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            try
            {
                var text = File.ReadAllText(manifest);
                var installDir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                if (installDir.Length > 0 && !installDir.Equals(Path.GetFileName(installPath), StringComparison.OrdinalIgnoreCase)) continue;
                var m = BuildIdRegex().Match(text);
                if (m.Success) return m.Groups[1].Value;
            }
            catch (IOException) { }
        }
        return null;
    }

    private static string? DetectUnityVersion(string? dataFolder)
    {
        if (dataFolder is null) return null;
        foreach (var name in new[] { "globalgamemanagers", "data.unity3d", "mainData" })
        {
            var p = Path.Combine(dataFolder, name);
            if (!File.Exists(p)) continue;
            try
            {
                using var fs = File.OpenRead(p);
                var buf = new byte[Math.Min(4096, fs.Length)];
                var n = fs.Read(buf, 0, buf.Length);
                var text = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                var m = UnityVersionRegex().Match(text);
                if (m.Success) return m.Value;
            }
            catch (IOException) { }
        }
        return null;
    }
}
