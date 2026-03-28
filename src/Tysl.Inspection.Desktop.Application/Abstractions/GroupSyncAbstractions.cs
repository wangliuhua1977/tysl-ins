using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Abstractions;

public interface ISqliteBootstrapper
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IGroupSyncStore
{
    Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken);

    Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken);

    Task ReplaceDevicesForGroupAsync(
        string groupId,
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken);

    Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken);

    Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken);
}

public interface IGroupSyncService
{
    Task<GroupSyncSummary> SyncAsync(CancellationToken cancellationToken);

    Task<LocalSyncSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken);
}

public interface IOverviewStatsService
{
    Task<OverviewStats> GetAsync(CancellationToken cancellationToken);
}
