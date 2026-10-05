using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.Persistence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, AppPaths paths)
    {
        paths.EnsureCreated();
        services.AddSingleton(paths);
        services.AddSingleton(new AppDatabase(paths.DatabasePath));
        services.AddSingleton<IGameProfileRepository, SqliteGameProfileRepository>();
        services.AddSingleton<IProjectRepository, SqliteProjectRepository>();
        services.AddSingleton<IRevisionRepository, SqliteRevisionRepository>();
        services.AddSingleton<IBuildRecordRepository, SqliteBuildRecordRepository>();
        services.AddSingleton<IDeploymentRepository, SqliteDeploymentRepository>();
        services.AddSingleton<ISettingsRepository, SqliteSettingsRepository>();
        services.AddSingleton<MadModStudio.Core.Knowledge.IKnowledgeRepository, SqliteKnowledgeRepository>();
        services.AddSingleton<MadModStudio.Core.Knowledge.IModelPerformanceRepository, SqliteModelPerformanceRepository>();
        return services;
    }
}
