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
    public async Task SyncAsync_TracksGroupDeviceFailures_WithoutDiscardingSuccessfulGroups()
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
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(2, summary.GroupCount);
        Assert.Equal(1, summary.DeviceCount);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetGroupDeviceListFailed, summary.Failures[0].FailureKind);
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
