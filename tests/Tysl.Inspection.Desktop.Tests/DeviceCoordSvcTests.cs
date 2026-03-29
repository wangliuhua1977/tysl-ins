using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class DeviceCoordSvcTests
{
    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_UsesDeviceInfoByDeviceCodeAsPrimarySource()
    {
        var store = new StubGroupSyncStore();
        var client = new StubOpenPlatformClient
        {
            DeviceInfoResult = BuildSuccessResult("31.2304", "121.4737")
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, client.DeviceInfoRequestCount);
        Assert.Equal("31.2304", result!.RawLatitude);
        Assert.Equal("121.4737", result.RawLongitude);
        Assert.Equal("platform", result.CoordinateSource);
        Assert.Equal(CoordinateStateCatalog.Available, result.CoordinateStatus);
        Assert.Equal("31.2304", store.Devices["dev-001"].RawLatitude);
        Assert.Equal("121.4737", store.Devices["dev-001"].RawLongitude);
    }

    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_MarksMissing_WhenPlatformCoordinatesAreEmpty()
    {
        var store = new StubGroupSyncStore();
        var client = new StubOpenPlatformClient
        {
            DeviceInfoResult = BuildSuccessResult(null, null)
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, client.DeviceInfoRequestCount);
        Assert.Null(result!.RawLatitude);
        Assert.Null(result.RawLongitude);
        Assert.Equal("none", result.CoordinateSource);
        Assert.Equal(CoordinateStateCatalog.Missing, result.CoordinateStatus);
        Assert.Equal(CoordinateStateCatalog.Missing, store.Devices["dev-001"].CoordinateStatus);
    }

    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_UsesCachedCoordinate_WhenRawCoordinateAlreadyExists()
    {
        var store = new StubGroupSyncStore
        {
            Devices =
            {
                ["dev-001"] = new InspectionDevice(
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
                    "平台原始坐标来自 getDeviceInfoByDeviceCode。")
                {
                    MapLatitude = "31.224361",
                    MapLongitude = "121.469170"
                }
            }
        };
        var client = new StubOpenPlatformClient();
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, client.DeviceInfoRequestCount);
        Assert.Equal("31.2304", result!.RawLatitude);
        Assert.Equal("121.469170", result.MapLongitude);
        Assert.Equal(CoordinateStateCatalog.Available, result.CoordinateStatus);
    }

    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_DeduplicatesConcurrentRequests_ForSameDeviceCode()
    {
        var store = new StubGroupSyncStore();
        var gate = new TaskCompletionSource<OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubOpenPlatformClient
        {
            PendingDeviceInfoResult = gate.Task
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var firstTask = service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);
        await Task.Delay(50);
        var secondTask = service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, client.DeviceInfoRequestCount);

        gate.SetResult(BuildSuccessResult("31.2304", "121.4737"));

        var results = await Task.WhenAll(firstTask, secondTask);

        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.Equal(CoordinateStateCatalog.Available, result!.CoordinateStatus);
        });
        Assert.Equal(1, client.DeviceInfoRequestCount);
    }

    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_Maps30041ToRateLimited()
    {
        var store = new StubGroupSyncStore();
        var client = new StubOpenPlatformClient
        {
            DeviceInfoResult = new OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>
            {
                Success = false,
                EndpointName = "getDeviceInfoByDeviceCode",
                PlatformCode = "30041",
                PlatformMessage = "请求过于频繁",
                ErrorMessage = "30041 请求过于频繁"
            }
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(CoordinateStateCatalog.RateLimited, result!.CoordinateStatus);
        Assert.Contains("限频", result.CoordinateStatusMessage);
        Assert.Equal(CoordinateStateCatalog.RateLimited, store.Devices["dev-001"].CoordinateStatus);
    }

    private static OpenPlatformCallResult<OpenPlatformDeviceInfoPayload> BuildSuccessResult(string? latitude, string? longitude)
    {
        return new OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>
        {
            Success = true,
            EndpointName = "getDeviceInfoByDeviceCode",
            Payload = new OpenPlatformDeviceInfoPayload("dev-001", "测试设备", latitude, longitude, "上海")
        };
    }

    private sealed class StubOpenPlatformClient : IOpenPlatformClient
    {
        public int DeviceInfoRequestCount { get; private set; }

        public Task<OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>>? PendingDeviceInfoResult { get; set; }

        public OpenPlatformCallResult<OpenPlatformDeviceInfoPayload> DeviceInfoResult { get; set; } =
            BuildSuccessResult("31.2304", "121.4737");

        public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(string regionId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(string regionId, int pageNo, int pageSize, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(string regionCode, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>> GetDeviceInfoByDeviceCodeAsync(string deviceCode, CancellationToken cancellationToken)
        {
            DeviceInfoRequestCount++;
            return PendingDeviceInfoResult ?? Task.FromResult(DeviceInfoResult);
        }

        public Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(string deviceCode, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(string deviceCode, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubGroupSyncStore : IGroupSyncStore
    {
        public Dictionary<string, InspectionDevice> Devices { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dev-001"] = new(
                "dev-001",
                "测试设备",
                "group-001",
                null,
                null,
                null,
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"))
        };

        public Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReplaceSnapshotAsync(IReadOnlyCollection<InspectionGroup> groups, IReadOnlyCollection<InspectionDevice> devices, GroupSyncSnapshotMetadata metadata, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReplaceDevicesForGroupAsync(string groupId, IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task UpdateDevicePlatformDataAsync(IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            foreach (var device in devices)
            {
                Devices[device.DeviceCode] = device;
            }

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
            return Task.FromResult<IReadOnlyList<InspectionDevice>>(Devices.Values.ToArray());
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
}
