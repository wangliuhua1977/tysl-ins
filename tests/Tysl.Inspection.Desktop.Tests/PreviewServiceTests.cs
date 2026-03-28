using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class PreviewServiceTests
{
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
        var service = new PreviewService(new InMemoryMapStore(), client, new StubPlayProbe(), NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, new StubPlayProbe(), NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, new StubPlayProbe(), NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, new StubPlayProbe(), NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, new StubPlayProbe(), NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, probe, NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, probe, NullLogger<PreviewService>.Instance);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.Equal("在线", result.OnlineStatus);
        Assert.False(result.RtspReady);
        Assert.Equal("巡检失败：RTSP 地址未就绪", result.Conclusion);
        Assert.Equal("RTSP 响应解密失败", result.FailureCategory);
        Assert.Equal("RTSP 响应解密失败", result.DetailMessage);
        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.RtspNotReady, result.AbnormalClass);
        Assert.Equal("RTSP 未就绪", result.AbnormalClassText);
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
        var service = new PreviewService(new InMemoryMapStore(), client, probe, NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, probe, NullLogger<PreviewService>.Instance);

        var result = await service.InspectAsync("dev-001", CancellationToken.None);

        Assert.True(result.StatusResolved);
        Assert.True(result.RtspReady);
        Assert.True(result.PlaybackStarted);
        Assert.False(result.EnteredPlaying);
        Assert.Equal("巡检失败：播放建链失败", result.Conclusion);
        Assert.Equal("播放建链失败", result.FailureCategory);
        Assert.Equal("播放器未能完成播放建链。", result.DetailMessage);
        Assert.True(result.IsAbnormal);
        Assert.Equal(InspectAbnormalClass.PlayFailed, result.AbnormalClass);
        Assert.Equal("播放失败", result.AbnormalClassText);
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
        var service = new PreviewService(new InMemoryMapStore(), client, probe, NullLogger<PreviewService>.Instance);

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
        Assert.True(probe.ProbeRequested);
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

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>> GroupListResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getGroupList",
                Payload = []
            };

        public OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>> GroupDeviceListResult { get; set; } =
            new()
            {
                Success = true,
                EndpointName = "getGroupDeviceList",
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

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>> GetGroupListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(GroupListResult);
        }

        public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GetGroupDeviceListAsync(string groupId, CancellationToken cancellationToken)
        {
            return Task.FromResult(GroupDeviceListResult);
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

    private sealed class InMemoryMapStore : IMapStore
    {
        public Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<InspectionDevice> payload =
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
                    DateTimeOffset.UtcNow)
            ];

            return Task.FromResult(payload);
        }
    }
}
