namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record InspectionGroup(
    string GroupId,
    string GroupName,
    string? ParentGroupId,
    string RegionCode,
    int DeviceCount,
    int Level,
    bool HasChildren,
    bool HasDevice,
    string? RegionGbId,
    DateTimeOffset SyncedAt);
