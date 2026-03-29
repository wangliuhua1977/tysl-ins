using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class MapServiceTests
{
    [Fact]
    public async Task LoadAsync_WritesBackRenderedCoordinateCache_WhenProjectionSucceeds()
    {
        var device = new InspectionDevice(
            "dev-001",
            "测试设备",
            "group-001",
            "31.2304",
            "121.4737",
            "上海",
            1,
            1,
            1,
            0,
            DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"),
            "platform",
            CoordinateStateCatalog.Available,
            "平台原始坐标来自 getDeviceInfoByDeviceCode。");
        var groupStore = new StubGroupSyncStore();
        var service = new MapService(
            new StubMapStore([device]),
            groupStore,
            new StubDeviceCoordinateService([device]),
            new StubCoordinateProjectionService(
                new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dev-001"] = new(
                        "dev-001",
                        true,
                        true,
                        CoordinateStateCatalog.Available,
                        "已获取并转换坐标",
                        "地图 marker 仅使用转换后的高德坐标。",
                        "31.224361",
                        "121.469170")
                }),
            NullLogger<MapService>.Instance);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Devices);
        Assert.Equal("31.224361", result.Devices[0].MapLatitude);
        Assert.Equal("121.469170", result.Devices[0].MapLongitude);
        var saved = Assert.Single(groupStore.UpdatedDevices);
        Assert.Equal("31.224361", saved.MapLatitude);
        Assert.Equal("121.469170", saved.MapLongitude);
    }

    [Fact]
    public async Task LoadAsync_KeepsSuccessfulProjectionWhenAnotherPointFails()
    {
        InspectionDevice[] devices =
        [
            new InspectionDevice(
                "dev-001",
                "测试设备一",
                "group-001",
                "31.2304",
                "121.4737",
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"),
                "platform",
                CoordinateStateCatalog.Available,
                "平台原始坐标来自 getDeviceInfoByDeviceCode。"),
            new InspectionDevice(
                "dev-002",
                "测试设备二",
                "group-001",
                "30.5728",
                "104.0668",
                "成都",
                0,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"),
                "platform",
                CoordinateStateCatalog.Available,
                "平台原始坐标来自 getDeviceInfoByDeviceCode。")
        ];
        var groupStore = new StubGroupSyncStore();
        var service = new MapService(
            new StubMapStore(devices),
            groupStore,
            new StubDeviceCoordinateService(devices),
            new StubCoordinateProjectionService(
                new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dev-001"] = new(
                        "dev-001",
                        true,
                        true,
                        CoordinateStateCatalog.Available,
                        "已获取并转换坐标",
                        "地图 marker 仅使用转换后的高德坐标。",
                        "31.224361",
                        "121.469170"),
                    ["dev-002"] = new(
                        "dev-002",
                        true,
                        false,
                        CoordinateStateCatalog.Failed,
                        "坐标转换失败，需人工确认",
                        "高德坐标转换未完成：status=error / invalid params",
                        null,
                        null)
                }),
            NullLogger<MapService>.Instance);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("31.224361", result.Devices.Single(device => device.DeviceCode == "dev-001").MapLatitude);
        Assert.Null(result.Devices.Single(device => device.DeviceCode == "dev-002").MapLatitude);
        Assert.Equal(2, groupStore.UpdatedDevices.Count);
        var savedSuccess = Assert.Single(groupStore.UpdatedDevices, device => device.DeviceCode == "dev-001");
        var savedFailure = Assert.Single(groupStore.UpdatedDevices, device => device.DeviceCode == "dev-002");
        Assert.Equal("31.224361", savedSuccess.MapLatitude);
        Assert.Equal(CoordinateStateCatalog.Available, savedSuccess.CoordinateStatus);
        Assert.Equal(CoordinateStateCatalog.Failed, savedFailure.CoordinateStatus);
        Assert.Contains("invalid params", savedFailure.CoordinateStatusMessage);
    }

    [Fact]
    public async Task LoadAsync_DoesNotWriteCoordinateCacheForMissingOrRateLimitedPoints()
    {
        InspectionDevice[] devices =
        [
            new InspectionDevice(
                "dev-003",
                "缺坐标设备",
                "group-001",
                null,
                null,
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"),
                "none",
                CoordinateStateCatalog.Missing,
                "平台未提供坐标。"),
            new InspectionDevice(
                "dev-004",
                "限频设备",
                "group-001",
                null,
                null,
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"),
                "none",
                CoordinateStateCatalog.RateLimited,
                "坐标获取限频，稍后重试。")
        ];
        var groupStore = new StubGroupSyncStore();
        var service = new MapService(
            new StubMapStore(devices),
            groupStore,
            new StubDeviceCoordinateService(devices),
            new StubCoordinateProjectionService(
                new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dev-003"] = new(
                        "dev-003",
                        false,
                        false,
                        CoordinateStateCatalog.Missing,
                        "平台未提供坐标",
                        "平台未提供坐标，当前不进入上图。",
                        null,
                        null),
                    ["dev-004"] = new(
                        "dev-004",
                        false,
                        false,
                        CoordinateStateCatalog.RateLimited,
                        "坐标获取限频，稍后重试",
                        "坐标获取限频，稍后重试。",
                        null,
                        null)
                }),
            NullLogger<MapService>.Instance);

        var result = await service.LoadAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.All(result.Devices, device => Assert.Null(device.MapLatitude));
        var saved = Assert.Single(groupStore.UpdatedDevices);
        Assert.Null(saved.MapLatitude);
        Assert.Equal("dev-003", saved.DeviceCode);
        Assert.Equal(CoordinateStateCatalog.Missing, saved.CoordinateStatus);
    }

    private sealed class StubMapStore(IReadOnlyList<InspectionDevice> devices) : IMapStore
    {
        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(devices);
        }
    }

    private sealed class StubGroupSyncStore : IGroupSyncStore
    {
        public List<InspectionDevice> UpdatedDevices { get; } = [];

        public Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ReplaceSnapshotAsync(IReadOnlyCollection<InspectionGroup> groups, IReadOnlyCollection<InspectionDevice> devices, GroupSyncSnapshotMetadata metadata, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task ReplaceDevicesForGroupAsync(string groupId, IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task UpdateDevicePlatformDataAsync(IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            UpdatedDevices.Clear();
            UpdatedDevices.AddRange(devices);
            return Task.CompletedTask;
        }

        public Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyDictionary<string, DeviceUserMaintenance>> GetDeviceMaintenanceMapAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task SaveDeviceMaintenanceAsync(DeviceUserMaintenance maintenance, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubDeviceCoordinateService(IReadOnlyList<InspectionDevice> devices) : IDeviceCoordinateService
    {
        public Task<IReadOnlyList<InspectionDevice>> RefreshPlatformCoordinatesAsync(IReadOnlyList<InspectionDevice> sourceDevices, CancellationToken cancellationToken)
        {
            return Task.FromResult(devices);
        }

        public Task<InspectionDevice?> RefreshPlatformCoordinatesAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(devices.FirstOrDefault(device => string.Equals(device.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private sealed class StubCoordinateProjectionService(IReadOnlyDictionary<string, CoordinateProjectionResult> results) : ICoordinateProjectionService
    {
        public Task<IReadOnlyDictionary<string, CoordinateProjectionResult>> ProjectBd09ToGcj02Async(
            IReadOnlyCollection<CoordinateProjectionRequest> requests,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(results);
        }
    }
}
