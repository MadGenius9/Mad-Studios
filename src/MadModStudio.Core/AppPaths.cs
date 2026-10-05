namespace MadModStudio.Core;

/// <summary>Locations of Mad Mod Studio's own data. Root is overridable for tests and portable installs.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Environment.GetEnvironmentVariable("MADMODSTUDIO_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MadModStudio");
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "madmodstudio.db");
    public string Workspace => Path.Combine(Root, "workspace");
    public string Projects => Path.Combine(Workspace, "projects");
    public string Cache => Path.Combine(Root, "cache");
    public string GameIndexes => Path.Combine(Cache, "game-index");
    public string Secrets => Path.Combine(Root, "secrets");
    public string Logs => Path.Combine(Root, "logs");
    public string Scratch => Path.Combine(Root, "scratch");
    /// <summary>Backups of game Mods folders replaced by "Deploy to Game", plus staging for deployments.</summary>
    public string Deployments => Path.Combine(Root, "deployments");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Workspace, Projects, Cache, GameIndexes, Secrets, Logs, Scratch, Deployments })
            Directory.CreateDirectory(d);
    }

    public string ProjectWorkspace(Guid projectId) => Path.Combine(Projects, projectId.ToString("N"));
    public string GameIndexPath(Guid profileId) => Path.Combine(GameIndexes, profileId.ToString("N"), "index.db");
}
