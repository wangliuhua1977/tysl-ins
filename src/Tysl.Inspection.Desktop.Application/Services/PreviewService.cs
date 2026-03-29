using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class PreviewService(
    IGroupSyncStore groupSyncStore,
    IOpenPlatformClient openPlatformClient,
    IPlayProbe playProbe,
    ILogger<PreviewService> logger) : IPreviewService
{
    public async Task<PreviewDeviceLoadResult> LoadLocalDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var groups = await groupSyncStore.GetGroupsAsync(cancellationToken);
            var devices = await groupSyncStore.GetDevicesAsync(cancellationToken);
            var snapshot = await groupSyncStore.GetLocalSyncSnapshotAsync(cancellationToken);
            var devicesByGroup = devices.ToLookup(device => device.GroupId, StringComparer.OrdinalIgnoreCase);

            var payload = devices
                .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.DeviceCode, StringComparer.OrdinalIgnoreCase)
                .Select(device => new PreviewDeviceOption(device.DeviceCode, device.DeviceName, device.OnlineStatus))
                .ToArray();

            var directoryGroups = groups
                .OrderBy(group => group.GroupName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                .Select(group => new PreviewDirectoryGroupItem(
                    group.GroupId,
                    group.GroupName,
                    group.DeviceCount,
                    devicesByGroup[group.GroupId]
                        .OrderBy(device => device.DeviceName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(device => device.DeviceCode, StringComparer.OrdinalIgnoreCase)
                        .Select(device => new PreviewDirectoryDeviceItem(device.DeviceCode, device.DeviceName, device.OnlineStatus))
                        .ToArray()))
                .ToArray();

            var reportedDeviceCount = groups.Sum(group => Math.Max(group.DeviceCount, 0));
            var emptyGroupCount = directoryGroups.Count(group => group.LoadedDeviceCount == 0);
            var mismatchedGroups = directoryGroups
                .Where(group => !group.CountMatches)
                .Select(group => $"{group.GroupName}({group.GroupId}) 本地{group.LoadedDeviceCount}/平台{group.ReportedDeviceCount}")
                .ToArray();

            logger.LogInformation(
                "Loaded full real device directory for preview page. SnapshotGroups={SnapshotGroupCount}, SnapshotDevices={SnapshotDeviceCount}, BoundGroups={BoundGroupCount}, BoundDevices={BoundDeviceCount}, ReportedDevices={ReportedDeviceCount}, EmptyGroups={EmptyGroupCount}, LastSyncedAt={LastSyncedAt}.",
                snapshot.GroupCount,
                snapshot.DeviceCount,
                directoryGroups.Length,
                payload.Length,
                reportedDeviceCount,
                emptyGroupCount,
                snapshot.LastSyncedAt?.ToString("O") ?? "null");

            if (snapshot.GroupCount != directoryGroups.Length || snapshot.DeviceCount != payload.Length)
            {
                logger.LogWarning(
                    "Preview directory binding count mismatch. SnapshotGroups={SnapshotGroupCount}, BoundGroups={BoundGroupCount}, SnapshotDevices={SnapshotDeviceCount}, BoundDevices={BoundDeviceCount}.",
                    snapshot.GroupCount,
                    directoryGroups.Length,
                    snapshot.DeviceCount,
                    payload.Length);
            }

            if (mismatchedGroups.Length > 0)
            {
                logger.LogWarning(
                    "Preview directory group count mismatch detected. Groups={Groups}.",
                    string.Join(" | ", mismatchedGroups));
            }

            return new PreviewDeviceLoadResult(
                true,
                string.Empty,
                payload,
                directoryGroups,
                snapshot.GroupCount,
                snapshot.DeviceCount,
                reportedDeviceCount,
                snapshot.LastSyncedAt);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load real device directory for preview page.");
            return new PreviewDeviceLoadResult(
                false,
                BuildSqliteMessage(exception),
                Array.Empty<PreviewDeviceOption>(),
                Array.Empty<PreviewDirectoryGroupItem>(),
                0,
                0,
                0,
                null);
        }
    }

    public async Task<PreviewPrepareResult> PrepareAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var requestedAt = DateTimeOffset.Now;
        logger.LogInformation("Starting single-device preview preparation for {DeviceCode}.", deviceCode);

        try
        {
            var flow = await PrepareCoreAsync(deviceCode, requestedAt, cancellationToken);

            if (!flow.DeviceFound)
            {
                logger.LogWarning("Preview preparation aborted because local device {DeviceCode} was not found.", deviceCode);
            }
            else if (!flow.StatusResolved)
            {
                logger.LogWarning(
                    "Single-device preview status query failed for {DeviceCode}. Message: {Message}",
                    flow.DeviceCode,
                    flow.DiagnosisText);
            }
            else if (flow.OnlineStatus is not 1)
            {
                logger.LogInformation(
                    "Single-device preview stopped after status diagnosis for {DeviceCode}. OnlineStatus={OnlineStatus}.",
                    flow.DeviceCode,
                    flow.OnlineStatus);
            }
            else if (!flow.RtspReady)
            {
                logger.LogWarning(
                    "Single-device preview url query failed for {DeviceCode}. Message: {Message}",
                    flow.DeviceCode,
                    flow.AddressStatusText);
            }
            else
            {
                logger.LogInformation(
                    "Single-device preview url is ready for {DeviceCode}. ExpireText={ExpireText}",
                    flow.DeviceCode,
                    flow.ExpireText);
            }

            return BuildResult(
                flow.RtspReady,
                flow.DeviceCode,
                flow.DeviceName,
                flow.DiagnosisText,
                flow.AddressStatusText,
                flow.RtspUrl,
                flow.ExpireText,
                flow.RequestedAt);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected single-device preview failure for {DeviceCode}.", deviceCode);
            return BuildResult(
                false,
                deviceCode,
                "未知设备",
                $"单点预览准备失败：{exception.Message}",
                "未发起预览地址获取",
                null,
                "平台未返回有效期",
                requestedAt);
        }
    }

    public async Task<InspectResult> InspectAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var inspectAt = DateTimeOffset.Now;
        logger.LogInformation("Starting single-device inspect for {DeviceCode}.", deviceCode);

        try
        {
            var flow = await PrepareCoreAsync(deviceCode, inspectAt, cancellationToken);
            logger.LogInformation(
                "Inspect online status result for {DeviceCode}. StatusResolved={StatusResolved}, OnlineStatus={OnlineStatus}.",
                flow.DeviceCode,
                flow.StatusResolved,
                flow.OnlineStatus);
            logger.LogInformation(
                "Inspect RTSP result for {DeviceCode}. RtspReady={RtspReady}, AddressStatus={AddressStatus}.",
                flow.DeviceCode,
                flow.RtspReady,
                flow.AddressStatusText);

            InspectResult result;
            if (!flow.DeviceFound)
            {
                result = BuildInspectResult(
                    flow,
                    false,
                    "未找到本地点位",
                    false,
                    false,
                    false,
                    "巡检失败：本地点位不存在",
                    "本地点位不存在",
                    flow.DiagnosisText,
                    InspectAbnormalClass.None);
            }
            else if (!flow.StatusResolved)
            {
                result = BuildInspectResult(
                    flow,
                    false,
                    "未获取",
                    false,
                    false,
                    false,
                    "巡检失败：设备状态未获取",
                    ExtractStatusFailureCategory(flow.DiagnosisText),
                    flow.DiagnosisText,
                    InspectAbnormalClass.None);
            }
            else if (flow.OnlineStatus is not 1)
            {
                result = BuildInspectResult(
                    flow,
                    true,
                    BuildOnlineStatusText(flow.OnlineStatus),
                    false,
                    false,
                    false,
                    BuildNonPlayingConclusion(flow.OnlineStatus),
                    BuildNonPlayingFailureCategory(flow.OnlineStatus),
                    flow.DiagnosisText,
                    InspectAbnormalClass.Offline);
            }
            else if (!flow.RtspReady || string.IsNullOrWhiteSpace(flow.RtspUrl))
            {
                result = BuildInspectResult(
                    flow,
                    true,
                    BuildOnlineStatusText(flow.OnlineStatus),
                    false,
                    false,
                    false,
                    "巡检失败：RTSP 未就绪",
                    ExtractRtspFailureCategory(flow.AddressStatusText),
                    flow.AddressStatusText,
                    InspectAbnormalClass.RtspNotReady);
            }
            else
            {
                logger.LogInformation(
                    "Inspect playback stage started for {DeviceCode}. Rtsp={RtspUrl}",
                    flow.DeviceCode,
                    MaskUrl(flow.RtspUrl));

                var probeResult = await playProbe.ProbeAsync(
                    new PlayProbeArgs(flow.DeviceName, flow.DeviceCode, flow.RtspUrl!),
                    cancellationToken);
                result = BuildPlaybackInspectResult(flow, probeResult);

                if (result.EnteredPlaying)
                {
                    logger.LogInformation(
                        "Inspect playback entered playing for {DeviceCode}. Conclusion={Conclusion}.",
                        flow.DeviceCode,
                        result.Conclusion);
                }
                else
                {
                    logger.LogWarning(
                        "Inspect playback failed for {DeviceCode}. FailureCategory={FailureCategory}.",
                        flow.DeviceCode,
                        result.FailureCategory);
                }
            }

            LogAbnormalClass(flow, result);
            logger.LogInformation(
                "Inspect completed for {DeviceCode}. Conclusion={Conclusion}, FailureCategory={FailureCategory}, AbnormalClass={AbnormalClass}.",
                result.DeviceCode,
                result.Conclusion,
                string.IsNullOrWhiteSpace(result.FailureCategory) ? "无" : result.FailureCategory,
                result.AbnormalClassText);

            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected single-device inspect failure for {DeviceCode}.", deviceCode);
            return new InspectResult(
                inspectAt,
                "未知设备",
                deviceCode,
                false,
                "未获取",
                false,
                false,
                false,
                "巡检失败：巡检执行异常",
                "巡检执行异常",
                $"最小巡检诊断失败：{exception.Message}",
                InspectAbnormalClass.None);
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

    private async Task<PreviewFlow> PrepareCoreAsync(
        string deviceCode,
        DateTimeOffset requestedAt,
        CancellationToken cancellationToken)
    {
        var devices = await groupSyncStore.GetDevicesAsync(cancellationToken);
        var device = devices.FirstOrDefault(item => string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            return new PreviewFlow(
                false,
                deviceCode,
                "未知设备",
                false,
                null,
                "本地点位不存在，请先完成同步",
                false,
                "未发起预览地址获取",
                null,
                "平台未返回有效期",
                requestedAt);
        }

        var statusResult = await openPlatformClient.GetDeviceStatusAsync(device.DeviceCode, cancellationToken);
        if (!statusResult.Success || statusResult.Payload is null)
        {
            return new PreviewFlow(
                true,
                device.DeviceCode,
                device.DeviceName,
                false,
                null,
                BuildStatusFailureMessage(statusResult.BuildMessage()),
                false,
                "未发起预览地址获取",
                null,
                "平台未返回有效期",
                requestedAt);
        }

        var diagnosisText = BuildDiagnosisText(statusResult.Payload.OnlineStatus);
        if (statusResult.Payload.OnlineStatus is not 1)
        {
            return new PreviewFlow(
                true,
                device.DeviceCode,
                device.DeviceName,
                true,
                statusResult.Payload.OnlineStatus,
                diagnosisText,
                false,
                "未发起预览地址获取",
                null,
                "平台未返回有效期",
                requestedAt);
        }

        var previewResult = await openPlatformClient.GetDevicePreviewUrlAsync(device.DeviceCode, cancellationToken);
        if (!previewResult.Success || previewResult.Payload is null || string.IsNullOrWhiteSpace(previewResult.Payload.Url))
        {
            return new PreviewFlow(
                true,
                device.DeviceCode,
                device.DeviceName,
                true,
                statusResult.Payload.OnlineStatus,
                diagnosisText,
                false,
                BuildPreviewFailureMessage(previewResult.BuildMessage()),
                null,
                "平台未返回有效期",
                requestedAt);
        }

        var expireText = string.IsNullOrWhiteSpace(previewResult.Payload.ExpireTime)
            ? "平台未返回有效期"
            : $"平台返回：{previewResult.Payload.ExpireTime}";

        return new PreviewFlow(
            true,
            device.DeviceCode,
            device.DeviceName,
            true,
            statusResult.Payload.OnlineStatus,
            diagnosisText,
            true,
            "预览地址已就绪",
            previewResult.Payload.Url,
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

    private static InspectResult BuildInspectResult(
        PreviewFlow flow,
        bool statusResolved,
        string onlineStatus,
        bool rtspReady,
        bool playbackStarted,
        bool enteredPlaying,
        string conclusion,
        string failureCategory,
        string detailMessage,
        InspectAbnormalClass abnormalClass)
    {
        return new InspectResult(
            flow.RequestedAt,
            flow.DeviceName,
            flow.DeviceCode,
            statusResolved,
            onlineStatus,
            rtspReady,
            playbackStarted,
            enteredPlaying,
            conclusion,
            failureCategory,
            detailMessage,
            abnormalClass);
    }

    private static InspectResult BuildPlaybackInspectResult(PreviewFlow flow, PlayProbeResult probeResult)
    {
        if (probeResult.EnteredPlaying)
        {
            return BuildInspectResult(
                flow,
                true,
                BuildOnlineStatusText(flow.OnlineStatus),
                true,
                probeResult.PlaybackStarted || probeResult.EnteredPlaying,
                true,
                "巡检通过",
                string.Empty,
                "播放器已进入 Playing 播放态；当前轮仅确认播放态，实际画面仍需人工复核。",
                InspectAbnormalClass.None);
        }

        var failureCategory = string.IsNullOrWhiteSpace(probeResult.FailureCategory)
            ? "播放建链失败"
            : probeResult.FailureCategory;

        return BuildInspectResult(
            flow,
            true,
            BuildOnlineStatusText(flow.OnlineStatus),
            true,
            probeResult.PlaybackStarted,
            false,
            failureCategory switch
            {
                "播放初始化失败" => "巡检失败：播放初始化失败",
                "播放建链失败" => "巡检失败：播放失败",
                "播放过程中断" => "巡检失败：播放失败",
                "地址可能失效" => "巡检失败：播放失败",
                _ => "巡检失败：播放失败"
            },
            failureCategory,
            probeResult.DetailMessage,
            InspectAbnormalClass.PlayFailed);
    }

    private void LogAbnormalClass(PreviewFlow flow, InspectResult result)
    {
        logger.LogInformation(
            "Inspect abnormal pre-class input for {DeviceCode}. StatusResolved={StatusResolved}, OnlineStatus={OnlineStatus}, RtspReady={RtspReady}, PlaybackStarted={PlaybackStarted}, EnteredPlaying={EnteredPlaying}, FailureCategory={FailureCategory}.",
            flow.DeviceCode,
            result.StatusResolved,
            result.OnlineStatus,
            result.RtspReady,
            result.PlaybackStarted,
            result.EnteredPlaying,
            string.IsNullOrWhiteSpace(result.FailureCategory) ? "无" : result.FailureCategory);

        logger.LogInformation(
            "Inspect abnormal pre-class result for {DeviceCode}. AbnormalClass={AbnormalClass}, IsAbnormal={IsAbnormal}.",
            result.DeviceCode,
            result.AbnormalClassText,
            result.IsAbnormal);
    }

    private static string BuildNonPlayingConclusion(int? onlineStatus)
    {
        return onlineStatus switch
        {
            0 => "巡检失败：设备离线",
            2 => "巡检失败：设备离线",
            3 => "巡检失败：设备离线",
            _ => "巡检失败：设备状态未知"
        };
    }

    private static string BuildNonPlayingFailureCategory(int? onlineStatus)
    {
        return onlineStatus switch
        {
            0 => "设备离线",
            2 => "设备休眠",
            3 => "设备休眠",
            _ => "设备状态未知"
        };
    }

    private static string ExtractStatusFailureCategory(string message)
    {
        return message.Contains("accessToken", StringComparison.OrdinalIgnoreCase)
            ? "accessToken 获取失败"
            : "状态查询失败";
    }

    private static string ExtractRtspFailureCategory(string message)
    {
        if (message.Contains("accessToken", StringComparison.OrdinalIgnoreCase))
        {
            return "accessToken 获取失败";
        }

        if (message.Contains("RTSP 响应解密失败", StringComparison.Ordinal))
        {
            return "RTSP 响应解密失败";
        }

        if (message.Contains("RTSP 解密后 JSON 解析失败", StringComparison.Ordinal))
        {
            return "RTSP 解密后 JSON 解析失败";
        }

        if (message.Contains("RTSP 返回缺少 url", StringComparison.Ordinal))
        {
            return "RTSP 返回缺少 url";
        }

        return "RTSP 接口业务失败";
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
            || message.Contains("Device", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Group", StringComparison.OrdinalIgnoreCase))
        {
            return "本地 SQLite 中缺少 Group / Device 表，请先完成初始化或同步。";
        }

        return $"本地点位读取失败：{message}";
    }

    private static string MaskUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= 12
            ? "****"
            : $"{value[..6]}****{value[^6..]}";
    }

    private sealed record PreviewFlow(
        bool DeviceFound,
        string DeviceCode,
        string DeviceName,
        bool StatusResolved,
        int? OnlineStatus,
        string DiagnosisText,
        bool RtspReady,
        string AddressStatusText,
        string? RtspUrl,
        string ExpireText,
        DateTimeOffset RequestedAt);
}
