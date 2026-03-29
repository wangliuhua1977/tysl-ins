using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class DeviceCoordSvc(
    IOpenPlatformClient openPlatformClient,
    IGroupSyncStore groupSyncStore,
    ILogger<DeviceCoordSvc> logger) : IDeviceCoordinateService
{
    public async Task<IReadOnlyList<InspectionDevice>> RefreshPlatformCoordinatesAsync(
        IReadOnlyList<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return Array.Empty<InspectionDevice>();
        }

        var refreshed = new List<InspectionDevice>(devices.Count);
        foreach (var device in devices)
        {
            refreshed.Add(await RefreshSingleAsync(device, cancellationToken));
        }

        await groupSyncStore.UpdateDevicePlatformDataAsync(refreshed, cancellationToken);
        return refreshed;
    }

    public async Task<InspectionDevice?> RefreshPlatformCoordinatesAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var devices = await groupSyncStore.GetDevicesAsync(cancellationToken);
        var device = devices.FirstOrDefault(item => string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            return null;
        }

        var refreshed = await RefreshSingleAsync(device, cancellationToken);
        await groupSyncStore.UpdateDevicePlatformDataAsync([refreshed], cancellationToken);
        return refreshed;
    }

    private async Task<InspectionDevice> RefreshSingleAsync(InspectionDevice device, CancellationToken cancellationToken)
    {
        logger.LogInformation("getDeviceInfoByDeviceCode started for {DeviceCode}.", device.DeviceCode);

        var result = await openPlatformClient.GetDeviceInfoByDeviceCodeAsync(device.DeviceCode, cancellationToken);
        if (!result.Success || result.Payload is null)
        {
            logger.LogWarning(
                "getDeviceInfoByDeviceCode failed for {DeviceCode}. Message={Message}.",
                device.DeviceCode,
                result.BuildMessage());

            return BuildLookupFailedDevice(device, result.BuildMessage());
        }

        var refreshed = BuildPlatformDevice(device, result.Payload);
        logger.LogInformation(
            "getDeviceInfoByDeviceCode completed for {DeviceCode}. CoordinateSource={CoordinateSource}, CoordinateStatus={CoordinateStatus}.",
            device.DeviceCode,
            refreshed.CoordinateSource,
            refreshed.CoordinateStatus);
        logger.LogInformation(
            "Device raw coordinate resolved for {DeviceCode}. RawLatitude={RawLatitude}, RawLongitude={RawLongitude}.",
            device.DeviceCode,
            refreshed.RawLatitude ?? "null",
            refreshed.RawLongitude ?? "null");

        return refreshed;
    }

    private static InspectionDevice BuildPlatformDevice(InspectionDevice device, OpenPlatformDeviceInfoPayload payload)
    {
        var latitude = NormalizeText(payload.Latitude);
        var longitude = NormalizeText(payload.Longitude);
        var location = NormalizeText(payload.Location) ?? device.Location;
        var hasCoordinate = !string.IsNullOrWhiteSpace(latitude) && !string.IsNullOrWhiteSpace(longitude);

        return device with
        {
            DeviceName = string.IsNullOrWhiteSpace(payload.DeviceName) ? device.DeviceName : payload.DeviceName.Trim(),
            Latitude = hasCoordinate ? latitude : null,
            Longitude = hasCoordinate ? longitude : null,
            Location = location,
            CoordinateSource = hasCoordinate ? "platform" : "none",
            CoordinateStatus = hasCoordinate ? "available" : "missing",
            CoordinateStatusMessage = hasCoordinate
                ? "平台原始坐标来自 getDeviceInfoByDeviceCode。"
                : "平台未提供坐标。"
        };
    }

    private static InspectionDevice BuildLookupFailedDevice(InspectionDevice device, string message)
    {
        if (!string.IsNullOrWhiteSpace(device.CoordinateStatus))
        {
            return device;
        }

        return device with
        {
            CoordinateSource = "none",
            CoordinateStatus = "lookup_failed",
            CoordinateStatusMessage = $"平台坐标读取失败，需人工确认：{message}"
        };
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
