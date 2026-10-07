using System.Windows;
using MadModStudio.AI;
using MadModStudio.App.ViewModels;
using MadModStudio.App.Views;
using MadModStudio.Core;
using MadModStudio.Core.Models;
using MadModStudio.Game7DTD.Build;
using MadModStudio.Game7DTD.Install;
using MadModStudio.Game7DTD.Projects;
using MadModStudio.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace MadModStudio.App.Tests;

/// <summary>The real composition root against a temporary data folder, a synthetic game and one imported, built mod.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public ServiceProvider Services { get; private set; } = null!;
    public ModProject Project { get; private set; } = null!;
    public GameProfile Profile { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Services = App.BuildServices(new AppPaths(FakeGame.TempDir("uiapp")), fileLogging: false);
        var game = Path.Combine(FakeGame.TempDir("uigame"), "game");
        FakeGame.Create(game);
        var profiles = Services.GetRequiredService<GameProfileService>();
        Profile = (await profiles.CreateProfileAsync(game)).Profile!;
        await profiles.ReindexAsync(Profile);
        Project = Assert.Single(await Services.GetRequiredService<ProjectService>().ImportAsync(SampleMod.WriteZip(FakeGame.TempDir("uiinbox")), Profile.Id)).Project;
        Assert.True((await Services.GetRequiredService<ModBuildPipeline>().RunAsync(Project, new BuildOptions())).Succeeded);
    }

    public Task DisposeAsync()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Creates every screen with its real view model and lays it out (all tabs selected), failing on XAML errors such as a
/// missing resource key and on WPF data-binding errors such as a misspelled property path.
/// </summary>
public class UiSmokeTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;
    public UiSmokeTests(AppFixture app) => _app = app;

    /// <summary>The main window's content (navigation, page host, status bar) rendered with the window's page templates.</summary>
    private static FrameworkElement Chrome(MainViewModel main)
    {
        var window = new MainWindow { DataContext = main };
        var chrome = (FrameworkElement)window.Content;
        window.Content = null;
        chrome.Resources.MergedDictionaries.Add(window.Resources);
        chrome.DataContext = main;
        return chrome;
    }

    private static void AssertNoBindingErrors()
    {
        var errors = UiThread.BindingErrors.Take();
        Assert.True(errors.Count == 0, "WPF binding errors:\n" + string.Join("\n", errors.Distinct()));
    }

    [Theory]
    [InlineData("home")]
    [InlineData("newmod")]
    [InlineData("repair")]
    [InlineData("mymods")]
    [InlineData("scanner")]
    [InlineData("aimodels")]
    [InlineData("profiles")]
    [InlineData("settings")]
    public async Task Every_page_renders_without_xaml_or_binding_errors(string page)
    {
        await UiThread.RunAsync(async () =>
        {
            UiThread.BindingErrors.Take();
            var main = _app.Services.GetRequiredService<MainViewModel>();
            await main.InitializeAsync();
            await main.NavigateAsync(page);
            Assert.NotNull(main.CurrentPage);
            Assert.Null(main.CurrentPage!.ErrorMessage);
            UiThread.Render(Chrome(main));
        });
        AssertNoBindingErrors();
    }

    [Fact]
    public async Task Project_screen_with_agents_tab_renders_with_real_data()
    {
        await UiThread.RunAsync(async () =>
        {
            UiThread.BindingErrors.Take();
            var main = _app.Services.GetRequiredService<MainViewModel>();
            await main.OpenProjectAsync(_app.Project.Id);
            var project = Assert.IsType<ProjectViewModel>(main.CurrentPage);
            Assert.Null(project.ErrorMessage);
            Assert.NotEmpty(project.FileTree);
            Assert.Equal(11, project.Agents.Team.Count);
            Assert.Contains("NOT CONFIGURED", project.Agents.AiAvailability); // no provider configured in tests
            UiThread.Render(Chrome(main));
        });
        AssertNoBindingErrors();
    }

    [Fact]
    public async Task Batch_scanner_renders_scan_results_safe_fixes_and_conflicts()
    {
        await UiThread.RunAsync(async () =>
        {
            UiThread.BindingErrors.Take();
            var main = _app.Services.GetRequiredService<MainViewModel>();
            await main.InitializeAsync();
            await main.NavigateAsync("scanner");
            var scanner = Assert.IsType<BatchScannerViewModel>(main.CurrentPage);
            scanner.ModsFolder = _app.Profile.ModsPath!;
            await scanner.ScanCommand.ExecuteAsync(null);
            Assert.Null(scanner.ErrorMessage);
            Assert.NotEmpty(scanner.Rows);
            scanner.Selected = scanner.Rows[0];
            UiThread.Render(Chrome(main));
        });
        AssertNoBindingErrors();
    }
}
