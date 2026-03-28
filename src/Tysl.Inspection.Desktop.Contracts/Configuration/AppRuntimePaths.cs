namespace Tysl.Inspection.Desktop.Contracts.Configuration;

public sealed record AppRuntimePaths(
    string RootPath,
    string LogsPath,
    string DataPath,
    string DatabasePath,
    string TokenCachePath);
