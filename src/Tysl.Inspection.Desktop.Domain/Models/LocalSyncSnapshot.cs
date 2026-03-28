namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record LocalSyncSnapshot(
    int GroupCount,
    int DeviceCount,
    DateTimeOffset? LastSyncedAt);
