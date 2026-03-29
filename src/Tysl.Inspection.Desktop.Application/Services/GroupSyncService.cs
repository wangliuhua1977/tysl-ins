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
        logger.LogInformation("Starting full group and device synchronization.");

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

        var reportedDeviceCount = groups.Sum(group => Math.Max(group.DeviceCount, 0));
        logger.LogInformation(
            "Group list fetched for full synchronization. Groups={GroupCount}, ReportedDevices={ReportedDeviceCount}.",
            groups.Length,
            reportedDeviceCount);

        var failures = new List<GroupSyncFailure>();
        var allDevices = new List<InspectionDevice>();
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

            logger.LogInformation(
                "Group device list fetched for {GroupId}. GroupName={GroupName}, ReportedDevices={ReportedDeviceCount}, FetchedDevices={FetchedDeviceCount}.",
                group.GroupId,
                group.GroupName,
                group.DeviceCount,
                devices.Length);

            if (group.DeviceCount != devices.Length)
            {
                logger.LogWarning(
                    "Group device count mismatch detected after fetch. GroupId={GroupId}, GroupName={GroupName}, ReportedDevices={ReportedDeviceCount}, FetchedDevices={FetchedDeviceCount}.",
                    group.GroupId,
                    group.GroupName,
                    group.DeviceCount,
                    devices.Length);
            }

            allDevices.AddRange(devices);
            successCount++;
        }

        if (failures.Count > 0)
        {
            var existingSnapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
            logger.LogWarning(
                "Full synchronization collected failures and skipped SQLite snapshot replacement to avoid partial data. ExistingGroups={GroupCount}, ExistingDevices={DeviceCount}, Success={SuccessCount}, Failure={FailureCount}.",
                existingSnapshot.GroupCount,
                existingSnapshot.DeviceCount,
                successCount,
                failures.Count);

            return new GroupSyncSummary(
                existingSnapshot.GroupCount,
                existingSnapshot.DeviceCount,
                successCount,
                failures.Count,
                existingSnapshot.LastSyncedAt,
                failures);
        }

        try
        {
            await groupSyncStore.ReplaceSnapshotAsync(groups, allDevices, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist full synchronization snapshot into SQLite.");

            var failedSnapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
            return new GroupSyncSummary(
                failedSnapshot.GroupCount,
                failedSnapshot.DeviceCount,
                successCount,
                1,
                failedSnapshot.LastSyncedAt,
                new[]
                {
                    new GroupSyncFailure(
                        GroupSyncFailureKind.DatabaseWriteFailed,
                        null,
                        null,
                        $"Snapshot write failed: {exception.Message}")
                });
        }

        var snapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
        logger.LogInformation(
            "Full synchronization completed. Groups={GroupCount}, Devices={DeviceCount}, ReportedDevices={ReportedDeviceCount}, Success={SuccessCount}, Failure={FailureCount}.",
            snapshot.GroupCount,
            snapshot.DeviceCount,
            reportedDeviceCount,
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
