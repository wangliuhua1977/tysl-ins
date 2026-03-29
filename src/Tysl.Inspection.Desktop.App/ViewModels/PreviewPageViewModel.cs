using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.App.Services;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class PreviewPageViewModel(
    IPreviewService previewService,
    IPlayWinSvc playWinSvc,
    ILogger<PreviewPageViewModel> logger) : ObservableObject
{
    private bool hasLoaded;

    public ObservableCollection<PreviewDeviceOption> Devices { get; } = [];

    [ObservableProperty]
    private string pageStatusText = "正在加载本地点位...";

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private PreviewDeviceOption? selectedDevice;

    [ObservableProperty]
    private string deviceName = "暂无";

    [ObservableProperty]
    private string deviceCode = "暂无";

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

        var currentCode = SelectedDevice?.DeviceCode;
        var result = await previewService.LoadLocalDevicesAsync(CancellationToken.None);
        Devices.Clear();

        foreach (var device in result.Devices)
        {
            Devices.Add(device);
        }

        if (!result.Success)
        {
            PageStatusText = result.Message;
            SelectedDevice = null;
            ResetPreviewResult();
            return;
        }

        PageStatusText = Devices.Count > 0
            ? $"已加载 {Devices.Count} 个本地点位，可直接发起单点预览准备。"
            : "本地 SQLite 中暂时无点位数据，请先完成同步。";

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

    partial void OnSelectedDeviceChanged(PreviewDeviceOption? value)
    {
        if (value is null)
        {
            ResetPreviewResult();
        }
        else
        {
            ApplyDevice(value);
        }

        RequestPreviewCommand.NotifyCanExecuteChanged();
        RequestInspectCommand.NotifyCanExecuteChanged();
        OpenPlayWinCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPlayWindowReady));
        OnPropertyChanged(nameof(PlayWindowHintText));
    }

    partial void OnIsBusyChanged(bool value)
    {
        RequestPreviewCommand.NotifyCanExecuteChanged();
        RequestInspectCommand.NotifyCanExecuteChanged();
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
}
