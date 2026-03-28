using Tysl.Inspection.Desktop.Contracts.OpenPlatform;

namespace Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;

public sealed class FakeOpenPlatformClient : IOpenPlatformClient
{
    public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.UtcNow;
        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformAccessTokenPayload>
        {
            Success = true,
            EndpointName = "getAccessToken",
            RequestUrl = "fake://getAccessToken",
            Payload = new OpenPlatformAccessTokenPayload(
                "fake-access-token",
                "fake-refresh-token",
                3600,
                requestedAt,
                requestedAt.AddHours(1),
                requestedAt.AddDays(30))
        });
    }

    public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>> GetGroupListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenPlatformGroupDto> payload =
        [
            new OpenPlatformGroupDto("group-demo-001", "离线开发分组 A", 2),
            new OpenPlatformGroupDto("group-demo-002", "离线开发分组 B", 1)
        ];

        return Task.FromResult(new OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>
        {
            Success = true,
            EndpointName = "getGroupList",
            RequestUrl = "fake://getGroupList",
            Payload = payload
        });
    }

    public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GetGroupDeviceListAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenPlatformDeviceDto> payload = groupId switch
        {
            "group-demo-001" =>
            [
                new OpenPlatformDeviceDto("dev-001", "离线设备 1", groupId, "31.2304", "121.4737", "上海", 1, 1, 1, 0),
                new OpenPlatformDeviceDto("dev-002", "离线设备 2", groupId, null, null, "未定位", 0, 1, 0, 0)
            ],
            _ =>
            [
                new OpenPlatformDeviceDto("dev-003", "离线设备 3", groupId, "39.9042", "116.4074", "北京", 1, 1, 1, 0)
            ]
        };

        return Task.FromResult(new OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>
        {
            Success = true,
            EndpointName = "getGroupDeviceList",
            RequestUrl = "fake://getGroupDeviceList",
            Payload = payload
        });
    }

    public Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var status = deviceCode switch
        {
            "dev-001" => 1,
            "dev-002" => 0,
            "dev-003" => 2,
            _ => 1
        };

        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>
        {
            Success = true,
            EndpointName = "getDeviceStatus",
            RequestUrl = "fake://getDeviceStatus",
            Payload = new OpenPlatformDeviceStatusPayload(deviceCode, status)
        });
    }

    public Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        if (string.Equals(deviceCode, "dev-002", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
            {
                Success = false,
                EndpointName = "getDeviceVideoUrl",
                RequestUrl = "fake://getDeviceVideoUrl",
                ErrorMessage = "设备离线"
            });
        }

        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
        {
            Success = true,
            EndpointName = "getDeviceVideoUrl",
            RequestUrl = "fake://getDeviceVideoUrl",
            Payload = new OpenPlatformPreviewUrlPayload(
                $"rtsp://fake-platform.example/live/{deviceCode}",
                "600 秒")
        });
    }
}
