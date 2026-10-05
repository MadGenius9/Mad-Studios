using MadModStudio.AI.Secrets;

namespace MadModStudio.AI.Providers;

/// <summary>
/// User-defined OpenAI-compatible endpoint (local model servers, gateways, other cloud providers with a compatible
/// API). The API key is optional; the URL comes from Settings → AI Providers.
/// </summary>
public sealed class CustomEndpointProvider : OpenAICompatibleProvider
{
    private readonly AIPolicy _policy;

    public CustomEndpointProvider(AIPolicy policy, ISecretStore secrets, HttpClient? http = null)
        : base(ProviderIds.Custom, "Custom endpoint", () => policy.CustomEndpointUrl, secrets, keyRequired: false, http: http)
        => _policy = policy;

    public override string DisplayName => string.IsNullOrWhiteSpace(_policy.CustomEndpointName) ? "Custom endpoint" : _policy.CustomEndpointName;
}
