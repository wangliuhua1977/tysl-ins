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
    private const int RegionDevicePageSize = 50;

    public async Task<GroupSyncSummary> SyncAsync(CancellationToken cancellationToken)
    {
        var syncedAt = DateTimeOffset.UtcNow;
        var existingSnapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);

        logger.LogInformation("Starting full monitor region tree synchronization.");

        var state = new SyncTraversalState(syncedAt);
        var treeLoaded = await LoadRegionTreeAsync(
            regionId: string.Empty,
            parentGroupId: null,
            parentGroupName: null,
            parentLevel: 0,
            state: state,
            cancellationToken: cancellationToken);

        if (!treeLoaded)
        {
            logger.LogWarning(
                "Monitor region tree synchronization aborted before SQLite replacement. ExistingGroups={GroupCount}, ExistingDevices={DeviceCount}, Success={SuccessCount}, Failure={FailureCount}.",
                existingSnapshot.GroupCount,
                existingSnapshot.DeviceCount,
                state.SuccessCount,
                state.Failures.Count);

            return BuildSummary(existingSnapshot, state.SuccessCount, state.Failures, snapshotReplaced: false);
        }

        var metadata = await BuildMetadataAsync(state.Groups, state.Devices, state.Failures, cancellationToken);

        try
        {
            logger.LogInformation(
                "Replacing SQLite snapshot for monitor region tree. PlatformGroups={PlatformGroupCount}, PlatformDevices={PlatformDeviceCount}, ReconciliationCompleted={ReconciliationCompleted}, ReconciliationMatched={ReconciliationMatched}.",
                metadata.PlatformGroupCount,
                metadata.PlatformDeviceCount,
                metadata.ReconciliationCompleted,
                metadata.ReconciliationMatched);

            await groupSyncStore.ReplaceSnapshotAsync(state.Groups, state.Devices, metadata, cancellationToken);

            logger.LogInformation(
                "SQLite snapshot replacement completed. StoredGroups={GroupCount}, StoredDevices={DeviceCount}.",
                state.Groups.Count,
                state.Devices.Count);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist monitor region tree snapshot into SQLite.");

            var failures = state.Failures
                .Concat(
                [
                    new GroupSyncFailure(
                        GroupSyncFailureKind.DatabaseWriteFailed,
                        null,
                        null,
                        $"Snapshot write failed: {exception.Message}")
                ])
                .ToArray();

            return BuildSummary(existingSnapshot, state.SuccessCount, failures, snapshotReplaced: false);
        }

        var snapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
        logger.LogInformation(
            "Full monitor region tree synchronization completed. Groups={GroupCount}, Devices={DeviceCount}, Success={SuccessCount}, Failure={FailureCount}, ReconciliationCompleted={ReconciliationCompleted}, ReconciliationMatched={ReconciliationMatched}, Scope={Scope}.",
            snapshot.GroupCount,
            snapshot.DeviceCount,
            state.SuccessCount,
            state.Failures.Count,
            snapshot.Metadata.ReconciliationCompleted,
            snapshot.Metadata.ReconciliationMatched,
            snapshot.Metadata.ReconciliationScopeText);

        return BuildSummary(snapshot, state.SuccessCount, state.Failures, snapshotReplaced: true);
    }

    public Task<LocalSyncSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        return groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
    }

    private async Task<bool> LoadRegionTreeAsync(
        string regionId,
        string? parentGroupId,
        string? parentGroupName,
        int parentLevel,
        SyncTraversalState state,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Monitor region tree recursion started. ParentRegionId={ParentRegionId}, ParentGroupId={ParentGroupId}.",
            string.IsNullOrWhiteSpace(regionId) ? "(root)" : regionId,
            parentGroupId ?? "(root)");

        var regionResult = await openPlatformClient.GetRegionListAsync(regionId, cancellationToken);
        if (!regionResult.Success || regionResult.Payload is null)
        {
            var failure = new GroupSyncFailure(
                GroupSyncFailureKind.GetRegionListFailed,
                parentGroupId,
                parentGroupName ?? "根目录",
                regionResult.BuildMessage());

            state.Failures.Add(failure);
            logger.LogWarning(
                "getReginWithGroupList failed. ParentRegionId={ParentRegionId}, ParentGroupId={ParentGroupId}, Message={Message}.",
                string.IsNullOrWhiteSpace(regionId) ? "(root)" : regionId,
                parentGroupId ?? "(root)",
                failure.Message);
            return false;
        }

        var regions = regionResult.Payload;
        logger.LogInformation(
            "Monitor region tree recursion completed. ParentRegionId={ParentRegionId}, RegionCount={RegionCount}.",
            string.IsNullOrWhiteSpace(regionId) ? "(root)" : regionId,
            regions.Count);

        foreach (var region in regions)
        {
            if (string.IsNullOrWhiteSpace(region.Id) || string.IsNullOrWhiteSpace(region.Name))
            {
                var invalidFailure = new GroupSyncFailure(
                    GroupSyncFailureKind.GetRegionListFailed,
                    parentGroupId,
                    parentGroupName ?? "根目录",
                    "getReginWithGroupList 返回目录缺少 id 或 name；文档未说明，需人工确认。");

                state.Failures.Add(invalidFailure);
                logger.LogWarning(
                    "Invalid monitor region node returned. ParentRegionId={ParentRegionId}, ParentGroupId={ParentGroupId}.",
                    string.IsNullOrWhiteSpace(regionId) ? "(root)" : regionId,
                    parentGroupId ?? "(root)");
                return false;
            }

            var level = NormalizeLevel(region.Level, parentLevel);
            var hasChildren = region.HasChildren == 1;
            var hasDevice = region.HavDevice == 1;
            var devices = Array.Empty<InspectionDevice>();

            if (hasDevice)
            {
                var loadedDevices = await LoadRegionDevicesAsync(region.Id, region.Name, state.SyncedAt, state.Failures, cancellationToken);
                if (loadedDevices is null)
                {
                    return false;
                }

                devices = loadedDevices.ToArray();
                state.Devices.AddRange(devices);
            }

            state.Groups.Add(new InspectionGroup(
                region.Id,
                region.Name,
                parentGroupId,
                region.RegionCode ?? string.Empty,
                devices.Length,
                level,
                hasChildren,
                hasDevice,
                region.RegionGBId,
                state.SyncedAt));
            state.SuccessCount++;

            if (hasChildren)
            {
                var childLoaded = await LoadRegionTreeAsync(
                    region.Id,
                    region.Id,
                    region.Name,
                    level,
                    state,
                    cancellationToken);
                if (!childLoaded)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task<IReadOnlyList<InspectionDevice>?> LoadRegionDevicesAsync(
        string regionId,
        string regionName,
        DateTimeOffset syncedAt,
        ICollection<GroupSyncFailure> failures,
        CancellationToken cancellationToken)
    {
        var devices = new List<InspectionDevice>();
        var pageNo = 1;
        var totalCount = 0;

        while (true)
        {
            var pageResult = await openPlatformClient.GetRegionDevicePageAsync(regionId, pageNo, RegionDevicePageSize, cancellationToken);
            if (!pageResult.Success || pageResult.Payload is null)
            {
                var failure = new GroupSyncFailure(
                    GroupSyncFailureKind.GetRegionDeviceListFailed,
                    regionId,
                    regionName,
                    pageResult.BuildMessage());

                failures.Add(failure);
                logger.LogWarning(
                    "getDeviceList failed. RegionId={RegionId}, RegionName={RegionName}, PageNo={PageNo}, Message={Message}.",
                    regionId,
                    regionName,
                    pageNo,
                    failure.Message);
                return null;
            }

            totalCount = Math.Max(totalCount, Math.Max(pageResult.Payload.TotalCount, 0));
            var effectivePageSize = pageResult.Payload.PageSize > 0
                ? pageResult.Payload.PageSize
                : RegionDevicePageSize;
            var pageDevices = pageResult.Payload.Items
                .Select(device => new InspectionDevice(
                    device.DeviceCode,
                    device.DeviceName,
                    regionId,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    syncedAt))
                .ToArray();

            devices.AddRange(pageDevices);
            logger.LogInformation(
                "Directory device page fetched. RegionId={RegionId}, RegionName={RegionName}, PageNo={PageNo}, PageSize={PageSize}, PageItemCount={PageItemCount}, Accumulated={Accumulated}, TotalCount={TotalCount}.",
                regionId,
                regionName,
                pageResult.Payload.PageNo,
                pageResult.Payload.PageSize,
                pageDevices.Length,
                devices.Count,
                totalCount);

            if (pageDevices.Length == 0)
            {
                break;
            }

            var platformTotalReached = totalCount > 0 && devices.Count == totalCount;
            var platformTotalUndershot = totalCount >= 0 && devices.Count > totalCount;
            var reachedShortPage = pageDevices.Length < effectivePageSize;

            if (platformTotalReached || reachedShortPage)
            {
                break;
            }

            if (platformTotalUndershot)
            {
                logger.LogWarning(
                    "Directory device pagination totalCount under-reported. RegionId={RegionId}, RegionName={RegionName}, ReportedTotalCount={ReportedTotalCount}, LoadedCount={LoadedCount}, PageNo={PageNo}, PageSize={PageSize}. Continuing until an empty or short page is returned.",
                    regionId,
                    regionName,
                    totalCount,
                    devices.Count,
                    pageResult.Payload.PageNo,
                    effectivePageSize);
            }

            pageNo++;
        }

        if (totalCount > 0 && devices.Count < totalCount)
        {
            var message = $"getDeviceList 分页结果未拉满：平台 totalCount={totalCount}，本地仅拉回 {devices.Count}。";
            failures.Add(new GroupSyncFailure(
                GroupSyncFailureKind.GetRegionDeviceListFailed,
                regionId,
                regionName,
                message));
            logger.LogWarning(
                "Directory device pagination mismatch. RegionId={RegionId}, RegionName={RegionName}, TotalCount={TotalCount}, LoadedCount={LoadedCount}.",
                regionId,
                regionName,
                totalCount,
                devices.Count);
            return null;
        }

        if (devices.Count > totalCount)
        {
            logger.LogWarning(
                "Directory device pagination completed with more returned items than totalCount. RegionId={RegionId}, RegionName={RegionName}, ReportedTotalCount={ReportedTotalCount}, LoadedCount={LoadedCount}.",
                regionId,
                regionName,
                totalCount,
                devices.Count);
        }

        return devices;
    }

    private async Task<GroupSyncSnapshotMetadata> BuildMetadataAsync(
        IReadOnlyList<InspectionGroup> groups,
        IReadOnlyList<InspectionDevice> devices,
        ICollection<GroupSyncFailure> failures,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            logger.LogInformation("Monitor region tree is empty after synchronization; getCusDeviceCount reconciliation skipped.");
            return new GroupSyncSnapshotMetadata(
                0,
                0,
                true,
                true,
                0,
                0,
                0,
                "当前目录树为空，无需执行首层对账。");
        }

        var countsResult = await openPlatformClient.GetRegionDeviceCountsAsync(string.Empty, cancellationToken);
        if (!countsResult.Success || countsResult.Payload is null)
        {
            var failure = new GroupSyncFailure(
                GroupSyncFailureKind.GetDeviceCountReconciliationFailed,
                null,
                "首层对账",
                countsResult.BuildMessage());
            failures.Add(failure);

            logger.LogWarning(
                "getCusDeviceCount reconciliation failed. Message={Message}.",
                failure.Message);

            return new GroupSyncSnapshotMetadata(
                groups.Count,
                devices.Count,
                false,
                false,
                0,
                0,
                0,
                $"已对账范围：未完成。getCusDeviceCount 调用失败：{failure.Message}");
        }

        var rootGroups = groups
            .Where(group => string.IsNullOrWhiteSpace(group.ParentGroupId))
            .ToArray();
        var subtreeCounts = BuildSubtreeDeviceCounts(groups, devices);
        var platformCounts = countsResult.Payload.ToArray();
        var platformCountByRegionCode = platformCounts
            .Where(item => !string.IsNullOrWhiteSpace(item.RegionCode))
            .ToDictionary(item => item.RegionCode, StringComparer.OrdinalIgnoreCase);

        var mismatches = new List<string>();
        foreach (var rootGroup in rootGroups)
        {
            if (string.IsNullOrWhiteSpace(rootGroup.RegionCode))
            {
                mismatches.Add($"{rootGroup.GroupName} 缺少 regionCode，无法对账");
                continue;
            }

            if (!platformCountByRegionCode.TryGetValue(rootGroup.RegionCode, out var platformCount))
            {
                mismatches.Add($"{rootGroup.GroupName}({rootGroup.RegionCode}) 缺少平台对账记录");
                continue;
            }

            var localCount = subtreeCounts.TryGetValue(rootGroup.GroupId, out var count)
                ? count
                : 0;
            var expectedCount = Math.Max(platformCount.DeviceCount ?? 0, 0);
            if (localCount != expectedCount)
            {
                mismatches.Add($"{rootGroup.GroupName}({rootGroup.RegionCode}) 本地{localCount}/平台{expectedCount}");
            }
        }

        foreach (var platformCount in platformCounts)
        {
            if (string.IsNullOrWhiteSpace(platformCount.RegionCode))
            {
                continue;
            }

            var exists = rootGroups.Any(group => string.Equals(group.RegionCode, platformCount.RegionCode, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                mismatches.Add($"平台返回额外首层 regionCode {platformCount.RegionCode}");
            }
        }

        var reconciledDeviceCount = platformCounts.Sum(item => Math.Max(item.DeviceCount ?? 0, 0));
        var reconciledOnlineCount = platformCounts.Sum(item => Math.Max(item.OnlineCount ?? 0, 0));
        var scopeText = platformCounts.Length == 0
            ? "首层 regionCode 返回空列表"
            : $"首层 regionCode：{string.Join(", ", platformCounts.Select(item => string.IsNullOrWhiteSpace(item.RegionCode) ? "(空)" : item.RegionCode))}";
        var matched = mismatches.Count == 0;

        logger.LogInformation(
            "getCusDeviceCount reconciliation completed. RegionCount={RegionCount}, DeviceCount={DeviceCount}, OnlineCount={OnlineCount}, Matched={Matched}, Scope={Scope}.",
            platformCounts.Length,
            reconciledDeviceCount,
            reconciledOnlineCount,
            matched,
            scopeText);

        if (!matched)
        {
            logger.LogWarning(
                "getCusDeviceCount reconciliation mismatch detected. Details={Details}.",
                string.Join(" | ", mismatches));
        }

        return new GroupSyncSnapshotMetadata(
            groups.Count,
            devices.Count,
            true,
            matched,
            platformCounts.Length,
            reconciledDeviceCount,
            reconciledOnlineCount,
            matched
                ? scopeText
                : $"{scopeText}；差异：{string.Join(" | ", mismatches)}");
    }

    private static Dictionary<string, int> BuildSubtreeDeviceCounts(
        IReadOnlyList<InspectionGroup> groups,
        IReadOnlyList<InspectionDevice> devices)
    {
        var childrenLookup = groups.ToLookup(group => group.ParentGroupId ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        var devicesByGroup = devices.ToLookup(device => device.GroupId, StringComparer.OrdinalIgnoreCase);
        var cache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            CountDevices(group.GroupId);
        }

        return cache;

        int CountDevices(string groupId)
        {
            if (cache.TryGetValue(groupId, out var cached))
            {
                return cached;
            }

            var total = devicesByGroup[groupId].Count();
            foreach (var child in childrenLookup[groupId])
            {
                total += CountDevices(child.GroupId);
            }

            cache[groupId] = total;
            return total;
        }
    }

    private static int NormalizeLevel(int? currentLevel, int parentLevel)
    {
        return currentLevel is > 0
            ? currentLevel.Value
            : parentLevel + 1;
    }

    private static GroupSyncSummary BuildSummary(
        LocalSyncSnapshot snapshot,
        int successCount,
        IReadOnlyList<GroupSyncFailure> failures,
        bool snapshotReplaced)
    {
        return new GroupSyncSummary(
            snapshot.GroupCount,
            snapshot.DeviceCount,
            successCount,
            failures.Count,
            snapshot.LastSyncedAt,
            failures,
            snapshotReplaced,
            snapshot.Metadata);
    }

    private sealed class SyncTraversalState(DateTimeOffset syncedAt)
    {
        public DateTimeOffset SyncedAt { get; } = syncedAt;

        public List<InspectionGroup> Groups { get; } = [];

        public List<InspectionDevice> Devices { get; } = [];

        public List<GroupSyncFailure> Failures { get; } = [];

        public int SuccessCount { get; set; }
    }
}
