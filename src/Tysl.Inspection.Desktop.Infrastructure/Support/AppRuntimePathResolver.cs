using Tysl.Inspection.Desktop.Contracts.Configuration;

namespace Tysl.Inspection.Desktop.Infrastructure.Support;

public static class AppRuntimePathResolver
{
    public static AppRuntimePaths Resolve()
    {
        var rootPath = ResolveRootPath();
        var logsPath = Path.Combine(rootPath, "logs");
        var dataPath = Path.Combine(rootPath, "data");
        var databasePath = Path.Combine(dataPath, "inspection.db");
        var tokenCachePath = Path.Combine(dataPath, "open-platform-token-cache.json");

        return new AppRuntimePaths(rootPath, logsPath, dataPath, databasePath, tokenCachePath);
    }

    private static string ResolveRootPath()
    {
        foreach (var startPath in EnumerateCandidates())
        {
            var current = new DirectoryInfo(startPath);
            while (current is not null)
            {
                var appsettingsPath = Path.Combine(current.FullName, "appsettings.json");
                var agentsPath = Path.Combine(current.FullName, "AGENTS.md");
                if (File.Exists(appsettingsPath) && File.Exists(agentsPath))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;
    }
}
