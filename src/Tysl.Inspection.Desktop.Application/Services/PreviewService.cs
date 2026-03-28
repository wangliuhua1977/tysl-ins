using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class PreviewService(
    IMapStore mapStore,
    IOpenPlatformClient openPlatformClient,
    ILogger<PreviewService> logger) : IPreviewService
{
    public async Task<PreviewDeviceLoadResult> LoadLocalDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await mapStore.GetDevicesAsync(cancellationToken);
            var payload = devices
                .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceCode, StringComparer.OrdinalIgnoreCase)
                .Select(device => new PreviewDeviceOption(device.DeviceCode, device.DeviceName, device.OnlineStatus))
                .ToArray();

            logger.LogInformation("Loaded {DeviceCount} local devices for single preview page.", payload.Length);

            return new PreviewDeviceLoadResult(true, string.Empty, payload);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load local devices for single preview page.");
            return new PreviewDeviceLoadResult(false, BuildSqliteMessage(exception), Array.Empty<PreviewDeviceOption>());
        }
    }

    public async Task<PreviewPrepareResult> PrepareAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.Now;
        logger.LogInformation("Starting single-device preview preparation for {DeviceCode}.", deviceCode);

        try
        {
            var devices = await mapStore.GetDevicesAsync(cancellationToken);
            var device = devices.FirstOrDefault(item => string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                logger.LogWarning("Preview preparation aborted because local device {DeviceCode} was not found.", deviceCode);
                return BuildResult(
                    success: false,
                    deviceCode,
                    "未知设备",
                    "本地点位不存在，请先完成同步",
                    "未发起预览地址获取",
                    null,
                    "平台未返回有效期",
                    requestedAt);
            }

            var statusResult = await openPlatformClient.GetDeviceStatusAsync(device.DeviceCode, cancellationToken);
            if (!statusResult.Success || statusResult.Payload is null)
            {
                var message = BuildStatusFailureMessage(statusResult.BuildMessage());
                logger.LogWarning(
                    "Single-device preview status query failed for {DeviceCode}. Message: {Message}",
                    device.DeviceCode,
                    message);

                return BuildResult(
                    success: false,
                    device.DeviceCode,
                    device.DeviceName,
                    message,
                    "未发起预览地址获取",
                    null,
                    "平台未返回有效期",
                    requestedAt);
            }

            var diagnosisText = BuildDiagnosisText(statusResult.Payload.OnlineStatus);
            if (statusResult.Payload.OnlineStatus is not 1)
            {
                logger.LogInformation(
                    "Single-device preview stopped after status diagnosis for {DeviceCode}. OnlineStatus={OnlineStatus}.",
                    device.DeviceCode,
                    statusResult.Payload.OnlineStatus);

                return BuildResult(
                    success: false,
                    device.DeviceCode,
                    device.DeviceName,
                    diagnosisText,
                    "未发起预览地址获取",
                    null,
                    "平台未返回有效期",
                    requestedAt);
            }

            var previewResult = await openPlatformClient.GetDevicePreviewUrlAsync(device.DeviceCode, cancellationToken);
            if (!previewResult.Success || previewResult.Payload is null || string.IsNullOrWhiteSpace(previewResult.Payload.Url))
            {
                var message = BuildPreviewFailureMessage(previewResult.BuildMessage());
                logger.LogWarning(
                    "Single-device preview url query failed for {DeviceCode}. Message: {Message}",
                    device.DeviceCode,
                    message);

                return BuildResult(
                    success: false,
                    device.DeviceCode,
                    device.DeviceName,
                    diagnosisText,
                    message,
                    null,
                    "平台未返回有效期",
                    requestedAt);
            }

            var expireText = string.IsNullOrWhiteSpace(previewResult.Payload.ExpireTime)
                ? "平台未返回有效期"
                : $"平台返回：{previewResult.Payload.ExpireTime}";

            logger.LogInformation(
                "Single-device preview url is ready for {DeviceCode}. ExpireText={ExpireText}",
                device.DeviceCode,
                expireText);

            return BuildResult(
                success: true,
                device.DeviceCode,
                device.DeviceName,
                diagnosisText,
                "预览地址已就绪",
                previewResult.Payload.Url,
                expireText,
                requestedAt);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected single-device preview failure for {DeviceCode}.", deviceCode);
            return BuildResult(
                success: false,
                deviceCode,
                "未知设备",
                $"单点预览准备失败：{exception.Message}",
                "未发起预览地址获取",
                null,
                "平台未返回有效期",
                requestedAt);
        }
    }

    private static PreviewPrepareResult BuildResult(
        bool success,
        string deviceCode,
        string deviceName,
        string diagnosisText,
        string addressStatusText,
        string? rtspUrl,
        string expireText,
        DateTimeOffset requestedAt)
    {
        return new PreviewPrepareResult(
            success,
            deviceCode,
            deviceName,
            diagnosisText,
            addressStatusText,
            rtspUrl,
            expireText,
            requestedAt);
    }

    private static string BuildDiagnosisText(int? onlineStatus)
    {
        return onlineStatus switch
        {
            1 => "在线：可请求预览地址",
            0 => "设备离线，无法获取预览地址",
            2 => "设备休眠，当前不进入预览",
            3 => "设备休眠，当前不进入预览",
            _ => "设备状态未知，当前不进入预览"
        };
    }

    private static string BuildStatusFailureMessage(string message)
    {
        if (message.Contains("accessToken", StringComparison.OrdinalIgnoreCase))
        {
            return $"accessToken 获取失败：{message}";
        }

        return $"设备状态查询失败：{message}";
    }

    private static string BuildPreviewFailureMessage(string message)
    {
        if (message.Contains("accessToken", StringComparison.OrdinalIgnoreCase))
        {
            return $"accessToken 获取失败：{message}";
        }

        if (message.StartsWith("RTSP ", StringComparison.Ordinal))
        {
            return message;
        }

        return $"获取预览地址失败：{message}";
    }

    private static string BuildSqliteMessage(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("unable to open database file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot open", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 SQLite 文件不存在或无法打开，请先检查数据库路径。";
        }

        if (message.Contains("no such table", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Device", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 SQLite 中缺少 Device 表，请先完成初始化或同步。";
        }

        return $"本地点位读取失败：{message}";
    }
}
