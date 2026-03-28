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
        var service = new PreviewService(new InMemoryMapStore(), client, NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, NullLogger<PreviewService>.Instance);

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
        var service = new PreviewService(new InMemoryMapStore(), client, NullLogger<PreviewService>.Instance);

        var result = await service.PrepareAsync("dev-001", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("在线：可请求预览地址", result.DiagnosisText);
        Assert.Equal("预览地址已就绪", result.AddressStatusText);
        Assert.Equal("rtsp://demo/live/dev-001", result.RtspUrl);
        Assert.True(client.PreviewUrlRequested);
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
