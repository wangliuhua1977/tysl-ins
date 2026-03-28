using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class MapService(
    IMapStore mapStore,
    ILogger<MapService> logger) : IMapService
{
    public async Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await mapStore.GetDevicesAsync(cancellationToken);
            logger.LogInformation("Loaded {DeviceCount} devices for map rendering.", devices.Count);
            return new MapLoadResult(true, string.Empty, devices);
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
