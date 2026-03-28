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

    [Theory]
    [InlineData(PlayStage.Initializing, "正在初始化播放器")]
    [InlineData(PlayStage.Connecting, "正在建立播放链路")]
    [InlineData(PlayStage.Playing, "正在播放")]
    [InlineData(PlayStage.Stopped, "已停止播放")]
    [InlineData(PlayStage.InitFailed, "播放初始化失败")]
    [InlineData(PlayStage.LinkFailed, "播放建链失败")]
    [InlineData(PlayStage.Interrupted, "播放过程中断")]
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
