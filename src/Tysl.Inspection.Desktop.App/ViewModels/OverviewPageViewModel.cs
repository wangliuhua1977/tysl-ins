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
    private int unlocatedCount;

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
        UnlocatedCount = stats.UnlocatedCount;
        LastSyncedAtText = stats.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无同步记录";
    }
}
