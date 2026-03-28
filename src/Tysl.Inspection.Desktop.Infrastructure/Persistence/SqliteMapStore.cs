using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Infrastructure.Persistence;

public sealed class SqliteMapStore(
    IOptions<DatabaseOptions> databaseOptions,
    AppRuntimePaths runtimePaths) : IMapStore
{
    public async Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(SqliteBootstrapper.BuildConnectionString(databaseOptions.Value, runtimePaths));
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

    private static string ReadString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        if (value is long longValue)
        {
            return Convert.ToInt32(longValue, CultureInfo.InvariantCulture);
        }

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
