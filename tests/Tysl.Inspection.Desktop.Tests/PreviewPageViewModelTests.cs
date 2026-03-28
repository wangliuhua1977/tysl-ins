using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
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
                "播放器未能完成播放建链。")
        };
        var viewModel = new PreviewPageViewModel(
            previewService,
            new StubPlayWinSvc(),
            NullLogger<PreviewPageViewModel>.Instance);

        await viewModel.InitializeAsync();
        await viewModel.RequestInspectCommand.ExecuteAsync(null);

        Assert.Equal("巡检失败：播放建链失败", viewModel.InspectConclusion);
        Assert.Equal("播放建链失败", viewModel.InspectFailureCategory);
        Assert.Contains("在线状态：在线", viewModel.InspectStageText);
        Assert.Contains("播放建链：已启动", viewModel.InspectStageText);
        Assert.Contains("Playing：未进入", viewModel.InspectStageText);
        Assert.Equal("播放器未能完成播放建链。", viewModel.InspectDetailText);
        Assert.Equal("2026-03-28 10:05:00", viewModel.InspectAtText);
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
            "仅在发起巡检诊断后展示最小结果。");

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
}
