using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Infrastructure.Persistence;

public sealed class SqliteGroupSyncStore(
    IOptions<DatabaseOptions> databaseOptions,
    AppRuntimePaths runtimePaths) : IGroupSyncStore
{
    public async Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteMissing = connection.CreateCommand())
        {
            deleteMissing.Transaction = transaction;
            if (groups.Count == 0)
            {
                deleteMissing.CommandText = """DELETE FROM "Group";""";
            }
            else
            {
                var placeholders = new List<string>();
                var index = 0;
                foreach (var group in groups)
                {
                    var parameterName = $"@groupId{index++}";
                    placeholders.Add(parameterName);
                    deleteMissing.Parameters.AddWithValue(parameterName, group.GroupId);
                }

                deleteMissing.CommandText = $"""DELETE FROM "Group" WHERE groupId NOT IN ({string.Join(",", placeholders)});""";
            }

            await deleteMissing.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var group in groups)
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO "Group"(groupId, groupName, deviceCount, syncedAt)
                VALUES(@groupId, @groupName, @deviceCount, @syncedAt)
                ON CONFLICT(groupId) DO UPDATE SET
                    groupName = excluded.groupName,
                    deviceCount = excluded.deviceCount,
                    syncedAt = excluded.syncedAt;
                """;
            upsert.Parameters.AddWithValue("@groupId", group.GroupId);
            upsert.Parameters.AddWithValue("@groupName", group.GroupName);
            upsert.Parameters.AddWithValue("@deviceCount", group.DeviceCount);
            upsert.Parameters.AddWithValue("@syncedAt", group.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceSnapshotAsync(
        IReadOnlyCollection<InspectionGroup> groups,
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteDevices = connection.CreateCommand())
        {
            deleteDevices.Transaction = transaction;
            deleteDevices.CommandText = "DELETE FROM Device;";
            await deleteDevices.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteMissingGroups = connection.CreateCommand())
        {
            deleteMissingGroups.Transaction = transaction;
            if (groups.Count == 0)
            {
                deleteMissingGroups.CommandText = """DELETE FROM "Group";""";
            }
            else
            {
                var placeholders = new List<string>();
                var index = 0;
                foreach (var group in groups)
                {
                    var parameterName = $"@groupId{index++}";
                    placeholders.Add(parameterName);
                    deleteMissingGroups.Parameters.AddWithValue(parameterName, group.GroupId);
                }

                deleteMissingGroups.CommandText = $"""DELETE FROM "Group" WHERE groupId NOT IN ({string.Join(",", placeholders)});""";
            }

            await deleteMissingGroups.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var group in groups)
        {
            await using var upsertGroup = connection.CreateCommand();
            upsertGroup.Transaction = transaction;
            upsertGroup.CommandText = """
                INSERT INTO "Group"(groupId, groupName, deviceCount, syncedAt)
                VALUES(@groupId, @groupName, @deviceCount, @syncedAt)
                ON CONFLICT(groupId) DO UPDATE SET
                    groupName = excluded.groupName,
                    deviceCount = excluded.deviceCount,
                    syncedAt = excluded.syncedAt;
                """;
            upsertGroup.Parameters.AddWithValue("@groupId", group.GroupId);
            upsertGroup.Parameters.AddWithValue("@groupName", group.GroupName);
            upsertGroup.Parameters.AddWithValue("@deviceCount", group.DeviceCount);
            upsertGroup.Parameters.AddWithValue("@syncedAt", group.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
            await upsertGroup.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var device in devices)
        {
            await using var insertDevice = connection.CreateCommand();
            insertDevice.Transaction = transaction;
            insertDevice.CommandText = """
                INSERT INTO Device(
                    deviceCode,
                    deviceName,
                    groupId,
                    latitude,
                    longitude,
                    location,
                    onlineStatus,
                    cloudStatus,
                    bandStatus,
                    sourceTypeFlag,
                    syncedAt)
                VALUES(
                    @deviceCode,
                    @deviceName,
                    @groupId,
                    @latitude,
                    @longitude,
                    @location,
                    @onlineStatus,
                    @cloudStatus,
                    @bandStatus,
                    @sourceTypeFlag,
                    @syncedAt);
                """;
            insertDevice.Parameters.AddWithValue("@deviceCode", device.DeviceCode);
            insertDevice.Parameters.AddWithValue("@deviceName", device.DeviceName);
            insertDevice.Parameters.AddWithValue("@groupId", device.GroupId);
            insertDevice.Parameters.AddWithValue("@latitude", (object?)device.Latitude ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@longitude", (object?)device.Longitude ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@location", (object?)device.Location ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@onlineStatus", (object?)device.OnlineStatus ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@cloudStatus", (object?)device.CloudStatus ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@bandStatus", (object?)device.BandStatus ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@sourceTypeFlag", (object?)device.SourceTypeFlag ?? DBNull.Value);
            insertDevice.Parameters.AddWithValue("@syncedAt", device.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
            await insertDevice.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Device
            WHERE groupId NOT IN (SELECT groupId FROM "Group");
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceDevicesForGroupAsync(
        string groupId,
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """DELETE FROM Device WHERE groupId = @groupId;""";
            delete.Parameters.AddWithValue("@groupId", groupId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var device in devices)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Device(
                    deviceCode,
                    deviceName,
                    groupId,
                    latitude,
                    longitude,
                    location,
                    onlineStatus,
                    cloudStatus,
                    bandStatus,
                    sourceTypeFlag,
                    syncedAt)
                VALUES(
                    @deviceCode,
                    @deviceName,
                    @groupId,
                    @latitude,
                    @longitude,
                    @location,
                    @onlineStatus,
                    @cloudStatus,
                    @bandStatus,
                    @sourceTypeFlag,
                    @syncedAt);
                """;
            insert.Parameters.AddWithValue("@deviceCode", device.DeviceCode);
            insert.Parameters.AddWithValue("@deviceName", device.DeviceName);
            insert.Parameters.AddWithValue("@groupId", device.GroupId);
            insert.Parameters.AddWithValue("@latitude", (object?)device.Latitude ?? DBNull.Value);
            insert.Parameters.AddWithValue("@longitude", (object?)device.Longitude ?? DBNull.Value);
            insert.Parameters.AddWithValue("@location", (object?)device.Location ?? DBNull.Value);
            insert.Parameters.AddWithValue("@onlineStatus", (object?)device.OnlineStatus ?? DBNull.Value);
            insert.Parameters.AddWithValue("@cloudStatus", (object?)device.CloudStatus ?? DBNull.Value);
            insert.Parameters.AddWithValue("@bandStatus", (object?)device.BandStatus ?? DBNull.Value);
            insert.Parameters.AddWithValue("@sourceTypeFlag", (object?)device.SourceTypeFlag ?? DBNull.Value);
            insert.Parameters.AddWithValue("@syncedAt", device.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var totalPoints = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM Device;", cancellationToken);
        var onlineCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM Device WHERE onlineStatus = 1;", cancellationToken);
        var offlineCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM Device WHERE onlineStatus = 0;", cancellationToken);
        var unlocatedCount = await ExecuteScalarIntAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM Device
            WHERE TRIM(IFNULL(latitude, '')) = ''
               OR TRIM(IFNULL(longitude, '')) = '';
            """,
            cancellationToken);

        var lastSyncedAt = await GetLastSyncedAtAsync(connection, cancellationToken);
        return new OverviewStats(totalPoints, onlineCount, offlineCount, unlocatedCount, lastSyncedAt);
    }

    public async Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                groupId,
                groupName,
                deviceCount,
                syncedAt
            FROM "Group"
            ORDER BY groupName COLLATE NOCASE, groupId;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<InspectionGroup>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new InspectionGroup(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadInt32(reader, 2),
                ReadSyncedAt(reader, 3)));
        }

        return groups;
    }

    public async Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                deviceCode,
                deviceName,
                groupId,
                latitude,
                longitude,
                location,
                onlineStatus,
                cloudStatus,
                bandStatus,
                sourceTypeFlag,
                syncedAt
            FROM Device
            ORDER BY deviceName COLLATE NOCASE, deviceCode;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var devices = new List<InspectionDevice>();
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new InspectionDevice(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableInt32(reader, 6),
                ReadNullableInt32(reader, 7),
                ReadNullableInt32(reader, 8),
                ReadNullableInt32(reader, 9),
                ReadSyncedAt(reader, 10)));
        }

        return devices;
    }

    public async Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var groupCount = await ExecuteScalarIntAsync(connection, """SELECT COUNT(*) FROM "Group";""", cancellationToken);
        var deviceCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM Device;", cancellationToken);
        var lastSyncedAt = await GetLastSyncedAtAsync(connection, cancellationToken);
        return new LocalSyncSnapshot(groupCount, deviceCount, lastSyncedAt);
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(SqliteBootstrapper.BuildConnectionString(databaseOptions.Value, runtimePaths));
    }

    private static async Task<int> ExecuteScalarIntAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> GetLastSyncedAtAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(syncedAt)
            FROM (
                SELECT syncedAt FROM "Group"
                UNION ALL
                SELECT syncedAt FROM Device
            );
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            return null;
        }

        return DateTimeOffset.TryParse(result.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string ReadString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return 0;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => Convert.ToInt32(longValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => Convert.ToInt32(longValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static DateTimeOffset ReadSyncedAt(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return DateTimeOffset.UtcNow;
        }

        var text = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }
}
