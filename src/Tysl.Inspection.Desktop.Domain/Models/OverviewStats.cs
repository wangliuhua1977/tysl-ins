namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record OverviewStats(
    int TotalPoints,
    int OnlineCount,
    int OfflineCount,
    int UnlocatedCount,
    DateTimeOffset? LastSyncedAt);
