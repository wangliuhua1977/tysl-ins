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

public interface IMapStore
{
    Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken);
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

public interface IMapService
{
    Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken);
}

public interface IPreviewService
{
    Task<PreviewDeviceLoadResult> LoadLocalDevicesAsync(CancellationToken cancellationToken);

    Task<PreviewPrepareResult> PrepareAsync(string deviceCode, CancellationToken cancellationToken);
}

public sealed record MapLoadResult(
    bool Success,
    string Message,
    IReadOnlyList<InspectionDevice> Devices);

public sealed record PreviewDeviceOption(
    string DeviceCode,
    string DeviceName,
    int? OnlineStatus)
{
    public string DisplayText => $"{DeviceName} ({DeviceCode})";
}

public sealed record PreviewDeviceLoadResult(
    bool Success,
    string Message,
    IReadOnlyList<PreviewDeviceOption> Devices);

public sealed record PreviewPrepareResult(
    bool Success,
    string DeviceCode,
    string DeviceName,
    string DiagnosisText,
    string AddressStatusText,
    string? RtspUrl,
    string ExpireText,
    DateTimeOffset RequestedAt);
