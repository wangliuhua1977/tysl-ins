namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record InspectionDevice(
    string DeviceCode,
    string DeviceName,
    string GroupId,
    string? Latitude,
    string? Longitude,
    string? Location,
    int? OnlineStatus,
    int? CloudStatus,
    int? BandStatus,
    int? SourceTypeFlag,
    DateTimeOffset SyncedAt,
    string CoordinateSource = "",
    string CoordinateStatus = "",
    string CoordinateStatusMessage = "")
{
    public string? RawLatitude => Latitude;

    public string? RawLongitude => Longitude;

    public string? MapLatitude { get; init; }

    public string? MapLongitude { get; init; }

    public bool HasRawCoordinate =>
        !string.IsNullOrWhiteSpace(RawLatitude)
        && !string.IsNullOrWhiteSpace(RawLongitude);

    public bool HasCachedMapCoordinate =>
        !string.IsNullOrWhiteSpace(MapLatitude)
        && !string.IsNullOrWhiteSpace(MapLongitude);
}

public sealed record DeviceUserMaintenance(
    string DeviceCode,
    string MaintenanceStatus,
    string MaintenanceNote,
    string ManualConfirmationNote,
    DateTimeOffset UpdatedAt);
