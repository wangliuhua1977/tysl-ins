using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Abstractions;

public interface ISqliteBootstrapper
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface IGroupSyncStore
{
    Task ReplaceGroupsAsync(IReadOnlyCollection<InspectionGroup> groups, CancellationToken cancellationToken);

    Task ReplaceSnapshotAsync(
        IReadOnlyCollection<InspectionGroup> groups,
        IReadOnlyCollection<InspectionDevice> devices,
        GroupSyncSnapshotMetadata metadata,
        CancellationToken cancellationToken);

    Task DeleteOrphanDevicesAsync(CancellationToken cancellationToken);

    Task ReplaceDevicesForGroupAsync(
        string groupId,
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken);

    Task<OverviewStats> GetOverviewStatsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InspectionGroup>> GetGroupsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<InspectionDevice>> GetDevicesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, DeviceUserMaintenance>> GetDeviceMaintenanceMapAsync(CancellationToken cancellationToken);

    Task SaveDeviceMaintenanceAsync(DeviceUserMaintenance maintenance, CancellationToken cancellationToken);

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

    Task<PreviewDeviceMaintenanceSaveResult> SaveDeviceMaintenanceAsync(
        string deviceCode,
        string maintenanceStatus,
        string maintenanceNote,
        string manualConfirmationNote,
        CancellationToken cancellationToken);
}

public interface IInspectAbnormalStore
{
    IReadOnlyList<InspectAbnormalItem> GetItems();

    InspectAbnormalItem? Add(InspectResult result);

    InspectAbnormalItem? Reinspect(Guid id, InspectResult result);

    InspectAbnormalItem? ToggleReviewed(Guid id);

    InspectAbnormalItem? AdvanceHandleStatus(Guid id);
}

public interface IInspectAbnormalPoolStore
{
    IReadOnlyList<InspectAbnormalItem> LoadItems();

    void Upsert(InspectAbnormalItem item);

    void Replace(InspectAbnormalItem item);

    void Delete(Guid id);
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

public sealed record PreviewDirectoryDeviceItem(
    string DeviceCode,
    string DeviceName,
    int? OnlineStatus)
{
    public string DisplayText => $"{DeviceName} ({DeviceCode})";

    public string OnlineStatusText => OnlineStatus switch
    {
        1 => "在线",
        0 => "离线",
        2 => "休眠（普通）",
        3 => "休眠（保活/AOV）",
        _ => "状态未知"
    };
}

public sealed record PreviewDirectoryGroupItem(
    string GroupId,
    string GroupName,
    string? ParentGroupId,
    string ParentGroupName,
    int Level,
    int ReportedDeviceCount,
    bool HasChildren,
    IReadOnlyList<PreviewDirectoryDeviceItem> Devices)
{
    public int LoadedDeviceCount => Devices.Count;

    public bool CountMatches => ReportedDeviceCount == LoadedDeviceCount;

    public string DisplayText => CountMatches
        ? $"{GroupName} ({LoadedDeviceCount})"
        : $"{GroupName} (本地 {LoadedDeviceCount} / 平台 {ReportedDeviceCount})";

    public string HierarchyText => string.IsNullOrWhiteSpace(ParentGroupName)
        ? $"层级 {Math.Max(Level, 1)} / 根目录"
        : $"层级 {Math.Max(Level, 1)} / 上级：{ParentGroupName}";

    public string DeviceCountSummaryText => CountMatches
        ? $"当前目录共 {LoadedDeviceCount} 台设备。"
        : $"当前目录本地已落地 {LoadedDeviceCount} 台设备，平台拉回 {ReportedDeviceCount} 台。";

    public string ChildrenHintText => HasChildren
        ? "包含子目录，子目录会在列表中继续展开显示。"
        : "当前目录无子目录。";

    public string EmptyStateText => LoadedDeviceCount == 0
        ? "当前目录暂无设备，已按空目录保留，便于人工核对是否完整。"
        : string.Empty;
}

public sealed record PreviewDeviceLoadResult(
    bool Success,
    string Message,
    IReadOnlyList<PreviewDeviceOption> Devices,
    IReadOnlyList<PreviewDirectoryGroupItem> DirectoryGroups,
    int SnapshotGroupCount,
    int SnapshotDeviceCount,
    GroupSyncSnapshotMetadata Metadata,
    DateTimeOffset? LastSyncedAt)
{
    public IReadOnlyDictionary<string, InspectionDevice> DeviceDetailsByCode { get; init; }
        = new Dictionary<string, InspectionDevice>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, DeviceUserMaintenance> DeviceMaintenanceByCode { get; init; }
        = new Dictionary<string, DeviceUserMaintenance>(StringComparer.OrdinalIgnoreCase);
}

public sealed record PreviewPrepareResult(
    bool Success,
    string DeviceCode,
    string DeviceName,
    string DiagnosisText,
    string AddressStatusText,
    string? RtspUrl,
    string ExpireText,
    DateTimeOffset RequestedAt);

public sealed record PreviewDeviceMaintenanceSaveResult(
    bool Success,
    string Message,
    DeviceUserMaintenance? Maintenance);

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
    InspectAbnormalClass AbnormalClass,
    string AbnormalClassText,
    string Conclusion,
    string FailureCategory,
    string SummaryText,
    bool IsReviewed,
    bool IsRecoveredConfirmed,
    DateTimeOffset? RecoveredConfirmedAt,
    string RecoveredSummary,
    InspectHandleStatus HandleStatus,
    string HandleStatusText,
    DateTimeOffset HandleUpdatedAt,
    DateTimeOffset UpdatedAt)
{
    public string DeviceDisplayText => $"{DeviceName} ({DeviceCode})";

    public string InspectAtText => InspectAt.ToString("yyyy-MM-dd HH:mm:ss");

    public string DispositionSummary => SummaryText;

    public string ReviewedText => IsReviewed ? "已复核" : "未复核";

    public string ReviewActionText => IsReviewed ? "取消已复核" : "标记已复核";

    public string RecoveredConfirmedText => IsRecoveredConfirmed ? "已恢复确认" : "未恢复确认";

    public string RecoveredConfirmedAtText => RecoveredConfirmedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "暂无";

    public string RecoveredSummaryText => string.IsNullOrWhiteSpace(RecoveredSummary) ? "暂无" : RecoveredSummary;

    public string HandleActionText => HandleStatus switch
    {
        InspectHandleStatus.Pending => "开始处理",
        InspectHandleStatus.InProgress => "标记已处理",
        InspectHandleStatus.Handled => "回退处理中",
        _ => "开始处理"
    };

    public static string BuildHandleStatusText(InspectHandleStatus handleStatus)
    {
        return handleStatus switch
        {
            InspectHandleStatus.Pending => "待处理",
            InspectHandleStatus.InProgress => "处理中",
            InspectHandleStatus.Handled => "已处理",
            _ => "待处理"
        };
    }
}

public enum InspectAbnormalClass
{
    None = 0,
    Offline = 1,
    RtspNotReady = 2,
    PlayFailed = 3
}

public enum InspectHandleStatus
{
    Pending = 1,
    InProgress = 2,
    Handled = 3
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
            return "播放器已进入 Playing 状态，画面内容仍需人工复核。";
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
        const string replacement = "RTSP 地址不展示明文";
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
