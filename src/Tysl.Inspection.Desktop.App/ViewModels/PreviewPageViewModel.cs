using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.App.Services;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class PreviewPageViewModel(
    IPreviewService previewService,
    IInspectAbnormalStore abnormalStore,
    IPlayWinSvc playWinSvc,
    ILogger<PreviewPageViewModel> logger) : ObservableObject
{
    private bool hasLoaded;
    private int selectedDeviceDetailLoadVersion;
    private IReadOnlyDictionary<string, InspectionDevice> deviceDetailsByCode = new Dictionary<string, InspectionDevice>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, DeviceUserMaintenance> deviceMaintenanceByCode = new Dictionary<string, DeviceUserMaintenance>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, PreviewDirectoryGroupItem> directoryGroupById = new Dictionary<string, PreviewDirectoryGroupItem>(StringComparer.OrdinalIgnoreCase);
    private CoordinateProjectionResult? selectedDeviceProjection;

    public ObservableCollection<PreviewDeviceOption> Devices { get; } = [];

    public ObservableCollection<PreviewDirectoryGroupItem> DirectoryGroups { get; } = [];

    public ObservableCollection<InspectAbnormalItem> AbnormalItems { get; } = [];

    [ObservableProperty]
    private string pageStatusText = "正在加载本地点位...";

    [ObservableProperty]
    private string directoryStatusText = "正在加载真实监控目录树...";

    [ObservableProperty]
    private int directoryGroupCount;

    [ObservableProperty]
    private int directoryDeviceCount;

    [ObservableProperty]
    private int directorySnapshotGroupCount;

    [ObservableProperty]
    private int directorySnapshotDeviceCount;

    [ObservableProperty]
    private int directoryPlatformGroupCount;

    [ObservableProperty]
    private int directoryPlatformDeviceCount;

    [ObservableProperty]
    private bool directoryReconciliationCompleted;

    [ObservableProperty]
    private bool directoryReconciliationMatched;

    [ObservableProperty]
    private int directoryReconciledRegionCount;

    [ObservableProperty]
    private int directoryReconciledDeviceCount;

    [ObservableProperty]
    private int directoryReconciledOnlineCount;

    [ObservableProperty]
    private string directoryReconciliationScopeText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private PreviewDeviceOption? selectedDevice;

    [ObservableProperty]
    private string deviceName = "暂无";

    [ObservableProperty]
    private string deviceCode = "暂无";

    [ObservableProperty]
    private string selectedDeviceDirectoryPathText = "暂无";

    [ObservableProperty]
    private string selectedDeviceOnlineStatusText = "暂无";

    [ObservableProperty]
    private string selectedDeviceLatitudeText = "无";

    [ObservableProperty]
    private string selectedDeviceLongitudeText = "无";

    [ObservableProperty]
    private string selectedDeviceRawCoordinateText = "无";

    [ObservableProperty]
    private string selectedDeviceMapCoordinateText = "无";

    [ObservableProperty]
    private string selectedDeviceLocationText = "暂无";

    [ObservableProperty]
    private string selectedDeviceRecentInspectText = "暂无最近巡检记录";

    [ObservableProperty]
    private string selectedDeviceAbnormalPoolText = "异常池暂无该点位记录";

    [ObservableProperty]
    private string selectedDeviceAbnormalSummaryText = "异常池暂无该点位记录";

    [ObservableProperty]
    private string selectedDeviceHandleStatusText = "暂无";

    [ObservableProperty]
    private string selectedDeviceRecoveredStatusText = "暂无";

    [ObservableProperty]
    private string selectedDeviceCoordinateSourceText = "无";

    [ObservableProperty]
    private string selectedDeviceCoordinateStatusText = "平台未提供坐标";

    [ObservableProperty]
    private string selectedDeviceCoordinateRemarkText = "平台未提供坐标，当前不进入上图。";

    [ObservableProperty]
    private string selectedDeviceMaintenanceStatusText = string.Empty;

    [ObservableProperty]
    private string selectedDeviceMaintenanceNoteText = string.Empty;

    [ObservableProperty]
    private string selectedDeviceManualConfirmationNoteText = string.Empty;

    [ObservableProperty]
    private string selectedDeviceMaintenanceUpdatedAtText = "暂无";

    [ObservableProperty]
    private string diagnosisText = "尚未发起诊断";

    [ObservableProperty]
    private string addressStatusText = "尚未发起预览地址获取";

    [ObservableProperty]
    private string rtspUrl = string.Empty;

    [ObservableProperty]
    private string expireText = "平台未返回有效期";

    [ObservableProperty]
    private string requestedAtText = "暂无";

    [ObservableProperty]
    private string inspectConclusion = "尚未发起巡检诊断";

    [ObservableProperty]
    private string inspectFailureCategory = "暂无";

    [ObservableProperty]
    private string inspectAbnormalClassText = string.Empty;

    [ObservableProperty]
    private string inspectStageText = "在线状态：暂无 | RTSP：未校验 | 播放建链：未启动 | Playing：未进入";

    [ObservableProperty]
    private string inspectDetailText = "仅在发起巡检诊断后展示最小结果。";

    [ObservableProperty]
    private string inspectAtText = "暂无";

    [ObservableProperty]
    private string inspectSummaryText = "仅在发起巡检诊断后生成最小处置摘要。";

    public bool IsPlayWindowReady => CanOpenPlayWindow();

    public string PlayWindowHintText => IsPlayWindowReady
        ? "RTSP 地址已就绪，可打开独立播放窗口。"
        : "请先成功获取 RTSP 地址后再打开播放窗口。";

    public string AbnormalListHintText => AbnormalItems.Count > 0
        ? $"当前异常池共 {AbnormalItems.Count} 条；“已复核”“处置状态”“已恢复确认”相互独立，恢复确认仅在复检通过后标记。"
        : "当前异常池暂无异常项；复检通过会在原记录上标记“已恢复确认”。";

    public bool DirectoryCountsMatch =>
        DirectoryGroupCount == DirectorySnapshotGroupCount
        && DirectoryDeviceCount == DirectorySnapshotDeviceCount;

    public string DirectoryPlatformSummaryText => $"目录 {DirectoryPlatformGroupCount} / 设备 {DirectoryPlatformDeviceCount}";

    public string DirectoryVerificationText
    {
        get
        {
            if (DirectorySnapshotGroupCount == 0 && DirectorySnapshotDeviceCount == 0)
            {
                return "当前本地 SQLite 中暂无目录快照，请先执行一次全量同步。";
            }

            var bindingText = DirectoryCountsMatch
                ? "当前目录绑定与本地 SQLite 快照一致。"
                : $"当前目录绑定与本地 SQLite 快照不一致：目录 {DirectoryGroupCount}/{DirectorySnapshotGroupCount}，设备 {DirectoryDeviceCount}/{DirectorySnapshotDeviceCount}。";
            var platformText = DirectoryPlatformGroupCount > 0 || DirectoryPlatformDeviceCount > 0
                ? $"平台本轮拉回目录 {DirectoryPlatformGroupCount} 个、设备 {DirectoryPlatformDeviceCount} 台。"
                : "平台拉回计数尚未写入本地快照。";

            if (!DirectoryReconciliationCompleted)
            {
                var unfinishedText = string.IsNullOrWhiteSpace(DirectoryReconciliationScopeText)
                    ? "首层 getCusDeviceCount 对账尚未执行。"
                    : DirectoryReconciliationScopeText;
                return $"{bindingText} {platformText} 最小对账未完成：{unfinishedText}";
            }

            var resultText = DirectoryReconciliationMatched
                ? "结果一致。"
                : "结果存在差异，请人工复核。";
            return $"{bindingText} {platformText} 已对账范围：{DirectoryReconciliationScopeText}；首层返回 {DirectoryReconciledRegionCount} 个 regionCode，平台对账设备 {DirectoryReconciledDeviceCount} 台、在线 {DirectoryReconciledOnlineCount} 台，{resultText}";
        }
    }

    public async Task InitializeAsync()
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        PageStatusText = "正在加载本地点位...";
        DirectoryStatusText = "正在加载真实监控目录树...";

        var currentCode = SelectedDevice?.DeviceCode;
        var result = await previewService.LoadLocalDevicesAsync(CancellationToken.None);
        deviceDetailsByCode = result.DeviceDetailsByCode;
        deviceMaintenanceByCode = result.DeviceMaintenanceByCode;
        directoryGroupById = result.DirectoryGroups.ToDictionary(group => group.GroupId, StringComparer.OrdinalIgnoreCase);

        Devices.Clear();
        DirectoryGroups.Clear();
        ReloadAbnormalItems();

        foreach (var device in result.Devices)
        {
            Devices.Add(device);
        }

        foreach (var group in result.DirectoryGroups)
        {
            DirectoryGroups.Add(group);
        }

        ApplyDirectorySummary(result);

        if (!result.Success)
        {
            PageStatusText = result.Message;
            DirectoryStatusText = result.Message;
            SelectedDevice = null;
            ResetPreviewResult();
            logger.LogWarning("Monitor region tree load failed for UI binding. Message={Message}.", result.Message);
            return;
        }

        DirectoryStatusText = DirectoryGroups.Count > 0
            ? BuildDirectoryStatusText(
                DirectoryGroups.Count,
                Devices.Count,
                result.SnapshotGroupCount,
                result.SnapshotDeviceCount,
                result.Metadata,
                result.LastSyncedAt)
            : "本地 SQLite 中暂无真实监控目录树数据，请先完成同步。";

        PageStatusText = Devices.Count > 0
            ? $"已加载 {Devices.Count} 个点位，可直接发起单点预览或巡检。"
            : "本地 SQLite 中暂无点位数据，请先完成同步。";

        logger.LogInformation(
            "Monitor region tree loaded and bound to UI. Groups={GroupCount}, Devices={DeviceCount}, PlatformGroups={PlatformGroupCount}, PlatformDevices={PlatformDeviceCount}, ReconciliationCompleted={ReconciliationCompleted}, ReconciliationMatched={ReconciliationMatched}, LastSyncedAt={LastSyncedAt}.",
            DirectoryGroups.Count,
            Devices.Count,
            DirectoryPlatformGroupCount,
            DirectoryPlatformDeviceCount,
            DirectoryReconciliationCompleted,
            DirectoryReconciliationMatched,
            result.LastSyncedAt?.ToString("O") ?? "null");

        if (!DirectoryCountsMatch)
        {
            logger.LogWarning(
                "Preview page directory binding mismatch. BoundGroups={BoundGroupCount}, SnapshotGroups={SnapshotGroupCount}, BoundDevices={BoundDeviceCount}, SnapshotDevices={SnapshotDeviceCount}.",
                DirectoryGroupCount,
                DirectorySnapshotGroupCount,
                DirectoryDeviceCount,
                DirectorySnapshotDeviceCount);
        }

        SelectedDevice = Devices.FirstOrDefault(device => string.Equals(device.DeviceCode, currentCode, StringComparison.OrdinalIgnoreCase))
            ?? Devices.FirstOrDefault();

        if (SelectedDevice is null)
        {
            ResetPreviewResult();
        }
        else
        {
            ApplyDevice(SelectedDevice);
        }

        RefreshSelectedDeviceDetailSummary();
    }

    [RelayCommand(CanExecute = nameof(CanRequestPreview))]
    private async Task RequestPreviewAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            PageStatusText = $"正在为 {SelectedDevice.DeviceName} 准备预览地址...";

            var result = await previewService.PrepareAsync(SelectedDevice.DeviceCode, CancellationToken.None);
            Apply(result);

            PageStatusText = result.Success
                ? "单点预览准备完成。"
                : "单点预览准备已结束，请查看诊断结果。";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Single preview page request failed.");
            DiagnosisText = $"单点预览准备失败：{exception.Message}";
            AddressStatusText = "未发起预览地址获取";
            RtspUrl = string.Empty;
            ExpireText = "平台未返回有效期";
            RequestedAtText = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            PageStatusText = "单点预览准备失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRequestInspect))]
    private async Task RequestInspectAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            PageStatusText = $"正在为 {SelectedDevice.DeviceName} 执行最小巡检诊断...";

            var result = await previewService.InspectAsync(SelectedDevice.DeviceCode, CancellationToken.None);
            ApplyInspect(result);
            AddAbnormalItem(result);
            PageStatusText = "最小巡检诊断已完成，请查看结果。";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Single inspect page request failed.");
            ApplyInspect(
                new InspectResult(
                    DateTimeOffset.Now,
                    DeviceName,
                    DeviceCode,
                    false,
                    "未获取",
                    false,
                    false,
                    false,
                    "巡检失败：巡检执行异常",
                    "巡检执行异常",
                    $"最小巡检诊断失败：{exception.Message}",
                    InspectAbnormalClass.None));
            PageStatusText = "最小巡检诊断失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanReinspect))]
    private async Task ReinspectAsync(InspectAbnormalItem? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SelectDeviceByCode(item.DeviceCode);
            PageStatusText = $"正在对 {item.DeviceName} 执行异常复检...";
            logger.LogInformation("Inspect abnormal reinspect started for {DeviceCode}.", item.DeviceCode);

            var result = await previewService.InspectAsync(item.DeviceCode, CancellationToken.None);
            ApplyInspect(result);

            var updated = abnormalStore.Reinspect(item.Id, result);
            ReloadAbnormalItems();

            if (updated is null)
            {
                PageStatusText = "异常复检完成，但原异常记录不存在。";
                logger.LogWarning("Inspect abnormal reinspect completed but original item not found. AbnormalId={AbnormalId}.", item.Id);
                return;
            }

            PageStatusText = updated.IsRecoveredConfirmed
                ? $"{updated.DeviceName} 复检通过，已恢复确认。"
                : $"{updated.DeviceName} 复检完成，已更新原异常记录。";

            logger.LogInformation(
                "Inspect abnormal reinspect completed for {DeviceCode}. RecoveredConfirmed={RecoveredConfirmed}.",
                updated.DeviceCode,
                updated.IsRecoveredConfirmed);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Inspect abnormal reinspect failed for {DeviceCode}.", item.DeviceCode);
            PageStatusText = $"异常复检失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveSelectedDeviceMaintenance))]
    private async Task SaveSelectedDeviceMaintenanceAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            PageStatusText = $"正在保存 {SelectedDevice.DeviceName} 的用户维护信息...";

            var result = await previewService.SaveDeviceMaintenanceAsync(
                SelectedDevice.DeviceCode,
                SelectedDeviceMaintenanceStatusText,
                SelectedDeviceMaintenanceNoteText,
                SelectedDeviceManualConfirmationNoteText,
                CancellationToken.None);

            if (!result.Success || result.Maintenance is null)
            {
                PageStatusText = result.Message;
                return;
            }

            deviceMaintenanceByCode = new Dictionary<string, DeviceUserMaintenance>(deviceMaintenanceByCode, StringComparer.OrdinalIgnoreCase)
            {
                [result.Maintenance.DeviceCode] = result.Maintenance
            };

            ApplySelectedDeviceMaintenance(result.Maintenance);
            PageStatusText = $"{SelectedDevice.DeviceName} 的用户维护信息已保存。";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Saving user maintenance info failed for {DeviceCode}.", SelectedDevice.DeviceCode);
            PageStatusText = $"用户维护信息保存失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleReviewed(InspectAbnormalItem? item)
    {
        if (item is null)
        {
            return;
        }

        var updated = abnormalStore.ToggleReviewed(item.Id);
        if (updated is null)
        {
            return;
        }

        ReplaceAbnormalItem(item, updated);
    }

    [RelayCommand]
    private void AdvanceHandleStatus(InspectAbnormalItem? item)
    {
        if (item is null)
        {
            return;
        }

        var updated = abnormalStore.AdvanceHandleStatus(item.Id);
        if (updated is null)
        {
            return;
        }

        ReplaceAbnormalItem(item, updated);
    }

    [RelayCommand]
    private void SelectDirectoryDevice(PreviewDirectoryDeviceItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectDeviceByCode(item.DeviceCode);
        PageStatusText = $"已选中 {item.DeviceName}，可继续获取预览地址或执行巡检。";
    }

    [RelayCommand(CanExecute = nameof(CanOpenPlayWindow))]
    private void OpenPlayWin()
    {
        if (!CanOpenPlayWindow())
        {
            return;
        }

        var message = playWinSvc.Open(new PlayWinArgs(DeviceName, DeviceCode, RtspUrl));
        PageStatusText = message ?? $"已打开 {DeviceName} 的独立播放窗口。";
    }

    private bool CanRequestPreview()
    {
        return !IsBusy && SelectedDevice is not null;
    }

    private bool CanOpenPlayWindow()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(RtspUrl);
    }

    private bool CanRequestInspect()
    {
        return !IsBusy && SelectedDevice is not null;
    }

    private bool CanReinspect(InspectAbnormalItem? item)
    {
        return !IsBusy && item is not null;
    }

    private bool CanSaveSelectedDeviceMaintenance()
    {
        return !IsBusy && SelectedDevice is not null;
    }

    partial void OnSelectedDeviceChanged(PreviewDeviceOption? value)
    {
        selectedDeviceDetailLoadVersion++;
        selectedDeviceProjection = null;

        if (value is null)
        {
            ResetPreviewResult();
        }
        else
        {
            logger.LogInformation("Point detail load started for {DeviceCode}.", value.DeviceCode);
            ApplyDevice(value);
            _ = LoadSelectedDeviceDetailAsync(value.DeviceCode, selectedDeviceDetailLoadVersion);
        }

        RequestPreviewCommand.NotifyCanExecuteChanged();
        RequestInspectCommand.NotifyCanExecuteChanged();
        SaveSelectedDeviceMaintenanceCommand.NotifyCanExecuteChanged();
        OpenPlayWinCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPlayWindowReady));
        OnPropertyChanged(nameof(PlayWindowHintText));
        RefreshSelectedDeviceDetailSummary();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RequestPreviewCommand.NotifyCanExecuteChanged();
        RequestInspectCommand.NotifyCanExecuteChanged();
        ReinspectCommand.NotifyCanExecuteChanged();
        SaveSelectedDeviceMaintenanceCommand.NotifyCanExecuteChanged();
        OpenPlayWinCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPlayWindowReady));
        OnPropertyChanged(nameof(PlayWindowHintText));
    }

    partial void OnRtspUrlChanged(string value)
    {
        OpenPlayWinCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPlayWindowReady));
        OnPropertyChanged(nameof(PlayWindowHintText));
    }

    private void Apply(PreviewPrepareResult result)
    {
        DeviceName = result.DeviceName;
        DeviceCode = result.DeviceCode;
        DiagnosisText = result.DiagnosisText;
        AddressStatusText = result.AddressStatusText;
        RtspUrl = result.RtspUrl ?? string.Empty;
        ExpireText = result.ExpireText;
        RequestedAtText = result.RequestedAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void ApplyDevice(PreviewDeviceOption device)
    {
        DeviceName = device.DeviceName;
        DeviceCode = device.DeviceCode;
        DiagnosisText = "尚未发起诊断";
        AddressStatusText = "尚未发起预览地址获取";
        RtspUrl = string.Empty;
        ExpireText = "平台未返回有效期";
        RequestedAtText = "暂无";
        ResetInspectResult();
    }

    private void ResetPreviewResult()
    {
        DeviceName = "暂无";
        DeviceCode = "暂无";
        DiagnosisText = "尚未发起诊断";
        AddressStatusText = "尚未发起预览地址获取";
        RtspUrl = string.Empty;
        ExpireText = "平台未返回有效期";
        RequestedAtText = "暂无";
        ResetInspectResult();
    }

    private void ApplyInspect(InspectResult result)
    {
        DeviceName = result.DeviceName;
        DeviceCode = result.DeviceCode;
        InspectConclusion = result.Conclusion;
        InspectAbnormalClassText = result.AbnormalClassText;
        InspectFailureCategory = string.IsNullOrWhiteSpace(result.FailureCategory) ? "无" : result.FailureCategory;
        InspectStageText =
            $"在线状态：{result.OnlineStatus} | RTSP：{(result.RtspReady ? "已就绪" : "未就绪")} | 播放建链：{(result.PlaybackStarted ? "已启动" : "未启动")} | Playing：{(result.EnteredPlaying ? "已进入" : "未进入")}";
        InspectDetailText = result.DetailMessage;
        InspectAtText = result.InspectAt.ToString("yyyy-MM-dd HH:mm:ss");
        logger.LogInformation("Inspect disposition summary generation started for {DeviceCode}.", result.DeviceCode);
        InspectSummaryText = result.BuildDispositionSummary();
        logger.LogInformation(
            "Inspect disposition summary generation completed for {DeviceCode}. SummaryLength={SummaryLength}.",
            result.DeviceCode,
            InspectSummaryText.Length);
        RefreshSelectedDeviceDetailSummary();
    }

    private void AddAbnormalItem(InspectResult result)
    {
        var item = abnormalStore.Add(result);
        if (item is null)
        {
            return;
        }

        var existing = AbnormalItems.FirstOrDefault(current => current.Id == item.Id);
        if (existing is not null)
        {
            AbnormalItems.Remove(existing);
        }

        AbnormalItems.Insert(0, item);
        OnPropertyChanged(nameof(AbnormalListHintText));
        RefreshSelectedDeviceDetailSummary();
    }

    private void ResetInspectResult()
    {
        InspectConclusion = "尚未发起巡检诊断";
        InspectAbnormalClassText = string.Empty;
        InspectFailureCategory = "暂无";
        InspectStageText = "在线状态：暂无 | RTSP：未校验 | 播放建链：未启动 | Playing：未进入";
        InspectDetailText = "仅在发起巡检诊断后展示最小结果。";
        InspectAtText = "暂无";
        InspectSummaryText = "仅在发起巡检诊断后生成最小处置摘要。";
    }

    private void ReloadAbnormalItems()
    {
        AbnormalItems.Clear();

        foreach (var item in abnormalStore.GetItems())
        {
            AbnormalItems.Add(item);
        }

        OnPropertyChanged(nameof(AbnormalListHintText));
        RefreshSelectedDeviceDetailSummary();
    }

    private void ReplaceAbnormalItem(InspectAbnormalItem current, InspectAbnormalItem updated)
    {
        var index = AbnormalItems.IndexOf(current);
        if (index >= 0)
        {
            AbnormalItems[index] = updated;
        }
        else
        {
            ReloadAbnormalItems();
            return;
        }

        OnPropertyChanged(nameof(AbnormalListHintText));
        RefreshSelectedDeviceDetailSummary();
    }

    private void SelectDeviceByCode(string deviceCode)
    {
        SelectedDevice = Devices.FirstOrDefault(device => string.Equals(device.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshSelectedDeviceDetailSummary()
    {
        if (SelectedDevice is null)
        {
            ResetSelectedDeviceDetailSummary();
            return;
        }

        var detail = GetSelectedDeviceDetail();
        SelectedDeviceDirectoryPathText = BuildDirectoryPathText(detail);
        SelectedDeviceOnlineStatusText = BuildOnlineStatusText(detail?.OnlineStatus ?? SelectedDevice.OnlineStatus);
        SelectedDeviceLatitudeText = detail?.RawLatitude ?? "无";
        SelectedDeviceLongitudeText = detail?.RawLongitude ?? "无";
        SelectedDeviceRawCoordinateText = HasPlatformCoordinate(detail)
            ? $"{SelectedDeviceLatitudeText} / {SelectedDeviceLongitudeText}"
            : "无";
        SelectedDeviceMapCoordinateText = BuildMapCoordinateText(selectedDeviceProjection);
        SelectedDeviceLocationText = string.IsNullOrWhiteSpace(detail?.Location) ? "暂无" : detail.Location!;
        SelectedDeviceCoordinateSourceText = GetCoordinateSourceText(detail);
        SelectedDeviceCoordinateStatusText = BuildCoordinateStatusText(detail, selectedDeviceProjection);
        SelectedDeviceCoordinateRemarkText = BuildCoordinateRemarkText(detail, selectedDeviceProjection);
        SelectedDeviceRecentInspectText = BuildRecentInspectText();
        var latestAbnormal = GetLatestSelectedDeviceAbnormalItem();
        SelectedDeviceAbnormalSummaryText = latestAbnormal?.SummaryText ?? "异常池暂无该点位记录";
        SelectedDeviceHandleStatusText = latestAbnormal?.HandleStatusText ?? "暂无";
        SelectedDeviceRecoveredStatusText = latestAbnormal?.RecoveredConfirmedText ?? "暂无";
        SelectedDeviceAbnormalPoolText = BuildAbnormalPoolText(latestAbnormal);
        ApplySelectedDeviceMaintenance(GetSelectedDeviceMaintenance());
    }

    private void ResetSelectedDeviceDetailSummary()
    {
        SelectedDeviceDirectoryPathText = "暂无";
        SelectedDeviceOnlineStatusText = "暂无";
        SelectedDeviceLatitudeText = "无";
        SelectedDeviceLongitudeText = "无";
        SelectedDeviceRawCoordinateText = "无";
        SelectedDeviceMapCoordinateText = "无";
        SelectedDeviceLocationText = "暂无";
        SelectedDeviceCoordinateSourceText = "无";
        SelectedDeviceCoordinateStatusText = "平台未提供坐标";
        SelectedDeviceCoordinateRemarkText = "平台未提供坐标，当前不进入上图。";
        SelectedDeviceRecentInspectText = "暂无最近巡检记录";
        SelectedDeviceAbnormalSummaryText = "异常池暂无该点位记录";
        SelectedDeviceHandleStatusText = "暂无";
        SelectedDeviceRecoveredStatusText = "暂无";
        SelectedDeviceAbnormalPoolText = "异常池暂无该点位记录";
        selectedDeviceProjection = null;
        ApplySelectedDeviceMaintenance(null);
    }

    private InspectionDevice? GetSelectedDeviceDetail()
    {
        return SelectedDevice is not null && deviceDetailsByCode.TryGetValue(SelectedDevice.DeviceCode, out var detail)
            ? detail
            : null;
    }

    private string BuildDirectoryPathText(InspectionDevice? detail)
    {
        if (detail is null || string.IsNullOrWhiteSpace(detail.GroupId))
        {
            return "暂无";
        }

        var segments = new List<string>();
        var currentGroupId = detail.GroupId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(currentGroupId) && directoryGroupById.TryGetValue(currentGroupId, out var group))
        {
            segments.Add(group.GroupName);
            currentGroupId = group.ParentGroupId;
            guard++;
            if (guard > 32)
            {
                break;
            }
        }

        segments.Reverse();
        return segments.Count > 0
            ? string.Join(" / ", segments)
            : "暂无";
    }

    private string BuildRecentInspectText()
    {
        if (SelectedDevice is null)
        {
            return "暂无最近巡检记录";
        }

        if (string.Equals(DeviceCode, SelectedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(InspectAtText, "暂无", StringComparison.Ordinal))
        {
            return $"{InspectAtText} | {InspectConclusion} | {InspectFailureCategory}";
        }

        var latest = AbnormalItems
            .Where(item => string.Equals(item.DeviceCode, SelectedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.InspectAt)
            .FirstOrDefault();

        return latest is null
            ? "暂无最近巡检记录"
            : $"{latest.InspectAtText} | {latest.Conclusion} | {latest.FailureCategory}";
    }

    private InspectAbnormalItem? GetLatestSelectedDeviceAbnormalItem()
    {
        if (SelectedDevice is null)
        {
            return null;
        }

        return AbnormalItems
            .Where(item => string.Equals(item.DeviceCode, SelectedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.InspectAt)
            .FirstOrDefault();
    }

    private static string BuildAbnormalPoolText(InspectAbnormalItem? latest)
    {
        return latest is null
            ? "异常池暂无该点位记录"
            : $"{latest.HandleStatusText} | {latest.RecoveredConfirmedText} | {latest.AbnormalClassText} | {latest.SummaryText}";
    }

    private static string BuildOnlineStatusText(int? onlineStatus)
    {
        return onlineStatus switch
        {
            1 => "在线",
            0 => "离线",
            2 => "休眠（普通）",
            3 => "休眠（保活/AOV）",
            _ => "未知"
        };
    }

    private DeviceUserMaintenance? GetSelectedDeviceMaintenance()
    {
        return SelectedDevice is not null && deviceMaintenanceByCode.TryGetValue(SelectedDevice.DeviceCode, out var maintenance)
            ? maintenance
            : null;
    }

    private void ApplySelectedDeviceMaintenance(DeviceUserMaintenance? maintenance)
    {
        SelectedDeviceMaintenanceStatusText = maintenance?.MaintenanceStatus ?? string.Empty;
        SelectedDeviceMaintenanceNoteText = maintenance?.MaintenanceNote ?? string.Empty;
        SelectedDeviceManualConfirmationNoteText = maintenance?.ManualConfirmationNote ?? string.Empty;
        SelectedDeviceMaintenanceUpdatedAtText = maintenance is null
            ? "暂无"
            : maintenance.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private void LogSelectedDeviceDetailCompleted(PreviewDeviceOption device)
    {
        var detail = GetSelectedDeviceDetail();
        var abnormal = GetLatestSelectedDeviceAbnormalItem();
        var maintenance = GetSelectedDeviceMaintenance();
        logger.LogInformation(
            "Point detail load completed for {DeviceCode}. CoordinateSource={CoordinateSource}, CoordinateStatus={CoordinateStatus}, HasLocation={HasLocation}, HasAbnormal={HasAbnormal}, HasMaintenance={HasMaintenance}.",
            device.DeviceCode,
            detail?.CoordinateSource ?? "none",
            selectedDeviceProjection?.CoordinateState ?? detail?.CoordinateStatus ?? "none",
            !string.IsNullOrWhiteSpace(detail?.Location),
            abnormal is not null,
            maintenance is not null);
    }

    private static bool HasPlatformCoordinate(InspectionDevice? detail)
    {
        return !string.IsNullOrWhiteSpace(detail?.RawLatitude)
            && !string.IsNullOrWhiteSpace(detail?.RawLongitude);
    }

    private async Task LoadSelectedDeviceDetailAsync(string deviceCode, int loadVersion)
    {
        var result = await previewService.LoadDeviceDetailAsync(deviceCode, CancellationToken.None);
        if (loadVersion != selectedDeviceDetailLoadVersion
            || SelectedDevice is null
            || !string.Equals(SelectedDevice.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!result.Success || result.Device is null)
        {
            logger.LogWarning("Point detail coordinate refresh failed for {DeviceCode}. Message={Message}.", deviceCode, result.Message);
            selectedDeviceProjection = result.Projection;
            RefreshSelectedDeviceDetailSummary();
            return;
        }

        deviceDetailsByCode = new Dictionary<string, InspectionDevice>(deviceDetailsByCode, StringComparer.OrdinalIgnoreCase)
        {
            [result.Device.DeviceCode] = result.Device
        };
        selectedDeviceProjection = result.Projection;
        RefreshSelectedDeviceDetailSummary();
        LogSelectedDeviceDetailCompleted(SelectedDevice);
    }

    private static string GetCoordinateSourceText(InspectionDevice? detail)
    {
        return string.Equals(detail?.CoordinateSource, "platform", StringComparison.OrdinalIgnoreCase)
            ? "平台"
            : "无";
    }

    private static string BuildMapCoordinateText(CoordinateProjectionResult? projection)
    {
        return projection?.HasMapCoordinate == true
            ? $"{projection.MapLatitude} / {projection.MapLongitude}"
            : "无";
    }

    private static string BuildCoordinateStatusText(InspectionDevice? detail, CoordinateProjectionResult? projection)
    {
        if (projection is not null && !string.IsNullOrWhiteSpace(projection.CoordinateStateText))
        {
            return projection.CoordinateStateText;
        }

        return detail?.CoordinateStatus switch
        {
            "available" => "已获取平台原始坐标",
            "lookup_failed" => "平台坐标读取失败，需人工确认",
            _ => "平台未提供坐标"
        };
    }

    private static string BuildCoordinateRemarkText(InspectionDevice? detail, CoordinateProjectionResult? projection)
    {
        if (projection is not null && !string.IsNullOrWhiteSpace(projection.CoordinateWarning))
        {
            return projection.CoordinateWarning;
        }

        return !string.IsNullOrWhiteSpace(detail?.CoordinateStatusMessage)
            ? detail.CoordinateStatusMessage
            : "平台未提供坐标，当前不进入上图。";
    }

    private void ApplyDirectorySummary(PreviewDeviceLoadResult result)
    {
        DirectoryGroupCount = result.DirectoryGroups.Count;
        DirectoryDeviceCount = result.Devices.Count;
        DirectorySnapshotGroupCount = result.SnapshotGroupCount;
        DirectorySnapshotDeviceCount = result.SnapshotDeviceCount;
        DirectoryPlatformGroupCount = result.Metadata.PlatformGroupCount;
        DirectoryPlatformDeviceCount = result.Metadata.PlatformDeviceCount;
        DirectoryReconciliationCompleted = result.Metadata.ReconciliationCompleted;
        DirectoryReconciliationMatched = result.Metadata.ReconciliationMatched;
        DirectoryReconciledRegionCount = result.Metadata.ReconciledRegionCount;
        DirectoryReconciledDeviceCount = result.Metadata.ReconciledDeviceCount;
        DirectoryReconciledOnlineCount = result.Metadata.ReconciledOnlineCount;
        DirectoryReconciliationScopeText = result.Metadata.ReconciliationScopeText;
        OnPropertyChanged(nameof(DirectoryCountsMatch));
        OnPropertyChanged(nameof(DirectoryPlatformSummaryText));
        OnPropertyChanged(nameof(DirectoryVerificationText));
    }

    private static string BuildDirectoryStatusText(
        int groupCount,
        int deviceCount,
        int snapshotGroupCount,
        int snapshotDeviceCount,
        GroupSyncSnapshotMetadata metadata,
        DateTimeOffset? lastSyncedAt)
    {
        var summary = $"当前展示的是真实监控目录树与目录层级设备：UI 目录 {groupCount}/{snapshotGroupCount}，UI 设备 {deviceCount}/{snapshotDeviceCount}";
        summary += $"；平台拉回目录 {metadata.PlatformGroupCount}，设备 {metadata.PlatformDeviceCount}";

        if (lastSyncedAt is null)
        {
            return $"{summary}。";
        }

        return $"{summary}；最近同步 {lastSyncedAt:yyyy-MM-dd HH:mm:ss}。";
    }
}
