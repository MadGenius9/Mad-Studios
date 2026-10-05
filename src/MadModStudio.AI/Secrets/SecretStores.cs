using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace MadModStudio.AI.Secrets;

/// <summary>Stores credentials such as API keys. Values are never logged.</summary>
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
    bool CanWrite { get; }
    string Description { get; }
}

/// <summary>
/// Windows DPAPI (CurrentUser scope): secrets are encrypted with the user's Windows credentials and stored outside the
/// project/workspace folders, so they can never end up in a package, revision history or Git.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MadModStudio.v1");
    private readonly string _dir;

    public DpapiSecretStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(_dir);
    }

    public bool CanWrite => true;
    public string Description => "Windows Data Protection (DPAPI, current user)";

    private string PathFor(string name) => Path.Combine(_dir, Core.IO.PathSafety.SanitizeFileName(name) + ".bin");

    public string? Get(string name)
    {
        var p = PathFor(name);
        if (!File.Exists(p)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; }
    }

    public void Set(string name, string value) =>
        File.WriteAllBytes(PathFor(name), ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    public void Delete(string name)
    {
        var p = PathFor(name);
        if (File.Exists(p)) File.Delete(p);
    }
}

/// <summary>Read-only store backed by environment variables (ANTHROPIC_API_KEY, OPENAI_API_KEY, GEMINI_API_KEY, XAI_API_KEY). Used on non-Windows hosts and CI.</summary>
public sealed class EnvironmentSecretStore : ISecretStore
{
    private readonly IReadOnlyDictionary<string, string> _map;

    public EnvironmentSecretStore(IReadOnlyDictionary<string, string>? nameToVariable = null) =>
        _map = nameToVariable ?? new Dictionary<string, string>
        {
            ["anthropic.apikey"] = "ANTHROPIC_API_KEY",
            ["openai.apikey"] = "OPENAI_API_KEY",
            ["google.apikey"] = "GEMINI_API_KEY",
            ["xai.apikey"] = "XAI_API_KEY",
            ["custom.apikey"] = "MADMODSTUDIO_CUSTOM_API_KEY",
        };

    public bool CanWrite => false;
    public string Description => "Environment variables (read-only)";
    public string? Get(string name) => _map.TryGetValue(name, out var v) ? Environment.GetEnvironmentVariable(v) is { Length: > 0 } s ? s : null : null;
    public void Set(string name, string value) => throw new NotSupportedException("Environment secrets are read-only.");
    public void Delete(string name) => throw new NotSupportedException("Environment secrets are read-only.");
}

/// <summary>Reads from the first store that has a value; writes go to the first writable store.</summary>
public sealed class CompositeSecretStore : ISecretStore
{
    private readonly IReadOnlyList<ISecretStore> _stores;
    public CompositeSecretStore(params ISecretStore[] stores) => _stores = stores;

    public bool CanWrite => _stores.Any(s => s.CanWrite);
    public string Description => string.Join(" → ", _stores.Select(s => s.Description));
    public string? Get(string name) => _stores.Select(s => s.Get(name)).FirstOrDefault(v => v != null);
    public void Set(string name, string value) => (_stores.FirstOrDefault(s => s.CanWrite) ?? throw new NotSupportedException("No writable secret store on this platform.")).Set(name, value);
    public void Delete(string name) { foreach (var s in _stores.Where(s => s.CanWrite)) s.Delete(name); }

    public static ISecretStore CreateDefault(string secretsDirectory) =>
        OperatingSystem.IsWindows()
            ? new CompositeSecretStore(new DpapiSecretStore(secretsDirectory), new EnvironmentSecretStore())
            : new CompositeSecretStore(new EnvironmentSecretStore());
}
