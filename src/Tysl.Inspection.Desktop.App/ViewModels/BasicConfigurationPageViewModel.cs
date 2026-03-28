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
    private string statusText = "可从此页面手动触发“同步分组与设备”。";

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
            StatusText = "正在同步分组与设备，请稍候...";
            FailureDetails = string.Empty;
            logger.LogInformation("Manual sync triggered from basic configuration page.");

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
    }

    private void ApplySummary(GroupSyncSummary summary)
    {
        GroupCount = summary.GroupCount;
        DeviceCount = summary.DeviceCount;
        SuccessCount = summary.SuccessCount;
        FailureCount = summary.FailureCount;
        LastSyncedAtText = summary.LastSyncedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "尚无同步记录";
        StatusText = summary.IsSuccess
            ? "同步完成，分组和设备已写入本地 SQLite。"
            : "同步完成，但存在失败项，请查看失败明细与日志。";
        FailureDetails = string.Join(
            Environment.NewLine,
            summary.Failures.Select(failure =>
            {
                var prefix = string.IsNullOrWhiteSpace(failure.GroupId)
                    ? failure.FailureKind.ToString()
                    : $"{failure.FailureKind} / {failure.GroupName} ({failure.GroupId})";
                return $"{prefix}: {failure.Message}";
            }));
    }
}
