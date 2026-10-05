using MadModStudio.Core;
using MadModStudio.Game7DTD;
using MadModStudio.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MadModStudio.TestSupport;

/// <summary>A fully wired service provider over an isolated temporary app data folder.</summary>
public sealed class TestHost : IDisposable
{
    public TestHost()
    {
        Root = FakeGame.TempDir("apphome");
        Paths = new AppPaths(Root);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddPersistence(Paths);
        services.AddGame7DTD();
        Services = services.BuildServiceProvider();
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public ServiceProvider Services { get; }
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
