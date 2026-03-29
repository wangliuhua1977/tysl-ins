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
}

public sealed record DeviceUserMaintenance(
    string DeviceCode,
    string MaintenanceStatus,
    string MaintenanceNote,
    string ManualConfirmationNote,
    DateTimeOffset UpdatedAt);
