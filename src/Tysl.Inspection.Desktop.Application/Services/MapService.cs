using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class MapService(
    IMapStore mapStore,
    IDeviceCoordinateService deviceCoordinateService,
    ICoordinateProjectionService coordinateProjectionService,
    ILogger<MapService> logger) : IMapService
{
    public async Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await mapStore.GetDevicesAsync(cancellationToken);
            var refreshedDevices = await deviceCoordinateService.RefreshPlatformCoordinatesAsync(devices, cancellationToken);
            var projections = await coordinateProjectionService.ProjectBd09ToGcj02Async(
                refreshedDevices
                    .Select(device => new CoordinateProjectionRequest(
                        device.DeviceCode,
                        device.DeviceName,
                        device.RawLatitude,
                        device.RawLongitude))
                    .ToArray(),
                cancellationToken);

            var renderedCount = projections.Values.Count(result => result.HasMapCoordinate);
            var missingCount = projections.Values.Count(result => result.CoordinateState == "missing");
            var failedCount = projections.Values.Count(result => result.CoordinateState == "failed");

            logger.LogInformation(
                "Map render coordinates resolved. RenderedCount={RenderedCount}, MissingCount={MissingCount}, FailedCount={FailedCount}.",
                renderedCount,
                missingCount,
                failedCount);

            return new MapLoadResult(true, string.Empty, refreshedDevices)
            {
                ProjectionByDeviceCode = projections
            };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected failure while loading map points.");
            return new MapLoadResult(false, BuildSqliteMessage(exception), Array.Empty<InspectionDevice>());
        }
    }

    private static string BuildSqliteMessage(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("unable to open database file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot open", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 SQLite 文件不存在或无法打开，请先检查数据库路径。";
        }

        if (message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Device", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 SQLite 中缺少 Device 表，请先完成初始化或同步。";
        }

        return $"本地点位读取失败：{message}";
    }
}
