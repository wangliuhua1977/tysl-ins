using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class OverviewPageViewModel(
    IOverviewStatsService overviewStatsService,
    ILogger<OverviewPageViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private int totalPoints;

    [ObservableProperty]
    private int onlineCount;

    [ObservableProperty]
    private int offlineCount;

    [ObservableProperty]
    private int unmappedCount;

    [ObservableProperty]
    private string unmappedSummaryText = "未上图 = 平台未提供坐标 + 坐标获取限频 + 坐标转换或解析失败。";

    [ObservableProperty]
    private string lastSyncedAtText = "尚无同步记录";

    public async Task RefreshAsync()
    {
        logger.LogInformation("Refreshing overview statistics.");
        var stats = await overviewStatsService.GetAsync(CancellationToken.None);
        Apply(stats);
    }

    private void Apply(OverviewStats stats)
    {
        TotalPoints = stats.TotalPoints;
        OnlineCount = stats.OnlineCount;
        OfflineCount = stats.OfflineCount;
        UnmappedCount = stats.CoordinateStats.UnmappedCount;
        UnmappedSummaryText = stats.CoordinateStats.BuildUnmappedSummaryText();
        LastSyncedAtText = stats.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无同步记录";

        logger.LogInformation(
            "Overview final coordinate summary. RenderedCount={RenderedCount}, UnmappedCount={UnmappedCount}, MissingCount={MissingCount}, RateLimitedCount={RateLimitedCount}, FailedCount={FailedCount}.",
            stats.CoordinateStats.RenderedCount,
            stats.CoordinateStats.UnmappedCount,
            stats.CoordinateStats.MissingCount,
            stats.CoordinateStats.RateLimitedCount,
            stats.CoordinateStats.FailedCount);
    }
}
