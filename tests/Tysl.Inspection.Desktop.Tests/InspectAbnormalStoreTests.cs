using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class InspectAbnormalStoreTests
{
    [Theory]
    [InlineData(InspectAbnormalClass.Offline, "巡检失败：设备离线", "设备离线", "设备离线，当前无法获取预览地址。", "离线")]
    [InlineData(InspectAbnormalClass.RtspNotReady, "巡检失败：RTSP 地址未就绪", "RTSP 响应解密失败", "RTSP 响应解密失败", "RTSP 未就绪")]
    [InlineData(InspectAbnormalClass.PlayFailed, "巡检失败：播放建链失败", "播放建链失败", "播放器未能完成播放建链。", "播放失败")]
    public void Add_AddsAllowedAbnormalClasses(
        InspectAbnormalClass abnormalClass,
        string conclusion,
        string failureCategory,
        string detailMessage,
        string abnormalClassText)
    {
        var store = CreateStore();

        var item = store.Add(BuildResult(abnormalClass, conclusion, failureCategory, detailMessage));

        var saved = Assert.Single(store.GetItems());
        Assert.NotNull(item);
        Assert.Equal(abnormalClassText, saved.AbnormalClassText);
        Assert.Equal(conclusion, saved.Conclusion);
        Assert.False(saved.IsReviewed);
    }

    [Fact]
    public void Add_DoesNotAddPassedInspectResult()
    {
        var store = CreateStore();
        var result = new InspectResult(
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
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
            InspectAbnormalClass.None);

        var item = store.Add(result);

        Assert.Null(item);
        Assert.Empty(store.GetItems());
    }

    [Fact]
    public void ToggleReviewed_TogglesState()
    {
        var store = CreateStore();
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放建链失败",
            "播放建链失败",
            "播放器未能完成播放建链。"));

        var reviewed = store.ToggleReviewed(item!.Id);
        var reset = store.ToggleReviewed(item.Id);

        Assert.NotNull(reviewed);
        Assert.True(reviewed.IsReviewed);
        Assert.Equal("已复核", reviewed.ReviewedText);
        Assert.NotNull(reset);
        Assert.False(reset.IsReviewed);
        Assert.Equal("未复核", reset.ReviewedText);
    }

    [Fact]
    public void Add_StoresMaskedSummary_WhenDetailContainsRtsp()
    {
        var store = CreateStore();
        var rawRtsp = "rtsp://demo/live/dev-001?token=abc";
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放建链失败",
            "播放建链失败",
            $"播放器输出原始地址 {rawRtsp} 后建链失败。"));

        Assert.NotNull(item);
        Assert.DoesNotContain(rawRtsp, item.SummaryText);
        Assert.Contains("RTSP 地址不展示地址明文", item.SummaryText);
    }

    private static InspectAbnormalStore CreateStore()
    {
        return new InspectAbnormalStore(NullLogger<InspectAbnormalStore>.Instance);
    }

    private static InspectResult BuildResult(
        InspectAbnormalClass abnormalClass,
        string conclusion,
        string failureCategory,
        string detailMessage)
    {
        return new InspectResult(
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
            "测试设备",
            "dev-001",
            true,
            abnormalClass is InspectAbnormalClass.Offline ? "离线" : "在线",
            abnormalClass is not InspectAbnormalClass.Offline,
            abnormalClass is InspectAbnormalClass.PlayFailed,
            false,
            conclusion,
            failureCategory,
            detailMessage,
            abnormalClass);
    }
}
