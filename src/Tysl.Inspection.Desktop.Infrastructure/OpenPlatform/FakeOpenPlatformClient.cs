using Tysl.Inspection.Desktop.Contracts.OpenPlatform;

namespace Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;

public sealed class FakeOpenPlatformClient : IOpenPlatformClient
{
    private static readonly IReadOnlyList<OpenPlatformRegionDto> RootRegions =
    [
        new("region-demo-001", "R-001", 1, 1, "离线开发监控目录 A", 1, "GB-001"),
        new("region-demo-002", "R-002", 0, 0, "离线开发空目录", 1, "GB-002")
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<OpenPlatformRegionDto>> ChildRegions =
        new Dictionary<string, IReadOnlyList<OpenPlatformRegionDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["region-demo-001"] =
            [
                new OpenPlatformRegionDto("region-demo-001-01", "R-001-01", 0, 1, "离线开发子目录 A-1", 2, "GB-001-01")
            ]
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<OpenPlatformRegionDeviceDto>> DevicesByRegion =
        new Dictionary<string, IReadOnlyList<OpenPlatformRegionDeviceDto>>(StringComparer.OrdinalIgnoreCase)
        {
            ["region-demo-001"] =
            [
                new OpenPlatformRegionDeviceDto("dev-001", "离线设备 1"),
                new OpenPlatformRegionDeviceDto("dev-002", "离线设备 2")
            ],
            ["region-demo-001-01"] =
            [
                new OpenPlatformRegionDeviceDto("dev-003", "离线设备 3")
            ]
        };

    private static readonly IReadOnlyList<OpenPlatformRegionDeviceCountDto> RootCounts =
    [
        new("R-001", 3, 2),
        new("R-002", 0, 0)
    ];

    private static readonly IReadOnlyDictionary<string, OpenPlatformDeviceInfoPayload> DeviceInfoByCode =
        new Dictionary<string, OpenPlatformDeviceInfoPayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["dev-001"] = new("dev-001", "离线设备 1", "31.2304", "121.4737", "上海"),
            ["dev-002"] = new("dev-002", "离线设备 2", null, null, "平台未提供坐标"),
            ["dev-003"] = new("dev-003", "离线设备 3", "30.2741", "120.1551", "杭州")
        };

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

    public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(
        string regionId,
        CancellationToken cancellationToken)
    {
        var payload = string.IsNullOrWhiteSpace(regionId)
            ? RootRegions
            : ChildRegions.TryGetValue(regionId, out var regions)
                ? regions
                : [];

        return Task.FromResult(new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>
        {
            Success = true,
            EndpointName = "getReginWithGroupList",
            RequestUrl = "fake://getReginWithGroupList",
            Payload = payload
        });
    }

    public Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(
        string regionId,
        int pageNo,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var allDevices = DevicesByRegion.TryGetValue(regionId, out var devices)
            ? devices
            : [];
        var boundedPageSize = Math.Clamp(pageSize, 1, 50);
        var items = allDevices
            .Skip(Math.Max(pageNo - 1, 0) * boundedPageSize)
            .Take(boundedPageSize)
            .ToArray();

        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>
        {
            Success = true,
            EndpointName = "getDeviceList",
            RequestUrl = "fake://getDeviceList",
            Payload = new OpenPlatformRegionDevicePageDto(
                items,
                pageNo,
                boundedPageSize,
                allDevices.Count)
        });
    }

    public Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(
        string regionCode,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OpenPlatformRegionDeviceCountDto> payload = string.IsNullOrWhiteSpace(regionCode)
            ? RootCounts
            : RootCounts.Where(item => string.Equals(item.RegionCode, regionCode, StringComparison.OrdinalIgnoreCase)).ToArray();

        return Task.FromResult(new OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>
        {
            Success = true,
            EndpointName = "getCusDeviceCount",
            RequestUrl = "fake://getCusDeviceCount",
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

    public Task<OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>> GetDeviceInfoByDeviceCodeAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var payload = DeviceInfoByCode.TryGetValue(deviceCode, out var value)
            ? value
            : new OpenPlatformDeviceInfoPayload(deviceCode, string.Empty, null, null, null);

        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>
        {
            Success = true,
            EndpointName = "getDeviceInfoByDeviceCode",
            RequestUrl = "fake://getDeviceInfoByDeviceCode",
            Payload = payload
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
                EndpointName = "getDeviceMediaUrlRtsp",
                RequestUrl = "fake://getDeviceMediaUrlRtsp",
                ErrorMessage = "设备离线"
            });
        }

        return Task.FromResult(new OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>
        {
            Success = true,
            EndpointName = "getDeviceMediaUrlRtsp",
            RequestUrl = "fake://getDeviceMediaUrlRtsp",
            Payload = new OpenPlatformPreviewUrlPayload(
                $"rtsp://fake-platform.example/live/{deviceCode}",
                "600 秒")
        });
    }
}
