using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Application.Services;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Infrastructure.Persistence;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class InspectAbnormalStoreTests
{
    [Theory]
    [InlineData(InspectAbnormalClass.Offline, "巡检失败：设备离线", "设备离线", "设备离线，当前无法获取预览地址。", "离线")]
    [InlineData(InspectAbnormalClass.RtspNotReady, "巡检失败：RTSP 未就绪", "RTSP 响应解密失败", "RTSP 响应解密失败", "RTSP 未就绪")]
    [InlineData(InspectAbnormalClass.PlayFailed, "巡检失败：播放失败", "播放建链失败", "播放器未能完成播放建链。", "播放失败")]
    public void Add_PersistsAllowedAbnormalClasses(
        InspectAbnormalClass abnormalClass,
        string conclusion,
        string failureCategory,
        string detailMessage,
        string abnormalClassText)
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();

        var item = store.Add(BuildResult(
            abnormalClass,
            conclusion,
            failureCategory,
            detailMessage,
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        Assert.NotNull(item);

        var restoredStore = context.CreateStore();
        var saved = Assert.Single(restoredStore.GetItems());
        Assert.Equal(abnormalClass, saved.AbnormalClass);
        Assert.Equal(abnormalClassText, saved.AbnormalClassText);
        Assert.Equal(conclusion, saved.Conclusion);
        Assert.Equal(failureCategory, saved.FailureCategory);
        Assert.False(saved.IsReviewed);
        Assert.False(saved.IsRecoveredConfirmed);
        Assert.Equal("暂无", saved.RecoveredConfirmedAtText);
        Assert.Equal("暂无", saved.RecoveredSummaryText);
        Assert.Equal(InspectHandleStatus.Pending, saved.HandleStatus);
        Assert.Equal("待处理", saved.HandleStatusText);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"), saved.HandleUpdatedAt);
    }

    [Fact]
    public void Add_DoesNotPersistPassedInspectResult()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
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
        Assert.Empty(context.CreateStore().GetItems());
    }

    [Fact]
    public void Add_DeduplicatesByDeviceClassAndConclusion_AndUpdatesLatestSummary()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();

        var first = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            "首次诊断：播放器未能完成播放建链。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));
        store.ToggleReviewed(first!.Id);

        var deduped = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "地址可能失效",
            "再次诊断：播放器输出 rtsp://demo/live/dev-001?token=abc 后提示地址失效。",
            DateTimeOffset.Parse("2026-03-29T10:30:00+08:00")));

        var saved = Assert.Single(store.GetItems());
        Assert.NotNull(deduped);
        Assert.Equal(first.Id, deduped.Id);
        Assert.Equal(first.Id, saved.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T10:30:00+08:00"), saved.InspectAt);
        Assert.Equal("地址可能失效", saved.FailureCategory);
        Assert.True(saved.IsReviewed);
        Assert.False(saved.IsRecoveredConfirmed);
        Assert.Contains("再次诊断", saved.SummaryText);
        Assert.DoesNotContain("rtsp://demo/live/dev-001?token=abc", saved.SummaryText);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.Equal(saved.Id, restored.Id);
        Assert.True(restored.IsReviewed);
        Assert.Equal(saved.InspectAt, restored.InspectAt);
        Assert.Equal(saved.FailureCategory, restored.FailureCategory);
        Assert.Equal(saved.SummaryText, restored.SummaryText);
        Assert.False(restored.IsRecoveredConfirmed);
    }

    [Fact]
    public void Reinspect_StillAbnormalUpdatesOriginalRecord_WithoutCreatingDuplicate()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            "播放器未能完成播放建链。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        store.ToggleReviewed(item!.Id);
        store.AdvanceHandleStatus(item.Id);

        var updated = store.Reinspect(
            item.Id,
            BuildResult(
                InspectAbnormalClass.RtspNotReady,
                "巡检失败：RTSP 未就绪",
                "RTSP 响应解密失败",
                "复检结果：RTSP 响应解密失败。",
                DateTimeOffset.Parse("2026-03-29T10:30:00+08:00")));

        var saved = Assert.Single(store.GetItems());
        Assert.NotNull(updated);
        Assert.Equal(item.Id, updated.Id);
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T10:30:00+08:00"), saved.InspectAt);
        Assert.Equal("巡检失败：RTSP 未就绪", saved.Conclusion);
        Assert.Equal(InspectAbnormalClass.RtspNotReady, saved.AbnormalClass);
        Assert.Equal("RTSP 未就绪", saved.AbnormalClassText);
        Assert.Equal("RTSP 响应解密失败", saved.FailureCategory);
        Assert.True(saved.IsReviewed);
        Assert.Equal(InspectHandleStatus.InProgress, saved.HandleStatus);
        Assert.Equal("处理中", saved.HandleStatusText);
        Assert.False(saved.IsRecoveredConfirmed);
        Assert.Equal(string.Empty, saved.RecoveredSummary);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(saved.Conclusion, restored.Conclusion);
        Assert.Equal(saved.AbnormalClass, restored.AbnormalClass);
        Assert.True(restored.IsReviewed);
        Assert.Equal(InspectHandleStatus.InProgress, restored.HandleStatus);
        Assert.False(restored.IsRecoveredConfirmed);
    }

    [Fact]
    public void Reinspect_Passes_MarksRecoveredConfirmedAndPersists()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            "播放器未能完成播放建链。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        store.ToggleReviewed(item!.Id);
        store.AdvanceHandleStatus(item.Id);

        var recovered = store.Reinspect(
            item.Id,
            new InspectResult(
                DateTimeOffset.Parse("2026-03-29T10:40:00+08:00"),
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
                InspectAbnormalClass.None));

        var saved = Assert.Single(store.GetItems());
        Assert.NotNull(recovered);
        Assert.Equal(item.Id, recovered.Id);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T10:40:00+08:00"), saved.InspectAt);
        Assert.Equal("巡检失败：播放失败", saved.Conclusion);
        Assert.True(saved.IsReviewed);
        Assert.Equal(InspectHandleStatus.InProgress, saved.HandleStatus);
        Assert.True(saved.IsRecoveredConfirmed);
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T10:40:00+08:00"), saved.RecoveredConfirmedAt);
        Assert.Contains("复检通过", saved.RecoveredSummary);
        Assert.Contains("无异常/巡检通过", saved.RecoveredSummary);
        Assert.DoesNotContain("rtsp://", saved.RecoveredSummary, StringComparison.OrdinalIgnoreCase);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.True(restored.IsRecoveredConfirmed);
        Assert.Equal(saved.RecoveredConfirmedAt, restored.RecoveredConfirmedAt);
        Assert.Equal(saved.RecoveredSummary, restored.RecoveredSummary);
        Assert.True(restored.IsReviewed);
        Assert.Equal(InspectHandleStatus.InProgress, restored.HandleStatus);
    }

    [Fact]
    public void ToggleReviewed_PersistsState()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            "播放器未能完成播放建链。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        var reviewed = store.ToggleReviewed(item!.Id);

        Assert.NotNull(reviewed);
        Assert.True(reviewed.IsReviewed);
        Assert.Equal("已复核", reviewed.ReviewedText);
        Assert.Equal(InspectHandleStatus.Pending, reviewed.HandleStatus);
        Assert.Equal("待处理", reviewed.HandleStatusText);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.True(restored.IsReviewed);
        Assert.Equal("已复核", restored.ReviewedText);
        Assert.Equal(InspectHandleStatus.Pending, restored.HandleStatus);
        Assert.Equal("待处理", restored.HandleStatusText);
    }

    [Fact]
    public void LoadItems_RestoresPersistedPoolAfterReload()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();

        store.Add(BuildResult(
            InspectAbnormalClass.Offline,
            "巡检失败：设备离线",
            "设备离线",
            "设备离线，当前无法获取预览地址。",
            DateTimeOffset.Parse("2026-03-29T09:00:00+08:00"),
            deviceCode: "dev-001",
            deviceName: "一号点位"));
        store.Add(BuildResult(
            InspectAbnormalClass.RtspNotReady,
            "巡检失败：RTSP 未就绪",
            "RTSP 响应解密失败",
            "RTSP 响应解密失败",
            DateTimeOffset.Parse("2026-03-29T11:00:00+08:00"),
            deviceCode: "dev-002",
            deviceName: "二号点位"));

        var restoredStore = context.CreateStore();
        var restored = restoredStore.GetItems();

        Assert.Equal(2, restored.Count);
        Assert.Equal("dev-002", restored[0].DeviceCode);
        Assert.Equal("dev-001", restored[1].DeviceCode);
        Assert.All(restored, item =>
        {
            Assert.Equal(InspectHandleStatus.Pending, item.HandleStatus);
            Assert.False(item.IsRecoveredConfirmed);
        });
    }

    [Fact]
    public void AdvanceHandleStatus_PersistsMinimalTransitionSequence()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            "播放器未能完成播放建链。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        var processing = store.AdvanceHandleStatus(item!.Id);
        var handled = store.AdvanceHandleStatus(item.Id);
        var rolledBack = store.AdvanceHandleStatus(item.Id);

        Assert.NotNull(processing);
        Assert.Equal(InspectHandleStatus.InProgress, processing!.HandleStatus);
        Assert.Equal("处理中", processing.HandleStatusText);

        Assert.NotNull(handled);
        Assert.Equal(InspectHandleStatus.Handled, handled!.HandleStatus);
        Assert.Equal("已处理", handled.HandleStatusText);

        Assert.NotNull(rolledBack);
        Assert.Equal(InspectHandleStatus.InProgress, rolledBack!.HandleStatus);
        Assert.Equal("处理中", rolledBack.HandleStatusText);
        Assert.True(rolledBack.HandleUpdatedAt >= item.InspectAt);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.Equal(InspectHandleStatus.InProgress, restored.HandleStatus);
        Assert.Equal("处理中", restored.HandleStatusText);
        Assert.True(restored.HandleUpdatedAt >= item.InspectAt);
    }

    [Fact]
    public void Add_StoresMaskedSummary_WhenDetailContainsRtsp()
    {
        using var context = new SqliteAbnormalStoreTestContext();
        var store = context.CreateStore();
        var rawRtsp = "rtsp://demo/live/dev-001?token=abc";
        var item = store.Add(BuildResult(
            InspectAbnormalClass.PlayFailed,
            "巡检失败：播放失败",
            "播放建链失败",
            $"播放器输出原始地址 {rawRtsp} 后建链失败。",
            DateTimeOffset.Parse("2026-03-29T10:00:00+08:00")));

        Assert.NotNull(item);
        Assert.DoesNotContain(rawRtsp, item.SummaryText);
        Assert.Contains("RTSP 地址不展示明文", item.SummaryText);

        var restored = Assert.Single(context.CreateStore().GetItems());
        Assert.DoesNotContain(rawRtsp, restored.SummaryText);
    }

    private static InspectResult BuildResult(
        InspectAbnormalClass abnormalClass,
        string conclusion,
        string failureCategory,
        string detailMessage,
        DateTimeOffset inspectAt,
        string deviceCode = "dev-001",
        string deviceName = "测试设备")
    {
        return new InspectResult(
            inspectAt,
            deviceName,
            deviceCode,
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

    private sealed class SqliteAbnormalStoreTestContext : IDisposable
    {
        private readonly string rootPath;
        private readonly AppRuntimePaths runtimePaths;
        private readonly IOptions<DatabaseOptions> databaseOptions;

        public SqliteAbnormalStoreTestContext()
        {
            rootPath = Path.Combine(Path.GetTempPath(), $"tysl-ins-tests-{Guid.NewGuid():N}");
            var dataPath = Path.Combine(rootPath, "data");
            runtimePaths = new AppRuntimePaths(
                rootPath,
                Path.Combine(rootPath, "logs"),
                dataPath,
                Path.Combine(dataPath, "inspection.db"),
                Path.Combine(dataPath, "token-cache.json"));
            databaseOptions = Options.Create(new DatabaseOptions());

            Directory.CreateDirectory(rootPath);

            var bootstrapper = new SqliteBootstrapper(
                databaseOptions,
                runtimePaths,
                NullLogger<SqliteBootstrapper>.Instance);
            bootstrapper.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public InspectAbnormalStore CreateStore()
        {
            return new InspectAbnormalStore(
                new SqliteInspectAbnormalPoolStore(databaseOptions, runtimePaths),
                NullLogger<InspectAbnormalStore>.Instance);
        }

        public void Dispose()
        {
            if (!Directory.Exists(rootPath))
            {
                return;
            }

            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
