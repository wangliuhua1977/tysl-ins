using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class GroupSyncService(
    IOpenPlatformClient openPlatformClient,
    IGroupSyncStore groupSyncStore,
    ILogger<GroupSyncService> logger) : IGroupSyncService
{
    public async Task<GroupSyncSummary> SyncAsync(CancellationToken cancellationToken)
    {
        var syncedAt = DateTimeOffset.UtcNow;
        logger.LogInformation("Starting group and device synchronization.");

        var groupResult = await openPlatformClient.GetGroupListAsync(cancellationToken);
        if (!groupResult.Success || groupResult.Payload is null)
        {
            logger.LogWarning(
                "Group synchronization aborted because getGroupList failed. Message: {Message}",
                groupResult.BuildMessage());

            return new GroupSyncSummary(
                0,
                0,
                0,
                1,
                null,
                new[]
                {
                    new GroupSyncFailure(
                        GroupSyncFailureKind.GetGroupListFailed,
                        null,
                        null,
                        groupResult.BuildMessage())
                });
        }

        var groups = groupResult.Payload
            .Select(group => new InspectionGroup(group.GroupId, group.GroupName, group.DeviceCount, syncedAt))
            .ToArray();

        try
        {
            await groupSyncStore.ReplaceGroupsAsync(groups, cancellationToken);
            await groupSyncStore.DeleteOrphanDevicesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist groups into SQLite.");

            return new GroupSyncSummary(
                groups.Length,
                0,
                0,
                1,
                null,
                new[]
                {
                    new GroupSyncFailure(
                        GroupSyncFailureKind.DatabaseWriteFailed,
                        null,
                        null,
                        $"Group write failed: {exception.Message}")
                });
        }

        var failures = new List<GroupSyncFailure>();
        var successCount = 0;

        foreach (var group in groups)
        {
            var deviceResult = await openPlatformClient.GetGroupDeviceListAsync(group.GroupId, cancellationToken);
            if (!deviceResult.Success || deviceResult.Payload is null)
            {
                logger.LogWarning(
                    "getGroupDeviceList failed for group {GroupId}. Message: {Message}",
                    group.GroupId,
                    deviceResult.BuildMessage());

                failures.Add(new GroupSyncFailure(
                    GroupSyncFailureKind.GetGroupDeviceListFailed,
                    group.GroupId,
                    group.GroupName,
                    deviceResult.BuildMessage()));
                continue;
            }

            var devices = deviceResult.Payload
                .Select(device => new InspectionDevice(
                    device.DeviceCode,
                    device.DeviceName,
                    device.GroupId,
                    device.Latitude,
                    device.Longitude,
                    device.Location,
                    device.OnlineStatus,
                    device.CloudStatus,
                    device.BandStatus,
                    device.SourceTypeFlag,
                    syncedAt))
                .ToArray();

            try
            {
                await groupSyncStore.ReplaceDevicesForGroupAsync(group.GroupId, devices, cancellationToken);
                successCount++;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to persist devices for group {GroupId}.",
                    group.GroupId);

                failures.Add(new GroupSyncFailure(
                    GroupSyncFailureKind.DatabaseWriteFailed,
                    group.GroupId,
                    group.GroupName,
                    $"Device write failed: {exception.Message}"));
            }
        }

        var snapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
        logger.LogInformation(
            "Group and device synchronization completed. Groups={GroupCount}, Devices={DeviceCount}, Success={SuccessCount}, Failure={FailureCount}.",
            snapshot.GroupCount,
            snapshot.DeviceCount,
            successCount,
            failures.Count);

        return new GroupSyncSummary(
            snapshot.GroupCount,
            snapshot.DeviceCount,
            successCount,
            failures.Count,
            snapshot.LastSyncedAt,
            failures);
    }

    public Task<LocalSyncSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        return groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
    }
}
