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
        services.AddSingleton(CreateMapOptions(configuration));
        services.AddSingleton(runtimePaths);

        services.AddSingleton<ISqliteBootstrapper, SqliteBootstrapper>();
        services.AddSingleton<IGroupSyncStore, SqliteGroupSyncStore>();
        services.AddSingleton<IMapStore, SqliteMapStore>();
        services.AddSingleton<IGroupSyncService, GroupSyncService>();
        services.AddSingleton<IOverviewStatsService, OverviewStatsService>();
        services.AddSingleton<IMapService, MapService>();

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

    private static MapOptions CreateMapOptions(IConfiguration configuration)
    {
        var amap = ReadMapSection(configuration.GetSection("Amap"));
        if (amap.HasJsKey())
        {
            return amap;
        }

        var mapProvider = ReadMapSection(configuration.GetSection("MapProvider"));
        return mapProvider.HasJsKey() ? mapProvider : amap;
    }

    private static MapOptions ReadMapSection(IConfigurationSection section)
    {
        return new MapOptions
        {
            JsKey = ReadFirst(section, "JsKey", "AmapWebJsApiKey"),
            SecurityJsCode = ReadFirst(section, "SecurityJsCode", "AmapSecurityJsCode"),
            JsApiVersion = ReadFirstOrDefault(section, "2.0", "JsApiVersion", "AmapJsApiVersion"),
            CoordinateSystem = ReadFirst(section, "CoordinateSystem")
        };
    }

    private static string ReadFirst(IConfigurationSection section, params string[] names)
    {
        foreach (var name in names)
        {
            var value = section[name];
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadFirstOrDefault(IConfigurationSection section, string defaultValue, params string[] names)
    {
        var value = ReadFirst(section, names);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}
