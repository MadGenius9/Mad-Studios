using MadModStudio.AI.Agents;
using MadModStudio.AI.Coordination;
using MadModStudio.AI.Knowledge;
using MadModStudio.AI.Models;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Routing;
using MadModStudio.AI.Secrets;
using MadModStudio.Core;
using MadModStudio.Core.Knowledge;
using MadModStudio.Game7DTD.Install;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.AI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the multi-provider, multi-agent AI layer. The host must register an <see cref="IAIConsentService"/> and an
    /// <see cref="IChangeApprovalService"/>, and should load <see cref="AIPolicy"/> from settings at startup.
    /// </summary>
    public static IServiceCollection AddAI(this IServiceCollection services, AppPaths paths)
    {
        var configDir = Path.Combine(paths.Root, "config");
        services.AddSingleton<AIPolicy>();
        services.AddSingleton<ISecretStore>(_ => CompositeSecretStore.CreateDefault(paths.Secrets));
        services.AddSingleton(_ => new ModelProfileStore(configDir));

        services.AddSingleton<IAIProvider, AnthropicProvider>();
        services.AddSingleton<IAIProvider>(sp => new OpenAICompatibleProvider(ProviderIds.OpenAI, "OpenAI GPT", () => "https://api.openai.com/v1", sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IAIProvider>(sp => new GeminiProvider(sp.GetRequiredService<ISecretStore>()));
        services.AddSingleton<IAIProvider>(sp => new OpenAICompatibleProvider(ProviderIds.XAI, "xAI Grok", () => "https://api.x.ai/v1", sp.GetRequiredService<ISecretStore>(), maxTokensField: "max_tokens"));
        services.AddSingleton<IAIProvider>(sp =>
        {
            var policy = sp.GetRequiredService<AIPolicy>();
            return new CustomEndpointProvider(policy, sp.GetRequiredService<ISecretStore>());
        });
        services.AddSingleton<IAIProviderRegistry, AIProviderRegistry>();
        services.AddSingleton<IModelCatalog>(sp => new ModelCatalog(sp.GetRequiredService<IAIProviderRegistry>(), sp.GetRequiredService<ModelProfileStore>(), configDir));

        services.AddSingleton<IModelPerformanceTracker, ModelPerformanceTracker>();
        services.AddSingleton<BudgetGuard>();
        services.AddSingleton<IModelRouter, ModelRouter>();

        services.AddSingleton(sp =>
        {
            var profiles = new Lazy<GameProfileService>(() => sp.GetRequiredService<GameProfileService>());
            return new ProjectKnowledgeService(sp.GetRequiredService<IKnowledgeRepository>(), id =>
            {
                var p = profiles.Value.GetAsync(id).GetAwaiter().GetResult();
                return profiles.Value.GetIndex(p);
            });
        });
        services.AddSingleton<IGameUpdateListener>(sp => sp.GetRequiredService<ProjectKnowledgeService>());
        services.AddSingleton<IAIContextBuilder, AIContextBuilder>();

        services.AddSingleton<AgentCatalog>();
        services.AddSingleton<FileOwnershipManager>();
        services.AddSingleton<AgentChangeService>();
        services.AddSingleton<AgentRunner>();
        services.AddSingleton<IAgentCoordinator, AgentCoordinator>();
        return services;
    }
}
