namespace Tysl.Inspection.Desktop.Domain.Models;

public static class CoordinateStateCatalog
{
    public const string Available = "available";
    public const string Missing = "missing";
    public const string RateLimited = "rate_limited";
    public const string Failed = "failed";

    public static string GetStateText(string? state, bool hasMapCoordinate)
    {
        return state switch
        {
            Available when hasMapCoordinate => "已获取并转换坐标",
            Available => "已获取平台原始坐标",
            Missing => "平台未提供坐标",
            RateLimited => "坐标获取限频，稍后重试",
            Failed => "坐标处理失败，需人工确认",
            _ => "平台未提供坐标"
        };
    }

    public static string GetWarningText(string? state, string? message, bool hasMapCoordinate)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        return state switch
        {
            Available when hasMapCoordinate => "地图 marker 仅使用转换后的高德坐标。",
            Available => "平台原始坐标已缓存，等待生成高德渲染坐标。",
            Missing => "平台未提供坐标，当前不进入上图。",
            RateLimited => "坐标获取限频，稍后重试。",
            Failed => "坐标处理失败，需人工确认。",
            _ => "平台未提供坐标，当前不进入上图。"
        };
    }
}
