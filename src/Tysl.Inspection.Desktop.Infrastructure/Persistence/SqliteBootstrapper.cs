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
                syncedAt TEXT NOT NULL,
                parentGroupId TEXT NULL,
                regionCode TEXT NOT NULL DEFAULT '',
                level INTEGER NOT NULL DEFAULT 1,
                hasChildren INTEGER NOT NULL DEFAULT 0,
                hasDevice INTEGER NOT NULL DEFAULT 0,
                regionGbId TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Device (
                deviceCode TEXT PRIMARY KEY,
                deviceName TEXT NOT NULL,
                groupId TEXT NOT NULL,
                latitude TEXT NULL,
                longitude TEXT NULL,
                rawLatitude TEXT NULL,
                rawLongitude TEXT NULL,
                location TEXT NULL,
                coordinateSource TEXT NOT NULL DEFAULT '',
                coordinateStatus TEXT NOT NULL DEFAULT '',
                coordinateStatusMessage TEXT NOT NULL DEFAULT '',
                onlineStatus INTEGER NULL,
                cloudStatus INTEGER NULL,
                bandStatus INTEGER NULL,
                sourceTypeFlag INTEGER NULL,
                syncedAt TEXT NOT NULL,
                FOREIGN KEY (groupId) REFERENCES "Group"(groupId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SyncMetadata (
                id INTEGER NOT NULL PRIMARY KEY CHECK(id = 1),
                platformGroupCount INTEGER NOT NULL DEFAULT 0,
                platformDeviceCount INTEGER NOT NULL DEFAULT 0,
                reconciliationCompleted INTEGER NOT NULL DEFAULT 0,
                reconciliationMatched INTEGER NOT NULL DEFAULT 0,
                reconciledRegionCount INTEGER NOT NULL DEFAULT 0,
                reconciledDeviceCount INTEGER NOT NULL DEFAULT 0,
                reconciledOnlineCount INTEGER NOT NULL DEFAULT 0,
                reconciliationScopeText TEXT NOT NULL DEFAULT '',
                syncedAt TEXT NULL
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

            CREATE TABLE IF NOT EXISTS DeviceMaintenance (
                deviceCode TEXT NOT NULL PRIMARY KEY,
                maintenanceStatus TEXT NOT NULL DEFAULT '',
                maintenanceNote TEXT NOT NULL DEFAULT '',
                manualConfirmationNote TEXT NOT NULL DEFAULT '',
                updatedAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Device_GroupId ON Device(groupId);
            CREATE INDEX IF NOT EXISTS IX_InspectAbnormalPool_InspectAt ON InspectAbnormalPool(inspectAt DESC);
            CREATE INDEX IF NOT EXISTS IX_DeviceMaintenance_UpdatedAt ON DeviceMaintenance(updatedAt DESC);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await EnsureGroupColumnsAsync(connection, cancellationToken);
        await EnsureDeviceColumnsAsync(connection, cancellationToken);
        await EnsureSyncMetadataAsync(connection, cancellationToken);
        await EnsureInspectAbnormalPoolColumnsAsync(connection, cancellationToken);
        await EnsureIndexesAsync(connection, cancellationToken);

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

    private static async Task EnsureGroupColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await LoadColumnsAsync(connection, "Group", cancellationToken);

        if (!columns.Contains("parentGroupId"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN parentGroupId TEXT NULL;""",
                cancellationToken);
        }

        if (!columns.Contains("regionCode"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN regionCode TEXT NOT NULL DEFAULT '';""",
                cancellationToken);
        }

        if (!columns.Contains("level"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN level INTEGER NOT NULL DEFAULT 1;""",
                cancellationToken);
        }

        if (!columns.Contains("hasChildren"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN hasChildren INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken);
        }

        if (!columns.Contains("hasDevice"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN hasDevice INTEGER NOT NULL DEFAULT 0;""",
                cancellationToken);
        }

        if (!columns.Contains("regionGbId"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE "Group" ADD COLUMN regionGbId TEXT NULL;""",
                cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE "Group"
            SET regionCode = CASE
                WHEN regionCode IS NULL OR TRIM(regionCode) = '' THEN groupId
                ELSE regionCode
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE "Group"
            SET level = CASE
                WHEN level IS NULL OR level <= 0 THEN 1
                ELSE level
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE "Group"
            SET hasChildren = CASE
                WHEN hasChildren = 1 THEN 1
                ELSE 0
            END;
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE "Group"
            SET hasDevice = CASE
                WHEN hasDevice = 1 THEN 1
                WHEN deviceCount > 0 THEN 1
                ELSE 0
            END;
            """,
            cancellationToken);
    }

    private static async Task EnsureSyncMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            INSERT OR IGNORE INTO SyncMetadata(
                id,
                platformGroupCount,
                platformDeviceCount,
                reconciliationCompleted,
                reconciliationMatched,
                reconciledRegionCount,
                reconciledDeviceCount,
                reconciledOnlineCount,
                reconciliationScopeText,
                syncedAt)
            VALUES(1, 0, 0, 0, 0, 0, 0, 0, '', NULL);
            """,
            cancellationToken);
        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE SyncMetadata
            SET reconciliationScopeText = CASE
                WHEN reconciliationScopeText IS NULL THEN ''
                ELSE reconciliationScopeText
            END
            WHERE id = 1;
            """,
            cancellationToken);
    }

    private static async Task EnsureDeviceColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await LoadColumnsAsync(connection, "Device", cancellationToken);

        if (!columns.Contains("rawLatitude"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE Device ADD COLUMN rawLatitude TEXT NULL;""",
                cancellationToken);
        }

        if (!columns.Contains("rawLongitude"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE Device ADD COLUMN rawLongitude TEXT NULL;""",
                cancellationToken);
        }

        if (!columns.Contains("coordinateSource"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE Device ADD COLUMN coordinateSource TEXT NOT NULL DEFAULT '';""",
                cancellationToken);
        }

        if (!columns.Contains("coordinateStatus"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE Device ADD COLUMN coordinateStatus TEXT NOT NULL DEFAULT '';""",
                cancellationToken);
        }

        if (!columns.Contains("coordinateStatusMessage"))
        {
            await ExecuteNonQueryAsync(
                connection,
                """ALTER TABLE Device ADD COLUMN coordinateStatusMessage TEXT NOT NULL DEFAULT '';""",
                cancellationToken);
        }

        await ExecuteNonQueryAsync(
            connection,
            """
            UPDATE Device
            SET coordinateSource = CASE
                WHEN coordinateSource IS NULL THEN ''
                ELSE coordinateSource
            END,
                coordinateStatus = CASE
                    WHEN coordinateStatus IS NULL THEN ''
                    ELSE coordinateStatus
                END,
                coordinateStatusMessage = CASE
                    WHEN coordinateStatusMessage IS NULL THEN ''
                    ELSE coordinateStatusMessage
                END;
            """,
            cancellationToken);
    }

    private static async Task EnsureIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """CREATE INDEX IF NOT EXISTS IX_Group_ParentGroupId ON "Group"(parentGroupId);""",
            cancellationToken);
    }

    private static async Task EnsureInspectAbnormalPoolColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = await LoadColumnsAsync(connection, "InspectAbnormalPool", cancellationToken);

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

    private static async Task<HashSet<string>> LoadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""PRAGMA table_info("{tableName}");""";

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
