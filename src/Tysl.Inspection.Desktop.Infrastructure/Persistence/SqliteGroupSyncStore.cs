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
            await UpsertGroupAsync(connection, transaction, group, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceSnapshotAsync(
        IReadOnlyCollection<InspectionGroup> groups,
        IReadOnlyCollection<InspectionDevice> devices,
        GroupSyncSnapshotMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existingDevices = await LoadExistingDeviceMapAsync(connection, transaction, cancellationToken);

        await using (var deleteDevices = connection.CreateCommand())
        {
            deleteDevices.Transaction = transaction;
            deleteDevices.CommandText = "DELETE FROM Device;";
            await deleteDevices.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteGroups = connection.CreateCommand())
        {
            deleteGroups.Transaction = transaction;
            deleteGroups.CommandText = """DELETE FROM "Group";""";
            await deleteGroups.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var group in groups)
        {
            await UpsertGroupAsync(connection, transaction, group, cancellationToken);
        }

        foreach (var device in devices)
        {
            var merged = MergeDevice(device, existingDevices);
            await InsertDeviceAsync(connection, transaction, merged, cancellationToken);
        }

        await UpsertMetadataAsync(
            connection,
            transaction,
            metadata,
            GetSnapshotSyncedAt(groups, devices),
            cancellationToken);

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

        var existingDevices = await LoadExistingDeviceMapAsync(connection, transaction, cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """DELETE FROM Device WHERE groupId = @groupId;""";
            delete.Parameters.AddWithValue("@groupId", groupId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var device in devices)
        {
            var merged = MergeDevice(device, existingDevices);
            await InsertDeviceAsync(connection, transaction, merged, cancellationToken);
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
            WHERE TRIM(IFNULL(rawLatitude, '')) = ''
               OR TRIM(IFNULL(rawLongitude, '')) = '';
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
                parentGroupId,
                regionCode,
                deviceCount,
                level,
                hasChildren,
                hasDevice,
                regionGbId,
                syncedAt
            FROM "Group"
            ORDER BY level, groupName COLLATE NOCASE, groupId;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<InspectionGroup>();
        while (await reader.ReadAsync(cancellationToken))
        {
            groups.Add(new InspectionGroup(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadNullableString(reader, 2),
                ReadString(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadBoolean(reader, 6),
                ReadBoolean(reader, 7),
                ReadNullableString(reader, 8),
                ReadSyncedAt(reader, 9)));
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
                rawLatitude,
                rawLongitude,
                location,
                coordinateSource,
                coordinateStatus,
                coordinateStatusMessage,
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
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public async Task<IReadOnlyDictionary<string, DeviceUserMaintenance>> GetDeviceMaintenanceMapAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                deviceCode,
                maintenanceStatus,
                maintenanceNote,
                manualConfirmationNote,
                updatedAt
            FROM DeviceMaintenance
            ORDER BY updatedAt DESC, deviceCode;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new Dictionary<string, DeviceUserMaintenance>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new DeviceUserMaintenance(
                ReadString(reader, 0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadSyncedAt(reader, 4));
            items[item.DeviceCode] = item;
        }

        return items;
    }

    public async Task SaveDeviceMaintenanceAsync(DeviceUserMaintenance maintenance, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DeviceMaintenance(
                deviceCode,
                maintenanceStatus,
                maintenanceNote,
                manualConfirmationNote,
                updatedAt)
            VALUES(
                @deviceCode,
                @maintenanceStatus,
                @maintenanceNote,
                @manualConfirmationNote,
                @updatedAt)
            ON CONFLICT(deviceCode) DO UPDATE SET
                maintenanceStatus = excluded.maintenanceStatus,
                maintenanceNote = excluded.maintenanceNote,
                manualConfirmationNote = excluded.manualConfirmationNote,
                updatedAt = excluded.updatedAt;
            """;
        command.Parameters.AddWithValue("@deviceCode", maintenance.DeviceCode);
        command.Parameters.AddWithValue("@maintenanceStatus", maintenance.MaintenanceStatus);
        command.Parameters.AddWithValue("@maintenanceNote", maintenance.MaintenanceNote);
        command.Parameters.AddWithValue("@manualConfirmationNote", maintenance.ManualConfirmationNote);
        command.Parameters.AddWithValue("@updatedAt", maintenance.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateDevicePlatformDataAsync(
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return;
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var device in devices)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Device
                SET deviceName = @deviceName,
                    latitude = @latitude,
                    longitude = @longitude,
                    rawLatitude = @rawLatitude,
                    rawLongitude = @rawLongitude,
                    location = @location,
                    coordinateSource = @coordinateSource,
                    coordinateStatus = @coordinateStatus,
                    coordinateStatusMessage = @coordinateStatusMessage,
                    syncedAt = @syncedAt
                WHERE deviceCode = @deviceCode;
                """;
            update.Parameters.AddWithValue("@deviceCode", device.DeviceCode);
            update.Parameters.AddWithValue("@deviceName", device.DeviceName);
            update.Parameters.AddWithValue("@latitude", (object?)device.Latitude ?? DBNull.Value);
            update.Parameters.AddWithValue("@longitude", (object?)device.Longitude ?? DBNull.Value);
            update.Parameters.AddWithValue("@rawLatitude", (object?)device.RawLatitude ?? DBNull.Value);
            update.Parameters.AddWithValue("@rawLongitude", (object?)device.RawLongitude ?? DBNull.Value);
            update.Parameters.AddWithValue("@location", (object?)device.Location ?? DBNull.Value);
            update.Parameters.AddWithValue("@coordinateSource", device.CoordinateSource ?? string.Empty);
            update.Parameters.AddWithValue("@coordinateStatus", device.CoordinateStatus ?? string.Empty);
            update.Parameters.AddWithValue("@coordinateStatusMessage", device.CoordinateStatusMessage ?? string.Empty);
            update.Parameters.AddWithValue("@syncedAt", device.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var groupCount = await ExecuteScalarIntAsync(connection, """SELECT COUNT(*) FROM "Group";""", cancellationToken);
        var deviceCount = await ExecuteScalarIntAsync(connection, "SELECT COUNT(*) FROM Device;", cancellationToken);
        var metadata = await ReadMetadataAsync(connection, cancellationToken);
        var lastSyncedAt = await GetLastSyncedAtAsync(connection, cancellationToken);
        return new LocalSyncSnapshot(groupCount, deviceCount, lastSyncedAt, metadata);
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(SqliteBootstrapper.BuildConnectionString(databaseOptions.Value, runtimePaths));
    }

    private static async Task UpsertGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InspectionGroup group,
        CancellationToken cancellationToken)
    {
        await using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO "Group"(
                groupId,
                groupName,
                deviceCount,
                syncedAt,
                parentGroupId,
                regionCode,
                level,
                hasChildren,
                hasDevice,
                regionGbId)
            VALUES(
                @groupId,
                @groupName,
                @deviceCount,
                @syncedAt,
                @parentGroupId,
                @regionCode,
                @level,
                @hasChildren,
                @hasDevice,
                @regionGbId)
            ON CONFLICT(groupId) DO UPDATE SET
                groupName = excluded.groupName,
                deviceCount = excluded.deviceCount,
                syncedAt = excluded.syncedAt,
                parentGroupId = excluded.parentGroupId,
                regionCode = excluded.regionCode,
                level = excluded.level,
                hasChildren = excluded.hasChildren,
                hasDevice = excluded.hasDevice,
                regionGbId = excluded.regionGbId;
            """;
        upsert.Parameters.AddWithValue("@groupId", group.GroupId);
        upsert.Parameters.AddWithValue("@groupName", group.GroupName);
        upsert.Parameters.AddWithValue("@deviceCount", group.DeviceCount);
        upsert.Parameters.AddWithValue("@syncedAt", group.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
        upsert.Parameters.AddWithValue("@parentGroupId", (object?)group.ParentGroupId ?? DBNull.Value);
        upsert.Parameters.AddWithValue("@regionCode", group.RegionCode);
        upsert.Parameters.AddWithValue("@level", group.Level);
        upsert.Parameters.AddWithValue("@hasChildren", group.HasChildren ? 1 : 0);
        upsert.Parameters.AddWithValue("@hasDevice", group.HasDevice ? 1 : 0);
        upsert.Parameters.AddWithValue("@regionGbId", (object?)group.RegionGbId ?? DBNull.Value);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDeviceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InspectionDevice device,
        CancellationToken cancellationToken)
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
                rawLatitude,
                rawLongitude,
                location,
                coordinateSource,
                coordinateStatus,
                coordinateStatusMessage,
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
                @rawLatitude,
                @rawLongitude,
                @location,
                @coordinateSource,
                @coordinateStatus,
                @coordinateStatusMessage,
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
        insert.Parameters.AddWithValue("@rawLatitude", (object?)device.RawLatitude ?? DBNull.Value);
        insert.Parameters.AddWithValue("@rawLongitude", (object?)device.RawLongitude ?? DBNull.Value);
        insert.Parameters.AddWithValue("@location", (object?)device.Location ?? DBNull.Value);
        insert.Parameters.AddWithValue("@coordinateSource", device.CoordinateSource ?? string.Empty);
        insert.Parameters.AddWithValue("@coordinateStatus", device.CoordinateStatus ?? string.Empty);
        insert.Parameters.AddWithValue("@coordinateStatusMessage", device.CoordinateStatusMessage ?? string.Empty);
        insert.Parameters.AddWithValue("@onlineStatus", (object?)device.OnlineStatus ?? DBNull.Value);
        insert.Parameters.AddWithValue("@cloudStatus", (object?)device.CloudStatus ?? DBNull.Value);
        insert.Parameters.AddWithValue("@bandStatus", (object?)device.BandStatus ?? DBNull.Value);
        insert.Parameters.AddWithValue("@sourceTypeFlag", (object?)device.SourceTypeFlag ?? DBNull.Value);
        insert.Parameters.AddWithValue("@syncedAt", device.SyncedAt.ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, InspectionDevice>> LoadExistingDeviceMapAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                deviceCode,
                deviceName,
                groupId,
                rawLatitude,
                rawLongitude,
                location,
                coordinateSource,
                coordinateStatus,
                coordinateStatusMessage,
                onlineStatus,
                cloudStatus,
                bandStatus,
                sourceTypeFlag,
                syncedAt
            FROM Device;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var devices = new Dictionary<string, InspectionDevice>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var device = ReadDevice(reader);
            devices[device.DeviceCode] = device;
        }

        return devices;
    }

    private static InspectionDevice MergeDevice(
        InspectionDevice device,
        IReadOnlyDictionary<string, InspectionDevice> existingDevices)
    {
        if (!existingDevices.TryGetValue(device.DeviceCode, out var existingDevice))
        {
            return device;
        }

        return device with
        {
            Latitude = device.Latitude ?? existingDevice.Latitude,
            Longitude = device.Longitude ?? existingDevice.Longitude,
            Location = device.Location ?? existingDevice.Location,
            CoordinateSource = string.IsNullOrWhiteSpace(device.CoordinateSource) ? existingDevice.CoordinateSource : device.CoordinateSource,
            CoordinateStatus = string.IsNullOrWhiteSpace(device.CoordinateStatus) ? existingDevice.CoordinateStatus : device.CoordinateStatus,
            CoordinateStatusMessage = string.IsNullOrWhiteSpace(device.CoordinateStatusMessage) ? existingDevice.CoordinateStatusMessage : device.CoordinateStatusMessage,
            OnlineStatus = device.OnlineStatus ?? existingDevice.OnlineStatus,
            CloudStatus = device.CloudStatus ?? existingDevice.CloudStatus,
            BandStatus = device.BandStatus ?? existingDevice.BandStatus,
            SourceTypeFlag = device.SourceTypeFlag ?? existingDevice.SourceTypeFlag
        };
    }

    private static async Task UpsertMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GroupSyncSnapshotMetadata metadata,
        DateTimeOffset? syncedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO SyncMetadata(
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
            VALUES(
                1,
                @platformGroupCount,
                @platformDeviceCount,
                @reconciliationCompleted,
                @reconciliationMatched,
                @reconciledRegionCount,
                @reconciledDeviceCount,
                @reconciledOnlineCount,
                @reconciliationScopeText,
                @syncedAt)
            ON CONFLICT(id) DO UPDATE SET
                platformGroupCount = excluded.platformGroupCount,
                platformDeviceCount = excluded.platformDeviceCount,
                reconciliationCompleted = excluded.reconciliationCompleted,
                reconciliationMatched = excluded.reconciliationMatched,
                reconciledRegionCount = excluded.reconciledRegionCount,
                reconciledDeviceCount = excluded.reconciledDeviceCount,
                reconciledOnlineCount = excluded.reconciledOnlineCount,
                reconciliationScopeText = excluded.reconciliationScopeText,
                syncedAt = excluded.syncedAt;
            """;
        command.Parameters.AddWithValue("@platformGroupCount", metadata.PlatformGroupCount);
        command.Parameters.AddWithValue("@platformDeviceCount", metadata.PlatformDeviceCount);
        command.Parameters.AddWithValue("@reconciliationCompleted", metadata.ReconciliationCompleted ? 1 : 0);
        command.Parameters.AddWithValue("@reconciliationMatched", metadata.ReconciliationMatched ? 1 : 0);
        command.Parameters.AddWithValue("@reconciledRegionCount", metadata.ReconciledRegionCount);
        command.Parameters.AddWithValue("@reconciledDeviceCount", metadata.ReconciledDeviceCount);
        command.Parameters.AddWithValue("@reconciledOnlineCount", metadata.ReconciledOnlineCount);
        command.Parameters.AddWithValue("@reconciliationScopeText", metadata.ReconciliationScopeText);
        command.Parameters.AddWithValue("@syncedAt", syncedAt?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<GroupSyncSnapshotMetadata> ReadMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                platformGroupCount,
                platformDeviceCount,
                reconciliationCompleted,
                reconciliationMatched,
                reconciledRegionCount,
                reconciledDeviceCount,
                reconciledOnlineCount,
                reconciliationScopeText
            FROM SyncMetadata
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return GroupSyncSnapshotMetadata.Empty;
        }

        return new GroupSyncSnapshotMetadata(
            ReadInt32(reader, 0),
            ReadInt32(reader, 1),
            ReadBoolean(reader, 2),
            ReadBoolean(reader, 3),
            ReadInt32(reader, 4),
            ReadInt32(reader, 5),
            ReadInt32(reader, 6),
            ReadString(reader, 7));
    }

    private static DateTimeOffset? GetSnapshotSyncedAt(
        IReadOnlyCollection<InspectionGroup> groups,
        IReadOnlyCollection<InspectionDevice> devices)
    {
        var values = groups.Select(group => group.SyncedAt)
            .Concat(devices.Select(device => device.SyncedAt))
            .ToArray();

        return values.Length == 0 ? null : values.Max();
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
            SELECT syncedAt
            FROM SyncMetadata
            WHERE id = 1;
            """;

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not null
            && result is not DBNull
            && DateTimeOffset.TryParse(result.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var syncedAt))
        {
            return syncedAt;
        }

        await using var fallbackCommand = connection.CreateCommand();
        fallbackCommand.CommandText = """
            SELECT MAX(syncedAt)
            FROM (
                SELECT syncedAt FROM "Group"
                UNION ALL
                SELECT syncedAt FROM Device
            );
            """;

        var fallback = await fallbackCommand.ExecuteScalarAsync(cancellationToken);
        if (fallback is null || fallback is DBNull)
        {
            return null;
        }

        return DateTimeOffset.TryParse(fallback.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static InspectionDevice ReadDevice(SqliteDataReader reader)
    {
        return new InspectionDevice(
            ReadString(reader, 0),
            ReadString(reader, 1),
            ReadString(reader, 2),
            ReadNullableString(reader, 3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            ReadNullableInt32(reader, 9),
            ReadNullableInt32(reader, 10),
            ReadNullableInt32(reader, 11),
            ReadNullableInt32(reader, 12),
            ReadSyncedAt(reader, 13),
            ReadString(reader, 6),
            ReadString(reader, 7),
            ReadString(reader, 8));
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

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal)
    {
        return ReadInt32(reader, ordinal) == 1;
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
