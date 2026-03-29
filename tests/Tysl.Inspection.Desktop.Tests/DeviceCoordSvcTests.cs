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
            DeviceInfoResult = new OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>
            {
                Success = true,
                EndpointName = "getDeviceInfoByDeviceCode",
                Payload = new OpenPlatformDeviceInfoPayload("dev-001", "测试设备", "31.2304", "121.4737", "上海")
            }
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(client.DeviceInfoRequested);
        Assert.Equal("31.2304", result!.RawLatitude);
        Assert.Equal("121.4737", result.RawLongitude);
        Assert.Equal("platform", result.CoordinateSource);
        Assert.Equal("available", result.CoordinateStatus);
        Assert.Equal("31.2304", store.Devices["dev-001"].RawLatitude);
        Assert.Equal("121.4737", store.Devices["dev-001"].RawLongitude);
    }

    [Fact]
    public async Task RefreshPlatformCoordinatesAsync_MarksMissing_WhenPlatformCoordinatesAreEmpty()
    {
        var store = new StubGroupSyncStore();
        var client = new StubOpenPlatformClient
        {
            DeviceInfoResult = new OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>
            {
                Success = true,
                EndpointName = "getDeviceInfoByDeviceCode",
                Payload = new OpenPlatformDeviceInfoPayload("dev-001", "测试设备", null, null, "上海")
            }
        };
        var service = new DeviceCoordSvc(client, store, NullLogger<DeviceCoordSvc>.Instance);

        var result = await service.RefreshPlatformCoordinatesAsync("dev-001", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(client.DeviceInfoRequested);
        Assert.Null(result!.RawLatitude);
        Assert.Null(result.RawLongitude);
        Assert.Equal("none", result.CoordinateSource);
        Assert.Equal("missing", result.CoordinateStatus);
        Assert.Equal("missing", store.Devices["dev-001"].CoordinateStatus);
    }

    private sealed class StubOpenPlatformClient : IOpenPlatformClient
    {
        public bool DeviceInfoRequested { get; private set; }

        public OpenPlatformCallResult<OpenPlatformDeviceInfoPayload> DeviceInfoResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getDeviceInfoByDeviceCode",
                Payload = new OpenPlatformDeviceInfoPayload("dev-001", "测试设备", "31.2304", "121.4737", "上海")
            };

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
            DeviceInfoRequested = true;
            return Task.FromResult(DeviceInfoResult);
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
