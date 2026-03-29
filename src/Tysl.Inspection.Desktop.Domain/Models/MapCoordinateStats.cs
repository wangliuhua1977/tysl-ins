using System.Globalization;

namespace Tysl.Inspection.Desktop.Domain.Models;

public sealed record MapCoordinateStats(
    int TotalCount,
    int RenderedCount,
    int MissingCount,
    int RateLimitedCount,
    int FailedCount)
{
    public static MapCoordinateStats Empty { get; } = new(0, 0, 0, 0, 0);

    public int UnmappedCount => MissingCount + RateLimitedCount + FailedCount;

    public static MapCoordinateStats FromDevices(IReadOnlyList<InspectionDevice> devices)
    {
        if (devices.Count == 0)
        {
            return Empty;
        }

        var renderedCount = 0;
        var missingCount = 0;
        var rateLimitedCount = 0;
        var failedCount = 0;

        foreach (var device in devices)
        {
            if (HasValidMapCoordinate(device.MapLatitude, device.MapLongitude))
            {
                renderedCount++;
                continue;
            }

            switch (NormalizeState(device.CoordinateStatus, device.HasRawCoordinate))
            {
                case CoordinateStateCatalog.Missing:
                    missingCount++;
                    break;
                case CoordinateStateCatalog.RateLimited:
                    rateLimitedCount++;
                    break;
                default:
                    failedCount++;
                    break;
            }
        }

        return new MapCoordinateStats(devices.Count, renderedCount, missingCount, rateLimitedCount, failedCount);
    }

    public string BuildMapStatusText()
    {
        if (TotalCount == 0)
        {
            return "本地 SQLite 中暂无点位数据。";
        }

        if (FailedCount > 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"已读取 {TotalCount} 个本地点位，其中 {RenderedCount} 个完成上图，{MissingCount} 个平台未提供坐标，{RateLimitedCount} 个坐标获取限频，{FailedCount} 个坐标转换或解析失败。");
        }

        if (RateLimitedCount > 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"已读取 {TotalCount} 个本地点位，其中 {RenderedCount} 个完成上图，{MissingCount} 个平台未提供坐标，{RateLimitedCount} 个坐标获取限频，稍后重试。");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"已读取 {TotalCount} 个本地点位，其中 {RenderedCount} 个完成上图，{MissingCount} 个平台未提供坐标。");
    }

    public string BuildUnmappedSummaryText()
    {
        if (UnmappedCount == 0)
        {
            return "未上图 = 平台未提供坐标 + 坐标获取限频 + 坐标转换或解析失败。当前暂无未上图点位。";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"未上图 = 平台未提供坐标 + 坐标获取限频 + 坐标转换或解析失败。当前共 {UnmappedCount} 个，其中平台未提供坐标 {MissingCount} 个、坐标获取限频 {RateLimitedCount} 个、坐标转换或解析失败 {FailedCount} 个。");
    }

    private static bool HasValidMapCoordinate(string? latitude, string? longitude)
    {
        return TryReadCoordinate(latitude, out var lat)
            && TryReadCoordinate(longitude, out var lng)
            && lat is >= -90 and <= 90
            && lng is >= -180 and <= 180;
    }

    private static bool TryReadCoordinate(string? value, out double coordinate)
    {
        return double.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out coordinate);
    }

    private static string NormalizeState(string? state, bool hasRawCoordinate)
    {
        return state switch
        {
            CoordinateStateCatalog.Missing => CoordinateStateCatalog.Missing,
            CoordinateStateCatalog.RateLimited => CoordinateStateCatalog.RateLimited,
            CoordinateStateCatalog.Failed => CoordinateStateCatalog.Failed,
            _ when !hasRawCoordinate => CoordinateStateCatalog.Missing,
            _ => CoordinateStateCatalog.Failed
        };
    }
}
