using System.Windows;
using System.Windows.Threading;
using MadModStudio.AI;
using MadModStudio.AI.Agents;
using MadModStudio.App.Services;
using MadModStudio.App.ViewModels;
using MadModStudio.App.Views;
using MadModStudio.Core;
using MadModStudio.Core.Abstractions;
using MadModStudio.Core.Security;
using MadModStudio.Game7DTD;
using MadModStudio.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MadModStudio.App;

public partial class App : Application
{
    private static ILogger? _log;
    private ServiceProvider? _services;

    public static void Log(string message, Exception? ex = null) => _log?.LogError(ex, "{Message}", message);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log("Unobserved task exception", args.Exception); args.SetObserved(); };

        try
        {
            var paths = new AppPaths();
            _services = BuildServices(paths, fileLogging: true);
            _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("MadModStudio");
            _log.LogInformation("Mad Mod Studio starting. Data folder: {Root}", paths.Root);

            _services.GetRequiredService<AIPolicy>().CopyFrom(await AIPolicy.LoadAsync(_services.GetRequiredService<ISettingsRepository>()));
            var main = _services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = main };
            MainWindow = window;
            window.Show();
            await main.InitializeAsync();
            _log.LogInformation("Startup complete");
        }
        catch (Exception ex)
        {
            Log("Startup failed", ex);
            MessageBox.Show($"Mad Mod Studio could not start:\n\n{SecretRedactor.Redact(ex.Message)}", "Mad Mod Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>The application's composition root (also used by the UI tests).</summary>
    public static ServiceProvider BuildServices(AppPaths paths, bool fileLogging)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddDebug();
            if (fileLogging) b.AddProvider(new FileLoggerProvider(paths.Logs));
            b.SetMinimumLevel(LogLevel.Information);
        });
        services.AddPersistence(paths);
        services.AddGame7DTD();
        services.AddAI(paths);
        services.AddSingleton<IAIConsentService, WpfConsentService>();
        // GUIDED control: agent changes wait in this queue until the user approves them in the Agents tab.
        services.AddSingleton<ApprovalQueue>();
        services.AddSingleton<IChangeApprovalService>(sp => sp.GetRequiredService<ApprovalQueue>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<AppState>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddTransient<ImportWorkflow>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<NewModViewModel>();
        services.AddTransient<RepairViewModel>();
        services.AddTransient<MyModsViewModel>();
        services.AddTransient<BatchScannerViewModel>();
        services.AddTransient<GameProfilesViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<AIModelsViewModel>();
        services.AddTransient<AgentsViewModel>();
        services.AddTransient<ProjectViewModel>();
        return services.BuildServiceProvider();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Fail gracefully: report and keep the application running.
        Log("Unhandled UI exception", e.Exception);
        MessageBox.Show($"Something went wrong:\n\n{SecretRedactor.Redact(e.Exception.Message)}\n\nDetails were written to the log folder.",
            "Mad Mod Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
