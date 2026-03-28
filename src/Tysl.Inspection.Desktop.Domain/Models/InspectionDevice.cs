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
    DateTimeOffset SyncedAt);
