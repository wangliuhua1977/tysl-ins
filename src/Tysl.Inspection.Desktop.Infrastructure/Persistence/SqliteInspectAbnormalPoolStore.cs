using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;

namespace Tysl.Inspection.Desktop.Infrastructure.Persistence;

public sealed class SqliteInspectAbnormalPoolStore(
    IOptions<DatabaseOptions> databaseOptions,
    AppRuntimePaths runtimePaths) : IInspectAbnormalPoolStore
{
    public IReadOnlyList<InspectAbnormalItem> LoadItems()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                deviceCode,
                deviceName,
                inspectAt,
                abnormalClass,
                abnormalClassText,
                conclusion,
                failureCategory,
                dispositionSummary,
                isReviewed,
                handleStatus,
                handleStatusText,
                handleUpdatedAt,
                updatedAt
            FROM InspectAbnormalPool
            ORDER BY inspectAt DESC, updatedAt DESC, deviceCode COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<InspectAbnormalItem>();
        while (reader.Read())
        {
            var inspectAt = ReadDateTimeOffset(reader, 3);
            var handleStatus = ReadHandleStatus(reader, 10);
            var updatedAt = ReadDateTimeOffset(reader, 13);
            items.Add(new InspectAbnormalItem(
                ReadGuid(reader, 0),
                inspectAt,
                ReadString(reader, 2),
                ReadString(reader, 1),
                ReadAbnormalClass(reader, 4),
                ReadString(reader, 5),
                ReadString(reader, 6),
                ReadString(reader, 7),
                ReadString(reader, 8),
                ReadBoolean(reader, 9),
                handleStatus,
                ReadHandleStatusText(reader, 11, handleStatus),
                ReadDateTimeOffset(reader, 12, updatedAt),
                updatedAt));
        }

        return items;
    }

    public void Upsert(InspectAbnormalItem item)
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO InspectAbnormalPool(
                id,
                deviceCode,
                deviceName,
                inspectAt,
                abnormalClass,
                abnormalClassText,
                conclusion,
                failureCategory,
                dispositionSummary,
                isReviewed,
                handleStatus,
                handleStatusText,
                handleUpdatedAt,
                updatedAt)
            VALUES(
                @id,
                @deviceCode,
                @deviceName,
                @inspectAt,
                @abnormalClass,
                @abnormalClassText,
                @conclusion,
                @failureCategory,
                @dispositionSummary,
                @isReviewed,
                @handleStatus,
                @handleStatusText,
                @handleUpdatedAt,
                @updatedAt)
            ON CONFLICT(deviceCode, abnormalClass, conclusion) DO UPDATE SET
                deviceName = excluded.deviceName,
                inspectAt = excluded.inspectAt,
                abnormalClassText = excluded.abnormalClassText,
                failureCategory = excluded.failureCategory,
                dispositionSummary = excluded.dispositionSummary,
                isReviewed = excluded.isReviewed,
                handleStatus = excluded.handleStatus,
                handleStatusText = excluded.handleStatusText,
                handleUpdatedAt = excluded.handleUpdatedAt,
                updatedAt = excluded.updatedAt;
            """;
        command.Parameters.AddWithValue("@id", item.Id.ToString("D"));
        command.Parameters.AddWithValue("@deviceCode", item.DeviceCode);
        command.Parameters.AddWithValue("@deviceName", item.DeviceName);
        command.Parameters.AddWithValue("@inspectAt", item.InspectAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@abnormalClass", (int)item.AbnormalClass);
        command.Parameters.AddWithValue("@abnormalClassText", item.AbnormalClassText);
        command.Parameters.AddWithValue("@conclusion", item.Conclusion);
        command.Parameters.AddWithValue("@failureCategory", item.FailureCategory);
        command.Parameters.AddWithValue("@dispositionSummary", item.SummaryText);
        command.Parameters.AddWithValue("@isReviewed", item.IsReviewed ? 1 : 0);
        command.Parameters.AddWithValue("@handleStatus", (int)item.HandleStatus);
        command.Parameters.AddWithValue("@handleStatusText", item.HandleStatusText);
        command.Parameters.AddWithValue("@handleUpdatedAt", item.HandleUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@updatedAt", item.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(SqliteBootstrapper.BuildConnectionString(databaseOptions.Value, runtimePaths));
    }

    private static string ReadString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
    {
        return Guid.TryParse(ReadString(reader, ordinal), out var value)
            ? value
            : Guid.NewGuid();
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => longValue != 0,
            int intValue => intValue != 0,
            bool boolValue => boolValue,
            _ => string.Equals(value.ToString(), "1", StringComparison.Ordinal)
        };
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return ReadDateTimeOffset(reader, ordinal, DateTimeOffset.MinValue);
    }

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal, DateTimeOffset fallbackValue)
    {
        if (reader.IsDBNull(ordinal))
        {
            return fallbackValue;
        }

        var text = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value
            : fallbackValue;
    }

    private static InspectAbnormalClass ReadAbnormalClass(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return InspectAbnormalClass.None;
        }

        var value = reader.GetValue(ordinal);
        var raw = value switch
        {
            long longValue => Convert.ToInt32(longValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };

        return Enum.IsDefined(typeof(InspectAbnormalClass), raw)
            ? (InspectAbnormalClass)raw
            : InspectAbnormalClass.None;
    }

    private static InspectHandleStatus ReadHandleStatus(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return InspectHandleStatus.Pending;
        }

        var value = reader.GetValue(ordinal);
        var raw = value switch
        {
            long longValue => Convert.ToInt32(longValue, CultureInfo.InvariantCulture),
            int intValue => intValue,
            _ when int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => (int)InspectHandleStatus.Pending
        };

        return Enum.IsDefined(typeof(InspectHandleStatus), raw)
            ? (InspectHandleStatus)raw
            : InspectHandleStatus.Pending;
    }

    private static string ReadHandleStatusText(SqliteDataReader reader, int ordinal, InspectHandleStatus handleStatus)
    {
        var storedText = ReadString(reader, ordinal);
        var normalizedText = InspectAbnormalItem.BuildHandleStatusText(handleStatus);
        return string.IsNullOrWhiteSpace(storedText) ? normalizedText : storedText;
    }
}
