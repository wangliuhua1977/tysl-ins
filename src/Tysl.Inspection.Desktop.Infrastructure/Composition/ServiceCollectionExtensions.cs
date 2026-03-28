using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;
using Tysl.Inspection.Desktop.Infrastructure.Persistence;

namespace Tysl.Inspection.Desktop.Infrastructure.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        AppRuntimePaths runtimePaths)
    {
        services.Configure<TianyiOpenPlatformOptions>(configuration.GetSection("Tianyi"));
        services.Configure<DatabaseOptions>(configuration.GetSection("Database"));
        services.AddSingleton(runtimePaths);

        services.AddSingleton<ISqliteBootstrapper, SqliteBootstrapper>();
        services.AddSingleton<IGroupSyncStore, SqliteGroupSyncStore>();
        services.AddSingleton<IGroupSyncService, GroupSyncService>();
        services.AddSingleton<IOverviewStatsService, OverviewStatsService>();

        var openPlatformOptions = configuration.GetSection("Tianyi").Get<TianyiOpenPlatformOptions>() ?? new TianyiOpenPlatformOptions();
        if (openPlatformOptions.HasRequiredSecrets())
        {
            services.AddSingleton<IOpenPlatformClient, OpenPlatformClient>();
        }
        else
        {
            services.AddSingleton<IOpenPlatformClient, FakeOpenPlatformClient>();
        }

        return services;
    }
}
