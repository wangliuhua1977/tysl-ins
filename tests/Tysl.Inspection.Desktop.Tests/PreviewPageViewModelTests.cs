using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.App.Services;
using Tysl.Inspection.Desktop.App.ViewModels;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class PreviewPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_DisablesPlayWindow_WhenRtspAddressIsNotReady()
    {
        var previewService = new StubPreviewService();
        var playWinService = new StubPlayWinSvc();
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            playWinService,
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        Assert.False(viewModel.IsPlayWindowReady);
        Assert.False(viewModel.OpenPlayWinCommand.CanExecute(null));
        Assert.Contains("请先成功获取 RTSP 地址", viewModel.PlayWindowHintText);
    }

    [Fact]
    public async Task RequestPreviewAsync_EnablesPlayWindow_AndOpensWithCurrentRtspAddress()
    {
        var previewService = new StubPreviewService
        {
            PrepareResult = new PreviewPrepareResult(
                true,
                "dev-001",
                "测试设备",
                "在线：可请求预览地址",
                "预览地址已就绪",
                "rtsp://demo/live/dev-001",
                "平台返回：600 秒",
                DateTimeOffset.Parse("2026-03-28T10:00:00+08:00"))
        };
        var playWinService = new StubPlayWinSvc();
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            playWinService,
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestPreviewCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsPlayWindowReady);
        Assert.True(viewModel.OpenPlayWinCommand.CanExecute(null));

        viewModel.OpenPlayWinCommand.Execute(null);

        var opened = Assert.Single(playWinService.Opened);
        Assert.Equal("测试设备", opened.DeviceName);
        Assert.Equal("dev-001", opened.DeviceCode);
        Assert.Equal("rtsp://demo/live/dev-001", opened.RtspUrl);
    }

    [Fact]
    public async Task RequestInspectAsync_UpdatesInspectSummary()
    {
        var previewService = new StubPreviewService
        {
            InspectResult = new InspectResult(
                DateTimeOffset.Parse("2026-03-28T10:05:00+08:00"),
                "测试设备",
                "dev-001",
                true,
                "在线",
                true,
                true,
                false,
                "巡检失败：播放建链失败",
                "播放建链失败",
                "播放器未能完成播放建链。",
                InspectAbnormalClass.PlayFailed)
        };
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        Assert.Equal("巡检失败：播放建链失败", viewModel.InspectConclusion);
        Assert.Equal("播放失败", viewModel.InspectAbnormalClassText);
        Assert.Equal("播放建链失败", viewModel.InspectFailureCategory);
        Assert.Contains("在线状态：在线", viewModel.InspectStageText);
        Assert.Contains("播放建链：已启动", viewModel.InspectStageText);
        Assert.Contains("Playing：未进入", viewModel.InspectStageText);
        Assert.Equal("播放器未能完成播放建链。", viewModel.InspectDetailText);
        Assert.Equal("2026-03-28 10:05:00", viewModel.InspectAtText);
        Assert.Contains("巡检时间：2026-03-28 10:05:00", viewModel.InspectSummaryText);
        Assert.Contains("设备：测试设备（dev-001）", viewModel.InspectSummaryText);
        Assert.Contains("前置归类：播放失败", viewModel.InspectSummaryText);
    }

    [Fact]
    public async Task RequestInspectAsync_AddsAbnormalPoolItem_AndAllowsMinimalHandlingActions()
    {
        var previewService = new StubPreviewService
        {
            InspectResult = new InspectResult(
                DateTimeOffset.Parse("2026-03-28T10:05:00+08:00"),
                "测试设备",
                "dev-001",
                true,
                "在线",
                true,
                true,
                false,
                "巡检失败：播放建链失败",
                "播放建链失败",
                "播放器未能完成播放建链。",
                InspectAbnormalClass.PlayFailed)
        };
        var store = CreateStore();
        var viewModel = new PreviewPageViewModel(
            previewService,
            store,
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.AbnormalItems);
        Assert.Equal("播放失败", item.AbnormalClassText);
        Assert.Equal("未复核", item.ReviewedText);
        Assert.Equal("待处理", item.HandleStatusText);
        Assert.Contains("当前异常池共 1 条", viewModel.AbnormalListHintText);

        viewModel.AdvanceHandleStatusCommand.Execute(item);

        var handling = Assert.Single(viewModel.AbnormalItems);
        Assert.Equal(InspectHandleStatus.InProgress, handling.HandleStatus);
        Assert.Equal("处理中", handling.HandleStatusText);

        viewModel.ToggleReviewedCommand.Execute(item);

        var updated = Assert.Single(viewModel.AbnormalItems);
        Assert.True(updated.IsReviewed);
        Assert.Equal("已复核", updated.ReviewedText);
    }

    [Fact]
    public async Task RequestInspectAsync_DeduplicatesAbnormalPoolItemsInUi()
    {
        var previewService = new StubPreviewService
        {
            InspectResult = new InspectResult(
                DateTimeOffset.Parse("2026-03-28T10:05:00+08:00"),
                "测试设备",
                "dev-001",
                true,
                "在线",
                true,
                true,
                false,
                "巡检失败：播放建链失败",
                "播放建链失败",
                "播放器未能完成播放建链。",
                InspectAbnormalClass.PlayFailed)
        };
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        Assert.Single(viewModel.AbnormalItems);
    }

    [Fact]
    public async Task RequestInspectAsync_DoesNotShowExtraAbnormalText_WhenResultIsOutsideThreeClasses()
    {
        var previewService = new StubPreviewService
        {
            InspectResult = new InspectResult(
                DateTimeOffset.Parse("2026-03-28T10:06:00+08:00"),
                "测试设备",
                "dev-001",
                false,
                "未获取",
                false,
                false,
                false,
                "巡检失败：设备状态未获取",
                "状态查询失败",
                "设备状态查询失败：接口超时",
                InspectAbnormalClass.None)
        };
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.InspectAbnormalClassText);
        Assert.Equal("巡检失败：设备状态未获取", viewModel.InspectConclusion);
        Assert.Equal("状态查询失败", viewModel.InspectFailureCategory);
        Assert.Empty(viewModel.AbnormalItems);
    }

    [Fact]
    public async Task RequestInspectAsync_DoesNotAddAbnormalPoolItem_WhenInspectPasses()
    {
        var previewService = new StubPreviewService
        {
            InspectResult = new InspectResult(
                DateTimeOffset.Parse("2026-03-28T10:06:00+08:00"),
                "测试设备",
                "dev-001",
                true,
                "在线",
                true,
                true,
                true,
                "巡检通过",
                string.Empty,
                "播放器已进入 Playing 播放态。",
                InspectAbnormalClass.None)
        };
        var viewModel = new PreviewPageViewModel(
            previewService,
            CreateStore(),
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.AbnormalItems);
        Assert.Contains("当前异常池暂无异常项", viewModel.AbnormalListHintText);
    }

    [Theory]
    [InlineData(PlayStage.Initializing, "正在初始化播放器")]
    [InlineData(PlayStage.Connecting, "正在建立播放链路")]
    [InlineData(PlayStage.Playing, "正在播放")]
    [InlineData(PlayStage.Stopped, "已停止播放")]
    [InlineData(PlayStage.InitFailed, "播放初始化失败")]
    [InlineData(PlayStage.LinkFailed, "播放建链失败")]
    [InlineData(PlayStage.Interrupted, "播放过程中断")]
    [InlineData(PlayStage.AddressExpired, "地址可能失效")]
    public void PlayText_ToStatus_ReturnsExpectedChineseText(PlayStage stage, string expected)
    {
        Assert.Equal(expected, PlayText.ToStatus(stage));
    }

    private static InspectAbnormalStore CreateStore()
    {
        return new InspectAbnormalStore(
            new InMemoryInspectAbnormalPoolStore(),
            NullLogger<InspectAbnormalStore>.Instance);
    }

    private sealed class StubPreviewService : IPreviewService
    {
        public PreviewPrepareResult PrepareResult { get; set; } = new(
            false,
            "dev-001",
            "测试设备",
            "尚未发起诊断",
            "尚未发起预览地址获取",
            null,
            "平台未返回有效期",
            DateTimeOffset.Parse("2026-03-28T10:00:00+08:00"));

        public InspectResult InspectResult { get; set; } = new(
            DateTimeOffset.Parse("2026-03-28T10:00:00+08:00"),
            "测试设备",
            "dev-001",
            false,
            "未获取",
            false,
            false,
            false,
            "尚未发起巡检诊断",
            "暂无",
            "仅在发起巡检诊断后展示最小结果。",
            InspectAbnormalClass.None);

        public Task<PreviewDeviceLoadResult> LoadLocalDevicesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(
                new PreviewDeviceLoadResult(
                    true,
                    string.Empty,
                    [new PreviewDeviceOption("dev-001", "测试设备", 1)]));
        }

        public Task<PreviewPrepareResult> PrepareAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(PrepareResult);
        }

        public Task<InspectResult> InspectAsync(string deviceCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(InspectResult);
        }
    }

    private sealed class StubPlayWinSvc : IPlayWinSvc
    {
        public List<PlayWinArgs> Opened { get; } = [];

        public string? Open(PlayWinArgs args)
        {
            Opened.Add(args);
            return null;
        }
    }

    private sealed class InMemoryInspectAbnormalPoolStore : IInspectAbnormalPoolStore
    {
        private readonly List<InspectAbnormalItem> items = [];

        public IReadOnlyList<InspectAbnormalItem> LoadItems()
        {
            return items
                .OrderByDescending(item => item.InspectAt)
                .ThenByDescending(item => item.UpdatedAt)
                .ToArray();
        }

        public void Upsert(InspectAbnormalItem item)
        {
            var index = items.FindIndex(current =>
                current.Id == item.Id
                || (string.Equals(current.DeviceCode, item.DeviceCode, StringComparison.OrdinalIgnoreCase)
                    && current.AbnormalClass == item.AbnormalClass
                    && string.Equals(current.Conclusion, item.Conclusion, StringComparison.Ordinal)));

            if (index >= 0)
            {
                items[index] = item;
                return;
            }

            items.Add(item);
        }
    }
}
