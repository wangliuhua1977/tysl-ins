using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Abstractions;

public interface ISqliteBootstrapper
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IGroupSyncStore
{
    Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken);

    Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken);

    Task ReplaceDevicesForGroupAsync(
        string groupId,
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken);

    Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken);

    Task<LocalSyncSnapshot> GetLocalSyncSnapshotAsync(CancellationToken cancellationToken);
}

public interface IMapStore
{
    Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken);
}

public interface IGroupSyncService
{
    Task<GroupSyncSummary> SyncAsync(CancellationToken cancellationToken);

    Task<LocalSyncSnapshot> GetLatestSnapshotAsync(CancellationToken cancellationToken);
}

public interface IOverviewStatsService
{
    Task<OverviewStats> GetAsync(CancellationToken cancellationToken);
}

public interface IMapService
{
    Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken);
}

public interface IPreviewService
{
    Task<PreviewDeviceLoadResult> LoadLocalDevicesAsync(CancellationToken cancellationToken);

    Task<PreviewPrepareResult> PrepareAsync(string deviceCode, CancellationToken cancellationToken);

    Task<InspectResult> InspectAsync(string deviceCode, CancellationToken cancellationToken);
}

public interface IInspectAbnormalStore
{
    IReadOnlyList<InspectAbnormalItem> GetItems();

    InspectAbnormalItem? Add(InspectResult result);

    InspectAbnormalItem? ToggleReviewed(Guid id);
}

public interface IPlayProbe
{
    Task<PlayProbeResult> ProbeAsync(PlayProbeArgs args, CancellationToken cancellationToken);
}

public sealed record MapLoadResult(
    bool Success,
    string Message,
    IReadOnlyList<InspectionDevice> Devices);

public sealed record PreviewDeviceOption(
    string DeviceCode,
    string DeviceName,
    int? OnlineStatus)
{
    public string DisplayText => $"{DeviceName} ({DeviceCode})";
}

public sealed record PreviewDeviceLoadResult(
    bool Success,
    string Message,
    IReadOnlyList<PreviewDeviceOption> Devices);

public sealed record PreviewPrepareResult(
    bool Success,
    string DeviceCode,
    string DeviceName,
    string DiagnosisText,
    string AddressStatusText,
    string? RtspUrl,
    string ExpireText,
    DateTimeOffset RequestedAt);

public sealed record PlayProbeArgs(
    string DeviceName,
    string DeviceCode,
    string RtspUrl);

public sealed record PlayProbeResult(
    bool PlaybackStarted,
    bool EnteredPlaying,
    string FailureCategory,
    string DetailMessage);

public sealed record InspectAbnormalItem(
    Guid Id,
    DateTimeOffset InspectAt,
    string DeviceName,
    string DeviceCode,
    string Conclusion,
    string AbnormalClassText,
    string SummaryText,
    bool IsReviewed)
{
    public string DeviceDisplayText => $"{DeviceName}（{DeviceCode}）";

    public string InspectAtText => InspectAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string ReviewedText => IsReviewed ? "已复核" : "未复核";

    public string ReviewActionText => IsReviewed ? "取消已复核" : "标记已复核";
}

public enum InspectAbnormalClass
{
    None = 0,
    Offline = 1,
    RtspNotReady = 2,
    PlayFailed = 3
}

public sealed record InspectResult(
    DateTimeOffset InspectAt,
    string DeviceName,
    string DeviceCode,
    bool StatusResolved,
    string OnlineStatus,
    bool RtspReady,
    bool PlaybackStarted,
    bool EnteredPlaying,
    string Conclusion,
    string FailureCategory,
    string DetailMessage,
    InspectAbnormalClass AbnormalClass)
{
    public bool IsAbnormal => !string.Equals(Conclusion, "巡检通过", StringComparison.Ordinal);

    public string AbnormalClassText => AbnormalClass switch
    {
        InspectAbnormalClass.Offline => "离线",
        InspectAbnormalClass.RtspNotReady => "RTSP 未就绪",
        InspectAbnormalClass.PlayFailed => "播放失败",
        _ when IsAbnormal => string.Empty,
        _ => "无异常/巡检通过"
    };

    public string BuildDispositionSummary()
    {
        var parts = new List<string>(6)
        {
            $"巡检时间：{InspectAt:yyyy-MM-dd HH:mm:ss}",
            $"设备：{SanitizeSummaryText(DeviceName)}（{SanitizeSummaryText(DeviceCode)}）",
            $"结论：{SanitizeSummaryText(Conclusion)}",
            $"前置归类：{BuildSummaryAbnormalClassText()}"
        };

        if (!string.IsNullOrWhiteSpace(FailureCategory))
        {
            parts.Add($"失败分类：{SanitizeSummaryText(FailureCategory)}");
        }

        parts.Add($"说明：{BuildSummaryDetailText()}");
        return string.Join("；", parts);
    }

    private string BuildSummaryAbnormalClassText()
    {
        if (!string.IsNullOrWhiteSpace(AbnormalClassText))
        {
            return AbnormalClassText;
        }

        if (!IsAbnormal)
        {
            return "无异常/巡检通过";
        }

        if (ContainsKeyword(OnlineStatus, "离线")
            || ContainsKeyword(FailureCategory, "离线")
            || ContainsKeyword(Conclusion, "离线"))
        {
            return "离线";
        }

        if (ContainsKeyword(FailureCategory, "RTSP")
            || ContainsKeyword(Conclusion, "RTSP"))
        {
            return "RTSP 未就绪";
        }

        return "播放失败";
    }

    private string BuildSummaryDetailText()
    {
        if (!IsAbnormal && ContainsKeyword(DetailMessage, "Playing"))
        {
            return "播放器已进入播放态，画面内容仍需人工复核。";
        }

        return SanitizeSummaryText(string.IsNullOrWhiteSpace(DetailMessage)
            ? "未返回最小诊断信息。"
            : DetailMessage);
    }

    private static bool ContainsKeyword(string value, string keyword)
    {
        return value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeSummaryText(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return MaskRtspText(normalized);
    }

    private static string MaskRtspText(string value)
    {
        var masked = value;
        masked = ReplaceRtspSegment(masked, "rtsp://");
        masked = ReplaceRtspSegment(masked, "rtsps://");
        return masked;
    }

    private static string ReplaceRtspSegment(string value, string scheme)
    {
        const string replacement = "RTSP 地址不展示地址明文";
        var start = value.IndexOf(scheme, StringComparison.OrdinalIgnoreCase);

        while (start >= 0)
        {
            var end = start;
            while (end < value.Length && !IsRtspBoundary(value[end]))
            {
                end++;
            }

            value = value[..start] + replacement + value[end..];
            start = value.IndexOf(scheme, start + replacement.Length, StringComparison.OrdinalIgnoreCase);
        }

        return value;
    }

    private static bool IsRtspBoundary(char value)
    {
        return char.IsWhiteSpace(value)
            || value is ';'
            || value is '；'
            || value is ','
            || value is '，'
            || value is '.'
            || value is '。'
            || value is ')'
            || value is '）'
            || value is ']'
            || value is '】'
            || value is '"'
            || value is '\'';
    }
}
