using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class PreviewServiceTests
{
    [Fact]
    public async Task LoadLocalDevicesAsync_ReturnsMonitorRegionTree()
    {
        var store = new StubGroupSyncStore();
        var service = CreateService(groupStore: store);

        var result = await service.LoadLocalDevicesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Devices);
        var group = Assert.Single(result.DirectoryGroups);
        Assert.Equal("默认目录", group.GroupName);
        Assert.Equal("层级 1 / 根目录", group.HierarchyText);
        Assert.Equal(1, group.ReportedDeviceCount);
        Assert.True(group.CountMatches);
        var device = Assert.Single(group.Devices);
        Assert.Equal("测试设备", device.DeviceName);
        Assert.Equal("在线", device.OnlineStatusText);
        Assert.Equal(1, result.SnapshotGroupCount);
        Assert.Equal(1, result.SnapshotDeviceCount);
        Assert.Equal(1, result.Metadata.PlatformGroupCount);
        Assert.Equal(1, result.Metadata.PlatformDeviceCount);
        Assert.Single(result.DeviceDetailsByCode);
        Assert.True(result.DeviceDetailsByCode.TryGetValue("dev-001", out var deviceDetail));
        Assert.Equal("31.2304", deviceDetail.Latitude);
        Assert.Equal("121.4737", deviceDetail.Longitude);
        Assert.Equal("上海", deviceDetail.Location);
        Assert.True(result.Metadata.ReconciliationCompleted);
        Assert.True(result.Metadata.ReconciliationMatched);
        Assert.Equal(DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"), result.LastSyncedAt);
    }

    [Fact]
    public async Task LoadLocalDevicesAsync_KeepsUnlocatedDevices_AndEmptyDirectories()
    {
        var store = new StubGroupSyncStore
        {
            Groups =
            [
                new InspectionGroup("group-001", "默认目录", null, "R-001", 2, 1, true, true, null, DateTimeOffset.Parse("2026-03-28T09:58:00+08:00")),
                new InspectionGroup("group-001-01", "二级空目录", "group-001", "R-001-01", 0, 2, false, false, null, DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"))
            ],
            Devices =
            [
                new InspectionDevice("dev-001", "测试设备", "group-001", "31.2304", "121.4737", "上海", 1, 1, 1, 0, DateTimeOffset.UtcNow),
                new InspectionDevice("dev-002", "无坐标设备", "group-001", null, null, "未定位", 0, 1, 0, 0, DateTimeOffset.UtcNow)
            ],
            Metadata = new GroupSyncSnapshotMetadata(2, 2, true, true, 1, 2, 1, "首层 regionCode：R-001")
        };
        var service = CreateService(groupStore: store);

        var result = await service.LoadLocalDevicesAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.SnapshotGroupCount);
        Assert.Equal(2, result.SnapshotDeviceCount);
        Assert.Equal(2, result.Metadata.PlatformDeviceCount);
        var rootGroup = Assert.Single(result.DirectoryGroups, group => group.GroupId == "group-001");
        Assert.Equal(2, rootGroup.Devices.Count);
        Assert.Contains(rootGroup.Devices, device => device.DeviceCode == "dev-002");
        var emptyGroup = Assert.Single(result.DirectoryGroups, group => group.GroupId == "group-001-01");
        Assert.Empty(emptyGroup.Devices);
        Assert.Equal("层级 2 / 上级：默认目录", emptyGroup.HierarchyText);
        Assert.False(string.IsNullOrWhiteSpace(emptyGroup.EmptyStateText));
    }

    [Fact]
    public async Task LoadLocalDevicesAsync_UsesSameDeviceTotalAsOverviewStats()
    {
        var store = new StubGroupSyncStore
        {
            Groups =
            [
                new InspectionGroup("group-001", "默认目录", null, "R-001", 2, 1, false, true, null, DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"))
            ],
            Devices =
            [
                new InspectionDevice("dev-001", "测试设备", "group-001", "31.2304", "121.4737", "上海", 1, 1, 1, 0, DateTimeOffset.UtcNow),
                new InspectionDevice("dev-002", "无坐标设备", "group-001", null, null, "未定位", 0, 1, 0, 0, DateTimeOffset.UtcNow)
            ],
            Metadata = new GroupSyncSnapshotMetadata(1, 2, true, true, 1, 2, 1, "首层 regionCode：R-001")
        };
        var service = CreateService(groupStore: store);
        var overviewStatsService = new OverviewStatsService(store);

        var result = await service.LoadLocalDevicesAsync(CancellationToken.None);
        var stats = await overviewStatsService.GetAsync(CancellationToken.None);

        Assert.Equal(stats.TotalPoints, result.Devices.Count);
        Assert.Equal(stats.TotalPoints, result.SnapshotDeviceCount);
    }

    [Fact]
    public async Task PrepareAsync_ReturnsOfflineDiagnosis_WithoutRequestingPreviewUrl()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 0)
            }
        };
        var service = CreateService(client);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("设备离线，无法获取预览地址", result.DiagnosisText);
        Assert.Equal("未发起预览地址获取", result.AddressStatusText);
        Assert.False(client.PreviewUrlRequested);
    }

    [Fact]
    public async Task PrepareAsync_ReturnsSleepDiagnosis_WithoutRequestingPreviewUrl()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 2)
            }
        };
        var service = CreateService(client);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("设备休眠，当前不进入预览", result.DiagnosisText);
        Assert.False(client.PreviewUrlRequested);
    }

    [Fact]
    public async Task PrepareAsync_ReturnsStatusFailureMessage_WhenStatusQueryFails()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = false,
                EndpointName = "getDeviceStatus",
                ErrorMessage = "接口超时"
            }
        };
        var service = CreateService(client);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("设备状态查询失败", result.DiagnosisText);
        Assert.False(client.PreviewUrlRequested);
    }

    [Fact]
    public async Task PrepareAsync_ReturnsPreviewUrl_WhenDeviceIsOnline()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            },
            PreviewUrlResult = new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = true,
                EndpointName = "getDeviceMediaUrlRtsp",
                Payload = new OpenPlatformPreviewUrlPayload("rtsp://demo/live/dev-001", "600 秒")
            }
        };
        var service = CreateService(client);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("在线：可请求预览地址", result.DiagnosisText);
        Assert.Equal("预览地址已就绪", result.AddressStatusText);
        Assert.Equal("rtsp://demo/live/dev-001", result.RtspUrl);
        Assert.True(client.PreviewUrlRequested);
    }

    [Fact]
    public async Task PrepareAsync_PassesThroughRtspStageMessage_WhenPreviewUrlQueryFails()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            },
            PreviewUrlResult = new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = false,
                EndpointName = "getDeviceMediaUrlRtsp",
                ErrorMessage = "RTSP 响应解密失败"
            }
        };
        var service = CreateService(client);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("在线：可请求预览地址", result.DiagnosisText);
        Assert.Equal("RTSP 响应解密失败", result.AddressStatusText);
        Assert.True(client.PreviewUrlRequested);
    }

    [Fact]
    public async Task InspectAsync_ReturnsOfflineConclusion_WhenDeviceIsOffline()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 0)
            }
        };
        var probe = new StubPlayProbe();
        var service = CreateService(client, probe: probe);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.Equal("离线", result.OnlineStatus);
        Assert.False(result.RtspReady);
        Assert.False(result.PlaybackStarted);
        Assert.False(result.EnteredPlaying);
        Assert.Equal("巡检失败：设备离线", result.Conclusion);
        Assert.Equal("设备离线", result.FailureCategory);
        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.Offline, result.AbnormalClass);
        Assert.Equal("离线", result.AbnormalClassText);
        var summary = result.BuildDispositionSummary();
        Assert.Contains("结论：巡检失败：设备离线", summary);
        Assert.Contains("前置归类：离线", summary);
        Assert.Contains("失败分类：设备离线", summary);
        Assert.False(client.PreviewUrlRequested);
        Assert.False(probe.ProbeRequested);
    }

    [Fact]
    public async Task InspectAsync_ReturnsRtspNotReadyConclusion_WhenPreviewUrlIsNotReady()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            },
            PreviewUrlResult = new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = false,
                EndpointName = "getDeviceMediaUrlRtsp",
                ErrorMessage = "RTSP 响应解密失败"
            }
        };
        var probe = new StubPlayProbe();
        var service = CreateService(client, probe: probe);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.Equal("在线", result.OnlineStatus);
        Assert.False(result.RtspReady);
        Assert.Equal("巡检失败：RTSP 未就绪", result.Conclusion);
        Assert.Equal("RTSP 响应解密失败", result.FailureCategory);
        Assert.Equal("RTSP 响应解密失败", result.DetailMessage);
        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.RtspNotReady, result.AbnormalClass);
        Assert.Equal("RTSP 未就绪", result.AbnormalClassText);
        var summary = result.BuildDispositionSummary();
        Assert.Contains("结论：巡检失败：RTSP 未就绪", summary);
        Assert.Contains("前置归类：RTSP 未就绪", summary);
        Assert.Contains("失败分类：RTSP 响应解密失败", summary);
        Assert.True(client.PreviewUrlRequested);
        Assert.False(probe.ProbeRequested);
    }

    [Fact]
    public async Task InspectAsync_DoesNotEmitExtraAbnormalText_WhenStatusIsNotResolved()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = false,
                EndpointName = "getDeviceStatus",
                ErrorMessage = "接口超时"
            }
        };
        var probe = new StubPlayProbe();
        var service = CreateService(client, probe: probe);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.None, result.AbnormalClass);
        Assert.Equal(string.Empty, result.AbnormalClassText);
        Assert.Equal("巡检失败：设备状态未获取", result.Conclusion);
        Assert.False(probe.ProbeRequested);
    }

    [Fact]
    public async Task InspectAsync_ReturnsPlayFailedClass_WhenProbeFailsBeforePlaying()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            },
            PreviewUrlResult = new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = true,
                EndpointName = "getDeviceMediaUrlRtsp",
                Payload = new OpenPlatformPreviewUrlPayload("rtsp://demo/live/dev-001", "600 秒")
            }
        };
        var probe = new StubPlayProbe
        {
            ProbeResult = new PlayProbeResult(
                true,
                false,
                "播放建链失败",
                "播放器未能完成播放建链。")
        };
        var service = CreateService(client, probe: probe);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.True(result.RtspReady);
        Assert.True(result.PlaybackStarted);
        Assert.False(result.EnteredPlaying);
        Assert.Equal("巡检失败：播放失败", result.Conclusion);
        Assert.Equal("播放建链失败", result.FailureCategory);
        Assert.Equal("播放器未能完成播放建链。", result.DetailMessage);
        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.PlayFailed, result.AbnormalClass);
        Assert.Equal("播放失败", result.AbnormalClassText);
        var summary = result.BuildDispositionSummary();
        Assert.Contains("结论：巡检失败：播放失败", summary);
        Assert.Contains("前置归类：播放失败", summary);
        Assert.Contains("失败分类：播放建链失败", summary);
        Assert.True(probe.ProbeRequested);
    }

    [Fact]
    public async Task InspectAsync_ReturnsPassConclusion_WhenProbeEntersPlaying()
    {
        var client = new StubOpenPlatformClient
        {
            DeviceStatusResult = new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            },
            PreviewUrlResult = new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = true,
                EndpointName = "getDeviceMediaUrlRtsp",
                Payload = new OpenPlatformPreviewUrlPayload("rtsp://demo/live/dev-001", "600 秒")
            }
        };
        var probe = new StubPlayProbe
        {
            ProbeResult = new PlayProbeResult(
                true,
                true,
                string.Empty,
                "播放器已进入 Playing 播放态。")
        };
        var service = CreateService(client, probe: probe);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.Equal("在线", result.OnlineStatus);
        Assert.True(result.RtspReady);
        Assert.True(result.PlaybackStarted);
        Assert.True(result.EnteredPlaying);
        Assert.Equal("巡检通过", result.Conclusion);
        Assert.Equal(string.Empty, result.FailureCategory);
        Assert.False(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.None, result.AbnormalClass);
        Assert.Equal("无异常/巡检通过", result.AbnormalClassText);
        var summary = result.BuildDispositionSummary();
        Assert.Contains("结论：巡检通过", summary);
        Assert.Contains("前置归类：无异常/巡检通过", summary);
        Assert.DoesNotContain("失败分类：", summary);
        Assert.True(probe.ProbeRequested);
    }

    [Fact]
    public void BuildDispositionSummary_MasksFullRtspAddress_WhenDetailContainsRtsp()
    {
        var result = new InspectResult(
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
            "测试设备",
            "dev-001",
            true,
            "在线",
            true,
            true,
            false,
            "巡检失败：播放失败",
            "播放建链失败",
            "播放器输出原始地址 rtsp://demo/live/dev-001?token=abc 后建链失败。",
            InspectAbnormalClass.PlayFailed);

        var summary = result.BuildDispositionSummary();

        Assert.DoesNotContain("rtsp://demo/live/dev-001?token=abc", summary);
        Assert.Contains("RTSP 地址不展示明文", summary);
    }

    private static PreviewService CreateService(
        StubOpenPlatformClient? client = null,
        StubGroupSyncStore? groupStore = null,
        StubPlayProbe? probe = null)
    {
        return new PreviewService(
            groupStore ?? new StubGroupSyncStore(),
            client ?? new StubOpenPlatformClient(),
            probe ?? new StubPlayProbe(),
            NullLogger<PreviewService>.Instance);
    }

    private sealed class StubOpenPlatformClient : IOpenPlatformClient
    {
        public bool PreviewUrlRequested { get; private set; }

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

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>> RegionListResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getReginWithGroupList",
                Payload = []
            };

        public OpenPlatformCallResult<OpenPlatformRegionDevicePageDto> RegionDevicePageResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getDeviceList",
                Payload = new OpenPlatformRegionDevicePageDto([], 1, 50, 0)
            };

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>> RegionDeviceCountResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getCusDeviceCount",
                Payload = []
            };

        public OpenPlatformCallResult<OpenPlatformDeviceStatusPayload> DeviceStatusResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getDeviceStatus",
                Payload = new OpenPlatformDeviceStatusPayload("dev-001", 1)
            };

        public OpenPlatformCallResult<OpenPlatformPreviewUrlPayload> PreviewUrlResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getDeviceMediaUrlRtsp",
                Payload = new OpenPlatformPreviewUrlPayload("rtsp://demo/live/dev-001", "600 秒")
            };

        public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(AccessTokenResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(string regionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(RegionListResult);
        }

        public Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(
            string regionId,
            int pageNo,
            int pageSize,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RegionDevicePageResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(
            string regionCode,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RegionDeviceCountResult);
        }

        public Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(DeviceStatusResult);
        }

        public Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(string deviceCode, CancellationToken cancellationToken)
        {
            PreviewUrlRequested = true;
            return Task.FromResult(PreviewUrlResult);
        }
    }

    private sealed class StubPlayProbe : IPlayProbe
    {
        public bool ProbeRequested { get; private set; }

        public PlayProbeResult ProbeResult { get; set; } = new(
            true,
            true,
            string.Empty,
            "播放器已进入 Playing 播放态。");

        public Task<PlayProbeResult> ProbeAsync(PlayProbeArgs args, CancellationToken cancellationToken)
        {
            ProbeRequested = true;
            return Task.FromResult(ProbeResult);
        }
    }

    private sealed class StubGroupSyncStore : IGroupSyncStore
    {
        public IReadOnlyList<InspectionGroup> Groups { get; set; } =
        [
            new InspectionGroup("group-001", "默认目录", null, "R-001", 1, 1, false, true, null, DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"))
        ];

        public IReadOnlyList<InspectionDevice> Devices { get; set; } =
        [
            new InspectionDevice(
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
                DateTimeOffset.Parse("2026-03-28T09:58:00+08:00"))
        ];

        public GroupSyncSnapshotMetadata Metadata { get; set; } =
            new(1, 1, true, true, 1, 1, 1, "首层 regionCode：R-001");

        public Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken)
        {
            Groups = groups.ToArray();
            return Task.CompletedTask;
        }

        public Task ReplaceSnapshotAsync(
            IReadOnlyCollection<InspectionGroup> groups,
            IReadOnlyCollection<InspectionDevice> devices,
            GroupSyncSnapshotMetadata metadata,
            CancellationToken cancellationToken)
        {
            Groups = groups.ToArray();
            Devices = devices.ToArray();
            Metadata = metadata;
            return Task.CompletedTask;
        }

        public Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ReplaceDevicesForGroupAsync(string groupId, IReadOnlyCollection<InspectionDevice> devices, CancellationToken cancellationToken)
        {
            var updated = Devices
                .Where(device => !string.Equals(device.GroupId, groupId, StringComparison.OrdinalIgnoreCase))
                .Concat(devices)
                .ToArray();
            Devices = updated;
            return Task.CompletedTask;
        }

        public Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new OverviewStats(
                Devices.Count,
                Devices.Count(device => device.OnlineStatus == 1),
                Devices.Count(device => device.OnlineStatus == 0),
                Devices.Count(device => string.IsNullOrWhiteSpace(device.Latitude) || string.IsNullOrWhiteSpace(device.Longitude)),
                GetLastSyncedAt()));
        }

        public Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Groups);
        }

        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(Devices);
        }

        public Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new LocalSyncSnapshot(Groups.Count, Devices.Count, GetLastSyncedAt(), Metadata));
        }

        private DateTimeOffset? GetLastSyncedAt()
        {
            var lastSyncedAt = Groups.Select(group => group.SyncedAt)
                .Concat(Devices.Select(device => device.SyncedAt))
                .DefaultIfEmpty()
                .Max();
            return lastSyncedAt == default ? null : lastSyncedAt;
        }
    }
}
