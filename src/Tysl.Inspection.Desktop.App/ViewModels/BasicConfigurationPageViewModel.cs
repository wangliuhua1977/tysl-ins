using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class BasicConfigurationPageViewModel(
    IGroupSyncService groupSyncService,
    ILogger<BasicConfigurationPageViewModel> logger) : ObservableObject
{
    public event EventHandler? SyncCompleted;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    private bool isBusy;

    [ObservableProperty]
    private int groupCount;

    [ObservableProperty]
    private int deviceCount;

    [ObservableProperty]
    private int successCount;

    [ObservableProperty]
    private int failureCount;

    [ObservableProperty]
    private string lastSyncedAtText = "尚无同步记录";

    [ObservableProperty]
    private string statusText = "可从此页面手动触发“同步监控目录树与目录设备”。";

    [ObservableProperty]
    private string failureDetails = string.Empty;

    public async Task LoadAsync()
    {
        var snapshot = await groupSyncService.GetLatestSnapshotAsync(CancellationToken.None);
        ApplySnapshot(snapshot);
    }

    private bool CanSync() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = "正在同步监控目录树与目录设备，请稍候...";
            FailureDetails = string.Empty;
            logger.LogInformation("Manual monitor region tree sync triggered from basic configuration page.");

            var summary = await groupSyncService.SyncAsync(CancellationToken.None);
            ApplySummary(summary);
            SyncCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySnapshot(LocalSyncSnapshot snapshot)
    {
        GroupCount = snapshot.GroupCount;
        DeviceCount = snapshot.DeviceCount;
        SuccessCount = 0;
        FailureCount = 0;
        LastSyncedAtText = snapshot.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无同步记录";

        if (snapshot.GroupCount == 0 && snapshot.DeviceCount == 0)
        {
            StatusText = "可从此页面手动触发“同步监控目录树与目录设备”。";
            FailureDetails = string.Empty;
            return;
        }

        StatusText = snapshot.Metadata.ReconciliationCompleted
            ? snapshot.Metadata.ReconciliationMatched
                ? "上一次监控目录树快照已落地，并完成首层最小对账。"
                : "上一次监控目录树快照已落地，但首层最小对账发现差异，请人工复核。"
            : "上一次监控目录树快照已落地，但首层最小对账未完成。";
        FailureDetails = BuildMetadataLine(snapshot.Metadata);
    }

    private void ApplySummary(GroupSyncSummary summary)
    {
        GroupCount = summary.GroupCount;
        DeviceCount = summary.DeviceCount;
        SuccessCount = summary.SuccessCount;
        FailureCount = summary.FailureCount;
        LastSyncedAtText = summary.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无同步记录";

        if (!summary.SnapshotReplaced)
        {
            StatusText = "同步完成，但存在阻断失败；为避免半新半旧快照，本地 SQLite 仍保留上一版完整快照，请查看失败明细与日志。";
        }
        else if (!summary.Metadata.ReconciliationCompleted)
        {
            StatusText = "同步完成，监控目录树快照已更新；最小对账未完成，请查看失败明细与日志。";
        }
        else if (!summary.Metadata.ReconciliationMatched)
        {
            StatusText = "同步完成，监控目录树快照已更新；首层最小对账发现差异，请人工复核。";
        }
        else
        {
            StatusText = "同步完成，监控目录树与目录设备已写入本地 SQLite，并完成首层最小对账。";
        }

        var detailLines = new List<string>();
        var metadataLine = BuildMetadataLine(summary.Metadata);
        if (!string.IsNullOrWhiteSpace(metadataLine))
        {
            detailLines.Add(metadataLine);
        }

        detailLines.AddRange(summary.Failures.Select(failure =>
        {
            var prefix = string.IsNullOrWhiteSpace(failure.GroupId)
                ? failure.FailureKind.ToString()
                : $"{failure.FailureKind} / {failure.GroupName} ({failure.GroupId})";
            return $"{prefix}: {failure.Message}";
        }));

        FailureDetails = string.Join(Environment.NewLine, detailLines);
    }

    private static string BuildMetadataLine(GroupSyncSnapshotMetadata metadata)
    {
        if (metadata == GroupSyncSnapshotMetadata.Empty)
        {
            return string.Empty;
        }

        if (!metadata.ReconciliationCompleted)
        {
            return string.IsNullOrWhiteSpace(metadata.ReconciliationScopeText)
                ? "最小对账：尚未执行。"
                : $"最小对账：{metadata.ReconciliationScopeText}";
        }

        return $"平台拉回目录 {metadata.PlatformGroupCount} 个、设备 {metadata.PlatformDeviceCount} 台；已对账范围：{metadata.ReconciliationScopeText}；平台对账设备 {metadata.ReconciledDeviceCount} 台、在线 {metadata.ReconciledOnlineCount} 台。";
    }
}
