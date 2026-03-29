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
                isRecoveredConfirmed INTEGER NOT NULL DEFAULT 0,
                recoveredConfirmedAt TEXT NULL,
                recoveredSummary TEXT NOT NULL DEFAULT '',
                handleStatus INTEGER NOT NULL DEFAULT 1,
                handleStatusText TEXT NOT NULL DEFAULT '待处理',
                handleUpdatedAt TEXT NOT NULL DEFAULT '',
                updatedAt TEXT NOT NULL,
                UNIQUE(deviceCode, abnormalClass, conclusion)
            );

            CREATE INDEX IF NOT EXISTS IX_Device_GroupId ON Device(groupId);
            CREATE INDEX IF NOT EXISTS IX_InspectAbnormalPool_InspectAt ON InspectAbnormalPool(inspectAt DESC);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureInspectAbnormalPoolColumnsAsync(connection, cancellationToken);

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

    private static async Task EnsureInspectAbnormalPoolColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await LoadInspectAbnormalPoolColumnsAsync(connection, cancellationToken);

        if (!columns.Contains("handleStatus"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN handleStatus INTEGER NOT NULL DEFAULT 1;",
                cancellationToken);
        }

        if (!columns.Contains("handleStatusText"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN handleStatusText TEXT NOT NULL DEFAULT '待处理';",
                cancellationToken);
        }

        if (!columns.Contains("handleUpdatedAt"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN handleUpdatedAt TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        if (!columns.Contains("isRecoveredConfirmed"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN isRecoveredConfirmed INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        if (!columns.Contains("recoveredConfirmedAt"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN recoveredConfirmedAt TEXT NULL;",
                cancellationToken);
        }

        if (!columns.Contains("recoveredSummary"))
        {
            await ExecuteNonQueryAsync(
                connection,
                "ALTER TABLE InspectAbnormalPool ADD COLUMN recoveredSummary TEXT NOT NULL DEFAULT '';",
                cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE InspectAbnormalPool
            SET handleStatus = CASE
                WHEN handleStatus IN (1, 2, 3) THEN handleStatus
                ELSE 1
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE InspectAbnormalPool
            SET handleStatusText = CASE handleStatus
                WHEN 1 THEN '待处理'
                WHEN 2 THEN '处理中'
                WHEN 3 THEN '已处理'
                ELSE '待处理'
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE InspectAbnormalPool
            SET handleUpdatedAt = CASE
                WHEN handleUpdatedAt IS NULL OR handleUpdatedAt = '' THEN updatedAt
                ELSE handleUpdatedAt
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE InspectAbnormalPool
            SET isRecoveredConfirmed = CASE
                WHEN isRecoveredConfirmed = 1 THEN 1
                ELSE 0
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE InspectAbnormalPool
            SET recoveredSummary = CASE
                WHEN recoveredSummary IS NULL THEN ''
                ELSE recoveredSummary
            END;
            """,
            cancellationToken);
    }

    private static async Task<HashSet<string>> LoadInspectAbnormalPoolColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(InspectAbnormalPool);";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
