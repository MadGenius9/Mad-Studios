using MadModStudio.AI.Engines;
using MadModStudio.AI.Providers;
using MadModStudio.AI.Secrets;
using MadModStudio.Core;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.AI;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the AI layer. The host must also register an <see cref="IAIConsentService"/>.</summary>
    public static IServiceCollection AddAI(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton<ISecretStore>(_ => CompositeSecretStore.CreateDefault(paths.Secrets));
        services.AddSingleton<AnthropicProvider>();
        services.AddSingleton<IAIProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
        services.AddSingleton<AIRepairEngine>();
        services.AddSingleton<AIModBuilder>();
        return services;
    }
}
