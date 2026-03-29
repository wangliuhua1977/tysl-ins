using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;

namespace Tysl.Inspection.Desktop.Infrastructure.Persistence;

public sealed class SqliteBootstrapper(
    IOptions<DatabaseOptions> databaseOptions,
    AppRuntimePaths runtimePaths,
    ILogger<SqliteBootstrapper> logger) : ISqliteBootstrapper
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(runtimePaths.DataPath);

        await using var connection = new SqliteConnection(BuildConnectionString(databaseOptions.Value, runtimePaths));
        await connection.OpenAsync(cancellationToken);

        var commandText = """
            CREATE TABLE IF NOT EXISTS "Group" (
                groupId TEXT PRIMARY KEY,
                groupName TEXT NOT NULL,
                deviceCount INTEGER NOT NULL DEFAULT 0,
                syncedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Device (
                deviceCode TEXT PRIMARY KEY,
                deviceName TEXT NOT NULL,
                groupId TEXT NOT NULL,
                latitude TEXT NULL,
                longitude TEXT NULL,
                location TEXT NULL,
                onlineStatus INTEGER NULL,
                cloudStatus INTEGER NULL,
                bandStatus INTEGER NULL,
                sourceTypeFlag INTEGER NULL,
                syncedAt TEXT NOT NULL,
                FOREIGN KEY (groupId) REFERENCES "Group"(groupId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS InspectAbnormalPool (
                id TEXT NOT NULL PRIMARY KEY,
                deviceCode TEXT NOT NULL,
                deviceName TEXT NOT NULL,
                inspectAt TEXT NOT NULL,
                abnormalClass INTEGER NOT NULL,
                abnormalClassText TEXT NOT NULL,
                conclusion TEXT NOT NULL,
                failureCategory TEXT NOT NULL DEFAULT '',
                dispositionSummary TEXT NOT NULL,
                isReviewed INTEGER NOT NULL DEFAULT 0,
                updatedAt TEXT NOT NULL,
                UNIQUE(deviceCode, abnormalClass, conclusion)
            );

            CREATE INDEX IF NOT EXISTS IX_Device_GroupId ON Device(groupId);
            CREATE INDEX IF NOT EXISTS IX_InspectAbnormalPool_InspectAt ON InspectAbnormalPool(inspectAt DESC);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);

        logger.LogInformation("SQLite schema initialized at {DatabasePath}.", ResolveDatabasePath(databaseOptions.Value, runtimePaths));
    }

    internal static string BuildConnectionString(DatabaseOptions options, AppRuntimePaths runtimePaths)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(options, runtimePaths)
        };

        return builder.ToString();
    }

    internal static string ResolveDatabasePath(DatabaseOptions options, AppRuntimePaths runtimePaths)
    {
        return Path.IsPathRooted(options.Path)
            ? options.Path
            : Path.Combine(runtimePaths.RootPath, options.Path);
    }
}
