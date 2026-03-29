namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record OverviewStats(
    int TotalPoints,
    int OnlineCount,
    int OfflineCount,
    MapCoordinateStats CoordinateStats,
    DateTimeOffset? LastSyncedAt);
