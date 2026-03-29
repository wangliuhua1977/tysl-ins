using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class GroupSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_ReturnsGroupListFailure_WhenGroupListFails()
    {
        var client = new StubOpenPlatformClient
        {
            GroupListResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>
            {
                Success = false,
                EndpointName = "getGroupList",
                ErrorMessage = "group list failed"
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetGroupListFailed, summary.Failures[0].FailureKind);
        Assert.Equal(0, summary.GroupCount);
    }

    [Fact]
    public async Task SyncAsync_KeepsPreviousSnapshot_WhenAnyGroupDeviceListFails()
    {
        var client = new StubOpenPlatformClient
        {
            GroupListResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>
            {
                Success = true,
                EndpointName = "getGroupList",
                Payload =
                [
                    new OpenPlatformGroupDto("g1", "组1", 1),
                    new OpenPlatformGroupDto("g2", "组2", 1)
                ]
            },
            GroupDeviceResults =
            {
                ["g1"] = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>
                {
                    Success = true,
                    EndpointName = "getGroupDeviceList",
                    Payload = [new OpenPlatformDeviceDto("d1", "设备1", "g1", null, null, null, 1, 1, 1, 0)]
                },
                ["g2"] = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>
                {
                    Success = false,
                    EndpointName = "getGroupDeviceList",
                    ErrorMessage = "device list failed"
                }
            }
        };
        var store = new InMemoryGroupSyncStore();
        await store.ReplaceSnapshotAsync(
            [new InspectionGroup("seed-group", "旧分组", 1, DateTimeOffset.Parse("2026-03-28T09:00:00+08:00"))],
            [new InspectionDevice("seed-device", "旧设备", "seed-group", null, null, null, 1, 1, 1, 0, DateTimeOffset.Parse("2026-03-28T09:00:00+08:00"))],
            CancellationToken.None);
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(1, summary.GroupCount);
        Assert.Equal(1, summary.DeviceCount);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetGroupDeviceListFailed, summary.Failures[0].FailureKind);
    }

    [Fact]
    public async Task SyncAsync_ReplacesWholeSnapshot_WhenAllGroupsAndDevicesAreFetched()
    {
        var client = new StubOpenPlatformClient
        {
            GroupListResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>
            {
                Success = true,
                EndpointName = "getGroupList",
                Payload =
                [
                    new OpenPlatformGroupDto("g1", "组1", 2),
                    new OpenPlatformGroupDto("g2", "组2", 1)
                ]
            },
            GroupDeviceResults =
            {
                ["g1"] = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>
                {
                    Success = true,
                    EndpointName = "getGroupDeviceList",
                    Payload =
                    [
                        new OpenPlatformDeviceDto("d1", "设备1", "g1", "31.23", "121.47", "上海", 1, 1, 1, 0),
                        new OpenPlatformDeviceDto("d2", "设备2", "g1", null, null, "未定位", 0, 1, 0, 0)
                    ]
                },
                ["g2"] = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>
                {
                    Success = true,
                    EndpointName = "getGroupDeviceList",
                    Payload = [new OpenPlatformDeviceDto("d3", "设备3", "g2", "39.90", "116.40", "北京", 1, 1, 1, 0)]
                }
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);
        var groups = await store.GetGroupsAsync(CancellationToken.None);
        var devices = await store.GetDevicesAsync(CancellationToken.None);

        Assert.Equal(2, summary.GroupCount);
        Assert.Equal(3, summary.DeviceCount);
        Assert.Equal(2, summary.SuccessCount);
        Assert.Equal(0, summary.FailureCount);
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, devices.Count);
        Assert.Contains(devices, device => device.DeviceCode == "d2");
    }

    private sealed class StubOpenPlatformClient : IOpenPlatformClient
    {
        public OpenPlatformCallResult<OpenPlatformAccessTokenPayload> AccessTokenResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getAccessToken",
                Payload = new OpenPlatformAccessTokenPayload(
                    "token",
                    "refresh",
                    3600,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow.AddHours(1),
                    DateTimeOffset.UtcNow.AddDays(30))
            };

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>> GroupListResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getGroupList",
                Payload = []
            };

        public Dictionary<string, OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GroupDeviceResults { get; } = new();

        public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(AccessTokenResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>> GetGroupListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(GroupListResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GetGroupDeviceListAsync(string groupId, CancellationToken cancellationToken)
        {
            return Task.FromResult(GroupDeviceResults[groupId]);
        }

        public Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload(deviceCode, 1)
            });
        }

        public Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = true,
                EndpointName = "getDeviceMediaUrlRtsp",
                Payload = new OpenPlatformPreviewUrlPayload($"rtsp://demo/live/{deviceCode}", "600 秒")
            });
        }
    }

    private sealed class InMemoryGroupSyncStore : IGroupSyncStore
    {
        private readonly List<InspectionGroup> groups = [];
        private readonly Dictionary<string, List<InspectionDevice>> devicesByGroup = [];

        public Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
        {
            this.groups.Clear();
            this.groups.AddRange(groups);
            return Task.CompletedTask;
        }

        public Task ReplaceSnapshotAsync(
            IReadOnlyCollection<InspectionGroup> groups,
            IReadOnlyCollection<InspectionDevice> devices,
            CancellationToken cancellationToken)
        {
            this.groups.Clear();
            this.groups.AddRange(groups);
            devicesByGroup.Clear();

            foreach (var group in groups)
            {
                devicesByGroup[group.GroupId] = devices
                    .Where(device => string.Equals(device.GroupId, group.GroupId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return Task.CompletedTask;
        }

        public Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
        {
            var validGroups = groups.Select(group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var orphan in devicesByGroup.Keys.Where(key => !validGroups.Contains(key)).ToArray())
            {
                devicesByGroup.Remove(orphan);
            }

            return Task.CompletedTask;
        }

        public Task ReplaceDevicesForGroupAsync(string groupId, IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            devicesByGroup[groupId] = devices.ToList();
            return Task.CompletedTask;
        }

        public Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken)
        {
            var allDevices = devicesByGroup.Values.SelectMany(list => list).ToArray();
            var lastSyncedAt = groups.Select(group => group.SyncedAt)
                .Concat(allDevices.Select(device => device.SyncedAt))
                .DefaultIfEmpty()
                .Max();

            return Task.FromResult(new OverviewStats(
                allDevices.Length,
                allDevices.Count(device => device.OnlineStatus == 1),
                allDevices.Count(device => device.OnlineStatus == 0),
                allDevices.Count(device => string.IsNullOrWhiteSpace(device.Latitude) || string.IsNullOrWhiteSpace(device.Longitude)),
                lastSyncedAt == default ? null : lastSyncedAt));
        }

        public Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InspectionGroup>>(groups.ToArray());
        }

        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InspectionDevice>>(devicesByGroup.Values.SelectMany(list => list).ToArray());
        }

        public Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
        {
            var allDevices = devicesByGroup.Values.SelectMany(list => list).ToArray();
            var lastSyncedAt = groups.Select(group => group.SyncedAt)
                .Concat(allDevices.Select(device => device.SyncedAt))
                .DefaultIfEmpty()
                .Max();

            return Task.FromResult(new LocalSyncSnapshot(
                groups.Count,
                allDevices.Length,
                lastSyncedAt == default ? null : lastSyncedAt));
        }
    }
}
