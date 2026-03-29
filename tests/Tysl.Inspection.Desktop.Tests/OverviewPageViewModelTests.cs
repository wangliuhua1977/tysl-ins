using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.App.ViewModels;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class OverviewPageViewModelTests
{
    [Fact]
    public async Task RefreshAsync_UsesUnifiedUnmappedStats()
    {
        var viewModel = new OverviewPageViewModel(
            new StubOverviewStatsService(
                new OverviewStats(
                    19,
                    8,
                    2,
                    new MapCoordinateStats(19, 10, 4, 3, 2),
                    DateTimeOffset.Parse("2026-03-29T10:05:00+08:00"))),
            NullLogger<OverviewPageViewModel>.Instance);

        await viewModel.RefreshAsync();

        Assert.Equal(19, viewModel.TotalPoints);
        Assert.Equal(9, viewModel.UnmappedCount);
        Assert.Contains("平台未提供坐标 4 个", viewModel.UnmappedSummaryText);
        Assert.Contains("坐标获取限频 3 个", viewModel.UnmappedSummaryText);
        Assert.Contains("坐标转换或解析失败 2 个", viewModel.UnmappedSummaryText);
    }

    private sealed class StubOverviewStatsService(OverviewStats stats) : IOverviewStatsService
    {
        public Task<OverviewStats> GetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(stats);
        }
    }
}
