namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record InspectionGroup(
    string GroupId,
    string GroupName,
    int DeviceCount,
    DateTimeOffset SyncedAt);
