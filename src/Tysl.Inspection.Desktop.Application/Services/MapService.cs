using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class MapService(
    IMapStore mapStore,
    IGroupSyncStore groupSyncStore,
    IDeviceCoordinateService deviceCoordinateService,
    ICoordinateProjectionService coordinateProjectionService,
    ILogger<MapService> logger) : IMapService
{
    public async Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await mapStore.GetDevicesAsync(cancellationToken);
            var resolvedDevices = await deviceCoordinateService.RefreshPlatformCoordinatesAsync(devices, cancellationToken);
            var projections = await coordinateProjectionService.ProjectBd09ToGcj02Async(
                resolvedDevices
                    .Select(device => new CoordinateProjectionRequest(
                        device.DeviceCode,
                        device.DeviceName,
                        device.RawLatitude,
                        device.RawLongitude,
                        device.MapLatitude,
                        device.MapLongitude,
                        device.CoordinateStatus,
                        device.CoordinateStatusMessage))
                    .ToArray(),
                cancellationToken);

            var devicesWithProjectionCache = MergeProjectionCache(resolvedDevices, projections);
            var changedCacheDevices = resolvedDevices
                .Zip(devicesWithProjectionCache, (original, updated) => (original, updated))
                .Where(pair => HasProjectionStateChanged(pair.original, pair.updated))
                .Select(pair => pair.updated)
                .ToArray();

            if (changedCacheDevices.Length > 0)
            {
                logger.LogInformation(
                    "Coordinate render cache write started. DeviceCount={DeviceCount}.",
                    changedCacheDevices.Length);
                foreach (var device in changedCacheDevices)
                {
                    logger.LogInformation(
                        "Coordinate render cache write item prepared. DeviceCode={DeviceCode}, RawLongitude={RawLongitude}, RawLatitude={RawLatitude}, MapLongitude={MapLongitude}, MapLatitude={MapLatitude}, CoordinateState={CoordinateState}.",
                        device.DeviceCode,
                        device.RawLongitude ?? "null",
                        device.RawLatitude ?? "null",
                        device.MapLongitude ?? "null",
                        device.MapLatitude ?? "null",
                        device.CoordinateStatus);
                }
                await groupSyncStore.UpdateDevicePlatformDataAsync(changedCacheDevices, cancellationToken);
                logger.LogInformation(
                    "Coordinate render cache write completed. DeviceCount={DeviceCount}.",
                    changedCacheDevices.Length);
                foreach (var device in changedCacheDevices)
                {
                    logger.LogInformation(
                        "Coordinate render cache write item completed. DeviceCode={DeviceCode}, MapLongitude={MapLongitude}, MapLatitude={MapLatitude}, CoordinateState={CoordinateState}, CacheWriteSucceeded={CacheWriteSucceeded}.",
                        device.DeviceCode,
                        device.MapLongitude ?? "null",
                        device.MapLatitude ?? "null",
                        device.CoordinateStatus,
                        true);
                }
            }

            var stats = MapCoordinateStats.FromDevices(devicesWithProjectionCache);

            logger.LogInformation(
                "Map coordinate load completed. RenderedCount={RenderedCount}, UnmappedCount={UnmappedCount}, MissingCount={MissingCount}, RateLimitedCount={RateLimitedCount}, FailedCount={FailedCount}, RenderedDeviceCodes={RenderedDeviceCodes}, UnmappedSummary={UnmappedSummary}.",
                stats.RenderedCount,
                stats.UnmappedCount,
                stats.MissingCount,
                stats.RateLimitedCount,
                stats.FailedCount,
                BuildDeviceCodeSummary(devicesWithProjectionCache.Where(HasValidMapCoordinate).Select(device => device.DeviceCode)),
                BuildUnmappedSummary(devicesWithProjectionCache));

            return new MapLoadResult(true, string.Empty, devicesWithProjectionCache)
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

    private static IReadOnlyList<InspectionDevice> MergeProjectionCache(
        IReadOnlyList<InspectionDevice> devices,
        IReadOnlyDictionary<string, CoordinateProjectionResult> projections)
    {
        return devices
            .Select(device =>
            {
                if (!projections.TryGetValue(device.DeviceCode, out var projection))
                {
                    return device;
                }

                return device with
                {
                    MapLatitude = projection.HasMapCoordinate ? projection.MapLatitude : null,
                    MapLongitude = projection.HasMapCoordinate ? projection.MapLongitude : null,
                    CoordinateSource = projection.HasRawCoordinate
                        ? "amap_js_convert_from_baidu"
                        : device.CoordinateSource,
                    CoordinateStatus = projection.CoordinateState,
                    CoordinateStatusMessage = projection.CoordinateWarning
                };
            })
            .ToArray();
    }

    private static bool HasProjectionStateChanged(InspectionDevice original, InspectionDevice updated)
    {
        return !string.Equals(original.MapLatitude, updated.MapLatitude, StringComparison.Ordinal)
               || !string.Equals(original.MapLongitude, updated.MapLongitude, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateSource, updated.CoordinateSource, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateStatus, updated.CoordinateStatus, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateStatusMessage, updated.CoordinateStatusMessage, StringComparison.Ordinal);
    }

    private static bool HasValidMapCoordinate(InspectionDevice device)
    {
        return !string.IsNullOrWhiteSpace(device.MapLatitude)
               && !string.IsNullOrWhiteSpace(device.MapLongitude);
    }

    private static string BuildDeviceCodeSummary(IEnumerable<string> deviceCodes)
    {
        var codes = deviceCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        return codes.Length == 0 ? "<none>" : string.Join(", ", codes);
    }

    private static string BuildUnmappedSummary(IEnumerable<InspectionDevice> devices)
    {
        var summary = devices
            .Where(device => !HasValidMapCoordinate(device))
            .Select(device => $"{device.DeviceCode}:{device.CoordinateStatus}")
            .Take(20)
            .ToArray();

        return summary.Length == 0 ? "<none>" : string.Join(" | ", summary);
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
