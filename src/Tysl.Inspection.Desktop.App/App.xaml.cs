using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.App.ViewModels;
using Tysl.Inspection.Desktop.App.Views;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Infrastructure.Composition;
using Tysl.Inspection.Desktop.Infrastructure.Support;

namespace Tysl.Inspection.Desktop.App;

public partial class App : System.Windows.Application
{
    private IHost? host;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var runtimePaths = AppRuntimePathResolver.Resolve();
        ResetLogs(runtimePaths.LogsPath);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(runtimePaths.LogsPath, "desktop.log"),
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        host = BuildHost(runtimePaths);
        await host.StartAsync();

        var logger = host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Application startup.");

        var bootstrapper = host.Services.GetRequiredService<ISqliteBootstrapper>();
        await bootstrapper.InitializeAsync(CancellationToken.None);

        if (e.Args.Contains("--sync-once", StringComparer.OrdinalIgnoreCase))
        {
            var syncService = host.Services.GetRequiredService<IGroupSyncService>();
            var summary = await syncService.SyncAsync(CancellationToken.None);
            Console.WriteLine(
                "SYNC SUMMARY groups={0} devices={1} success={2} failure={3} lastSyncedAt={4}",
                summary.GroupCount,
                summary.DeviceCount,
                summary.SuccessCount,
                summary.FailureCount,
                summary.LastSyncedAt?.ToString("O") ?? "null");
            foreach (var failure in summary.Failures)
            {
                Console.WriteLine(
                    "SYNC FAILURE kind={0} groupId={1} groupName={2} message={3}",
                    failure.FailureKind,
                    failure.GroupId ?? string.Empty,
                    failure.GroupName ?? string.Empty,
                    failure.Message);
            }
            logger.LogInformation(
                "Headless sync completed. Groups={GroupCount}, Devices={DeviceCount}, Success={SuccessCount}, Failure={FailureCount}, LastSyncedAt={LastSyncedAt}.",
                summary.GroupCount,
                summary.DeviceCount,
                summary.SuccessCount,
                summary.FailureCount,
                summary.LastSyncedAt);
            Shutdown();
            return;
        }

        var shellWindow = host.Services.GetRequiredService<ShellWindow>();
        shellWindow.DataContext = host.Services.GetRequiredService<ShellWindowViewModel>();
        MainWindow = shellWindow;
        shellWindow.Show();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (host is not null)
        {
            var logger = host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application shutdown.");
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IHost BuildHost(AppRuntimePaths runtimePaths)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.SetBasePath(runtimePaths.RootPath);
                configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
                configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
            })
            .UseSerilog()
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration, runtimePaths);

                services.AddSingleton<ShellWindow>();
                services.AddSingleton<ShellWindowViewModel>();
                services.AddSingleton<OverviewPageViewModel>();
                services.AddSingleton<BasicConfigurationPageViewModel>();
                services.AddSingleton<MapPageViewModel>();
                services.AddSingleton<ThemeSettingsPageViewModel>();
            })
            .Build();
    }

    private static void ResetLogs(string logsPath)
    {
        Directory.CreateDirectory(logsPath);
        foreach (var file in Directory.GetFiles(logsPath, "*.log", SearchOption.TopDirectoryOnly))
        {
            File.Delete(file);
        }
    }
}
