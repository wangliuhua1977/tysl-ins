using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class GroupSyncServiceTests
{
    [Fact]
    public async Task SyncAsync_ReturnsRegionListFailure_WhenRootRegionListFails()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>
            {
                Success = false,
                EndpointName = "getReginWithGroupList",
                ErrorMessage = "region list failed"
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.False(summary.SnapshotReplaced);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetRegionListFailed, summary.Failures[0].FailureKind);
        Assert.Equal(0, summary.GroupCount);
    }

    [Fact]
    public async Task SyncAsync_KeepsPreviousSnapshot_WhenAnyDirectoryDevicePageFails()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = SuccessResult(
            [
                new OpenPlatformRegionDto("r1", "R-001", 0, 1, "目录1", 1, "GB-001")
            ]),
            RegionDevicePages =
            {
                [BuildPageKey("r1", 1)] = new OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>
                {
                    Success = false,
                    EndpointName = "getDeviceList",
                    ErrorMessage = "page 1 failed"
                }
            }
        };
        var store = new InMemoryGroupSyncStore();
        await store.ReplaceSnapshotAsync(
            [new InspectionGroup("seed-group", "旧目录", null, "SEED", 1, 1, false, true, null, DateTimeOffset.Parse("2026-03-28T09:00:00+08:00"))],
            [new InspectionDevice("seed-device", "旧设备", "seed-group", null, null, null, 1, 1, 1, 0, DateTimeOffset.Parse("2026-03-28T09:00:00+08:00"))],
            new GroupSyncSnapshotMetadata(1, 1, true, true, 1, 1, 1, "首层 regionCode：SEED"),
            CancellationToken.None);
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.False(summary.SnapshotReplaced);
        Assert.Equal(1, summary.GroupCount);
        Assert.Equal(1, summary.DeviceCount);
        Assert.Equal(0, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetRegionDeviceListFailed, summary.Failures[0].FailureKind);
    }

    [Fact]
    public async Task SyncAsync_RecursesAllDirectories_PagesAllDevices_AndReconcilesFirstLevelCounts()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = SuccessResult(
            [
                new OpenPlatformRegionDto("r1", "R-001", 1, 1, "一级目录A", 1, "GB-001"),
                new OpenPlatformRegionDto("r2", "R-002", 0, 0, "一级空目录", 1, "GB-002")
            ]),
            RegionResults =
            {
                ["r1"] = SuccessResult(
                [
                    new OpenPlatformRegionDto("r1-1", "R-001-01", 0, 1, "二级目录A-1", 2, "GB-001-01"),
                    new OpenPlatformRegionDto("r1-2", "R-001-02", 0, 0, "二级空目录A-2", 2, "GB-001-02")
                ])
            },
            RegionDevicePages =
            {
                [BuildPageKey("r1", 1)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d1", "设备1"),
                    new OpenPlatformRegionDeviceDto("d2", "设备2")
                ], 1, 2, 3),
                [BuildPageKey("r1", 2)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d3", "设备3")
                ], 2, 2, 3),
                [BuildPageKey("r1-1", 1)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d4", "设备4")
                ], 1, 50, 1)
            },
            RegionDeviceCountResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>
            {
                Success = true,
                EndpointName = "getCusDeviceCount",
                Payload =
                [
                    new OpenPlatformRegionDeviceCountDto("R-001", 4, 3),
                    new OpenPlatformRegionDeviceCountDto("R-002", 0, 0)
                ]
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);
        var groups = await store.GetGroupsAsync(CancellationToken.None);
        var devices = await store.GetDevicesAsync(CancellationToken.None);

        Assert.True(summary.SnapshotReplaced);
        Assert.Equal(4, summary.GroupCount);
        Assert.Equal(4, summary.DeviceCount);
        Assert.Equal(4, summary.SuccessCount);
        Assert.Equal(0, summary.FailureCount);
        Assert.True(summary.Metadata.ReconciliationCompleted);
        Assert.True(summary.Metadata.ReconciliationMatched);
        Assert.Equal(2, summary.Metadata.ReconciledRegionCount);
        Assert.Equal(4, summary.Metadata.ReconciledDeviceCount);
        Assert.Equal(3, summary.Metadata.ReconciledOnlineCount);

        var rootGroup = Assert.Single(groups, group => group.GroupId == "r1");
        Assert.Equal("R-001", rootGroup.RegionCode);
        Assert.True(rootGroup.HasChildren);
        Assert.True(rootGroup.HasDevice);
        Assert.Equal(3, rootGroup.DeviceCount);

        var childGroup = Assert.Single(groups, group => group.GroupId == "r1-1");
        Assert.Equal("r1", childGroup.ParentGroupId);
        Assert.Equal(2, childGroup.Level);
        Assert.Equal(1, childGroup.DeviceCount);

        var emptyGroup = Assert.Single(groups, group => group.GroupId == "r2");
        Assert.Equal(0, emptyGroup.DeviceCount);
        Assert.False(emptyGroup.HasChildren);
        Assert.False(emptyGroup.HasDevice);

        Assert.Equal(4, devices.Count);
        Assert.Contains(devices, device => device.DeviceCode == "d4");
    }

    [Fact]
    public async Task SyncAsync_ExplainsFirstLevelReconciliationDifferences()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = SuccessResult(
            [
                new OpenPlatformRegionDto("r1", "R-001", 0, 1, "交通要道", 1, "GB-001"),
                new OpenPlatformRegionDto("r2", "R-002", 0, 1, "我的设备", 1, "GB-002")
            ]),
            RegionDevicePages =
            {
                [BuildPageKey("r1", 1)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d1", "设备1"),
                    new OpenPlatformRegionDeviceDto("d2", "设备2"),
                    new OpenPlatformRegionDeviceDto("d3", "设备3"),
                    new OpenPlatformRegionDeviceDto("d4", "设备4"),
                    new OpenPlatformRegionDeviceDto("d5", "设备5"),
                    new OpenPlatformRegionDeviceDto("d6", "设备6"),
                    new OpenPlatformRegionDeviceDto("d7", "设备7")
                ], 1, 50, 7),
                [BuildPageKey("r2", 1)] = SuccessPage([], 1, 50, 0)
            },
            RegionDeviceCountResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>
            {
                Success = true,
                EndpointName = "getCusDeviceCount",
                Payload =
                [
                    new OpenPlatformRegionDeviceCountDto("R-001", 5, 4)
                ]
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.True(summary.SnapshotReplaced);
        Assert.True(summary.Metadata.ReconciliationCompleted);
        Assert.False(summary.Metadata.ReconciliationMatched);
        Assert.Contains("差异已解释", summary.Metadata.ReconciliationScopeText);
        Assert.Contains("平台未返回该 regionCode 对账记录", summary.Metadata.ReconciliationScopeText);
        Assert.Contains("双目子通道", summary.Metadata.ReconciliationScopeText);
        Assert.Contains("本地 7 / 平台 5", summary.Metadata.ReconciliationScopeText);
    }

    [Fact]
    public async Task SyncAsync_UsesReturnedDeviceList_WhenPlatformTotalCountIsLowerThanReturnedItems()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = SuccessResult(
            [
                new OpenPlatformRegionDto("r1", "R-001", 0, 1, "一级目录A", 1, "GB-001")
            ]),
            RegionDevicePages =
            {
                [BuildPageKey("r1", 1)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d1", "设备1"),
                    new OpenPlatformRegionDeviceDto("d2", "设备2")
                ], 1, 50, 1)
            },
            RegionDeviceCountResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>
            {
                Success = true,
                EndpointName = "getCusDeviceCount",
                Payload =
                [
                    new OpenPlatformRegionDeviceCountDto("R-001", 2, 1)
                ]
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);
        var devices = await store.GetDevicesAsync(CancellationToken.None);

        Assert.True(summary.SnapshotReplaced);
        Assert.Equal(1, summary.GroupCount);
        Assert.Equal(2, summary.DeviceCount);
        Assert.Equal(0, summary.FailureCount);
        Assert.True(summary.Metadata.ReconciliationCompleted);
        Assert.True(summary.Metadata.ReconciliationMatched);
        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public async Task SyncAsync_ReplacesSnapshot_WhenReconciliationFails_ButMarksMetadataIncomplete()
    {
        var client = new StubOpenPlatformClient
        {
            RootRegionResult = SuccessResult(
            [
                new OpenPlatformRegionDto("r1", "R-001", 0, 1, "一级目录A", 1, "GB-001")
            ]),
            RegionDevicePages =
            {
                [BuildPageKey("r1", 1)] = SuccessPage(
                [
                    new OpenPlatformRegionDeviceDto("d1", "设备1")
                ], 1, 50, 1)
            },
            RegionDeviceCountResult = new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>
            {
                Success = false,
                EndpointName = "getCusDeviceCount",
                ErrorMessage = "count failed"
            }
        };
        var store = new InMemoryGroupSyncStore();
        var service = new GroupSyncService(client, store, NullLogger<GroupSyncService>.Instance);

        var summary = await service.SyncAsync(CancellationToken.None);

        Assert.True(summary.SnapshotReplaced);
        Assert.Equal(1, summary.GroupCount);
        Assert.Equal(1, summary.DeviceCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(GroupSyncFailureKind.GetDeviceCountReconciliationFailed, summary.Failures[0].FailureKind);
        Assert.False(summary.Metadata.ReconciliationCompleted);
        Assert.Contains("count failed", summary.Metadata.ReconciliationScopeText);
    }

    private static string BuildPageKey(string regionId, int pageNo) => $"{regionId}::{pageNo}";

    private static OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>> SuccessResult(
        IReadOnlyList<OpenPlatformRegionDto> payload)
    {
        return new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>
        {
            Success = true,
            EndpointName = "getReginWithGroupList",
            Payload = payload
        };
    }

    private static OpenPlatformCallResult<OpenPlatformRegionDevicePageDto> SuccessPage(
        IReadOnlyList<OpenPlatformRegionDeviceDto> items,
        int pageNo,
        int pageSize,
        int totalCount)
    {
        return new OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>
        {
            Success = true,
            EndpointName = "getDeviceList",
            Payload = new OpenPlatformRegionDevicePageDto(items, pageNo, pageSize, totalCount)
        };
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

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>> RootRegionResult { get; set; } =
            SuccessResult([]);

        public Dictionary<string, OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> RegionResults { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> RegionDevicePages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>> RegionDeviceCountResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getCusDeviceCount",
                Payload = []
            };

        public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(AccessTokenResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(string regionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(string.IsNullOrWhiteSpace(regionId)
                ? RootRegionResult
                : RegionResults[regionId]);
        }

        public Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(
            string regionId,
            int pageNo,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RegionDevicePages[BuildPageKey(regionId, pageNo)]);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(
            string regionCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RegionDeviceCountResult);
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
        private readonly List<InspectionDevice> devices = [];
        private GroupSyncSnapshotMetadata metadata = GroupSyncSnapshotMetadata.Empty;

        public Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
        {
            this.groups.Clear();
            this.groups.AddRange(groups);
            return Task.CompletedTask;
        }

        public Task ReplaceSnapshotAsync(
            IReadOnlyCollection<InspectionGroup> groups,
            IReadOnlyCollection<InspectionDevice> devices,
            GroupSyncSnapshotMetadata metadata,
            CancellationToken cancellationToken)
        {
            this.groups.Clear();
            this.groups.AddRange(groups);
            this.devices.Clear();
            this.devices.AddRange(devices);
            this.metadata = metadata;
            return Task.CompletedTask;
        }

        public Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
        {
            var validGroups = groups.Select(group => group.GroupId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            devices.RemoveAll(device => !validGroups.Contains(device.GroupId));
            return Task.CompletedTask;
        }

        public Task ReplaceDevicesForGroupAsync(string groupId, IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            this.devices.RemoveAll(device => string.Equals(device.GroupId, groupId, StringComparison.OrdinalIgnoreCase));
            this.devices.AddRange(devices);
            return Task.CompletedTask;
        }

        public Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken)
        {
            var lastSyncedAt = groups.Select(group => group.SyncedAt)
                .Concat(devices.Select(device => device.SyncedAt))
                .DefaultIfEmpty()
                .Max();

            return Task.FromResult(new OverviewStats(
                devices.Count,
                devices.Count(device => device.OnlineStatus == 1),
                devices.Count(device => device.OnlineStatus == 0),
                devices.Count(device => string.IsNullOrWhiteSpace(device.Latitude) || string.IsNullOrWhiteSpace(device.Longitude)),
                lastSyncedAt == default ? null : lastSyncedAt));
        }

        public Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InspectionGroup>>(groups.ToArray());
        }

        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<InspectionDevice>>(devices.ToArray());
        }

        public Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
        {
            var lastSyncedAt = groups.Select(group => group.SyncedAt)
                .Concat(devices.Select(device => device.SyncedAt))
                .DefaultIfEmpty()
                .Max();

            return Task.FromResult(new LocalSyncSnapshot(
                groups.Count,
                devices.Count,
                lastSyncedAt == default ? null : lastSyncedAt,
                metadata));
        }
    }
}
