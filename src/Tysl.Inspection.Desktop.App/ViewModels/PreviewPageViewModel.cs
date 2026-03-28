using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class PreviewPageViewModel(
    IPreviewService previewService,
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
            : "本地 SQLite 中暂无点位数据，请先完成同步。";

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

    private bool CanRequestPreview()
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
    }

    partial void OnIsBusyChanged(bool value)
    {
        RequestPreviewCommand.NotifyCanExecuteChanged();
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
    }
}
