using System.Text.RegularExpressions;

namespace MadModStudio.Core.Security;

/// <summary>Removes things that look like API keys from text before it is logged or displayed.</summary>
public static partial class SecretRedactor
{
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{12,}|AIza[0-9A-Za-z_\-]{20,}|(?i:x-api-key|authorization|api[_-]?key)\s*[:=]\s*\S+)")]
    private static partial Regex SecretPattern();

    public static string Redact(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : SecretPattern().Replace(text, "[REDACTED]");
}
