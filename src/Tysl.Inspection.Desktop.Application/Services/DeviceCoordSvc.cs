using System.Collections.Concurrent;
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
    private static readonly TimeSpan CoordinateLookupInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan RateLimitRetryWindow = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Lazy<Task<InspectionDevice>>> inFlightLookups =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim lookupGate = new(1, 1);
    private DateTimeOffset lastLookupStartedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<InspectionDevice>> RefreshPlatformCoordinatesAsync(
        IReadOnlyList<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            return Array.Empty<InspectionDevice>();
        }

        var uniqueDevices = new Dictionary<string, InspectionDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            uniqueDevices.TryAdd(device.DeviceCode, device);
        }

        var resolvedByCode = new Dictionary<string, InspectionDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in uniqueDevices.Values)
        {
            resolvedByCode[device.DeviceCode] = await ResolveCachedOrRefreshAsync(device, cancellationToken);
        }

        var changedDevices = uniqueDevices.Values
            .Select(device => (Original: device, Resolved: resolvedByCode[device.DeviceCode]))
            .Where(pair => HasDeviceChanged(pair.Original, pair.Resolved))
            .Select(pair => pair.Resolved)
            .ToArray();

        await PersistResolvedDevicesAsync(changedDevices, cancellationToken);

        return devices
            .Select(device => resolvedByCode[device.DeviceCode])
            .ToArray();
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

        var resolved = await ResolveCachedOrRefreshAsync(device, cancellationToken);
        if (HasDeviceChanged(device, resolved))
        {
            await PersistResolvedDevicesAsync([resolved], cancellationToken);
        }

        return resolved;
    }

    private async Task<InspectionDevice> ResolveCachedOrRefreshAsync(InspectionDevice device, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (ShouldUseCachedDevice(device, now, out var cacheReason))
        {
            logger.LogInformation(
                "Coordinate cache hit for {DeviceCode}. Status={CoordinateStatus}, Reason={Reason}.",
                device.DeviceCode,
                string.IsNullOrWhiteSpace(device.CoordinateStatus) ? "none" : device.CoordinateStatus,
                cacheReason);
            return device;
        }

        logger.LogInformation(
            "Coordinate cache miss for {DeviceCode}. Status={CoordinateStatus}.",
            device.DeviceCode,
            string.IsNullOrWhiteSpace(device.CoordinateStatus) ? "none" : device.CoordinateStatus);

        var created = false;
        var lazyLookup = inFlightLookups.GetOrAdd(
            device.DeviceCode,
            _ =>
            {
                created = true;
                return new Lazy<Task<InspectionDevice>>(
                    () => RefreshFromPlatformAsync(device),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            });

        if (!created)
        {
            logger.LogInformation("Coordinate request dedupe hit for {DeviceCode}.", device.DeviceCode);
        }

        try
        {
            return await lazyLookup.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            inFlightLookups.TryRemove(new KeyValuePair<string, Lazy<Task<InspectionDevice>>>(device.DeviceCode, lazyLookup));
        }
    }

    private async Task<InspectionDevice> RefreshFromPlatformAsync(InspectionDevice device)
    {
        var resolvedAt = DateTimeOffset.UtcNow;
        logger.LogInformation("Coordinate supplement started for {DeviceCode}.", device.DeviceCode);

        await lookupGate.WaitAsync();
        try
        {
            var delay = lastLookupStartedAt == DateTimeOffset.MinValue
                ? TimeSpan.Zero
                : CoordinateLookupInterval - (DateTimeOffset.UtcNow - lastLookupStartedAt);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            lastLookupStartedAt = DateTimeOffset.UtcNow;
            var result = await openPlatformClient.GetDeviceInfoByDeviceCodeAsync(device.DeviceCode, CancellationToken.None);

            if (!result.Success || result.Payload is null)
            {
                if (IsRateLimited(result))
                {
                    logger.LogWarning(
                        "Coordinate rate limit hit for {DeviceCode}. PlatformCode={PlatformCode}, Message={Message}.",
                        device.DeviceCode,
                        result.PlatformCode ?? string.Empty,
                        result.BuildMessage());

                    var rateLimited = BuildRateLimitedDevice(device, result.BuildMessage(), resolvedAt);
                    LogCoordinateClassification(rateLimited);
                    logger.LogInformation("Coordinate supplement completed for {DeviceCode}.", device.DeviceCode);
                    return rateLimited;
                }

                logger.LogWarning(
                    "Coordinate supplement failed for {DeviceCode}. Message={Message}.",
                    device.DeviceCode,
                    result.BuildMessage());

                var failed = BuildLookupFailedDevice(device, result.BuildMessage(), resolvedAt);
                LogCoordinateClassification(failed);
                logger.LogInformation("Coordinate supplement completed for {DeviceCode}.", device.DeviceCode);
                return failed;
            }

            var refreshed = BuildPlatformDevice(device, result.Payload, resolvedAt);
            LogCoordinateClassification(refreshed);
            logger.LogInformation("Coordinate supplement completed for {DeviceCode}.", device.DeviceCode);
            return refreshed;
        }
        finally
        {
            lookupGate.Release();
        }
    }

    private async Task PersistResolvedDevicesAsync(
        IReadOnlyCollection<InspectionDevice> devices,
        CancellationToken cancellationToken)
    {
        if (devices.Count == 0)
        {
            logger.LogInformation("Coordinate cache write skipped because no device cache changed.");
            return;
        }

        await groupSyncStore.UpdateDevicePlatformDataAsync(devices, cancellationToken);
        logger.LogInformation("Coordinate cache write completed. DeviceCount={DeviceCount}.", devices.Count);
    }

    private static bool ShouldUseCachedDevice(InspectionDevice device, DateTimeOffset now, out string reason)
    {
        if (device.HasRawCoordinate)
        {
            reason = "raw_coordinate";
            return true;
        }

        if (string.Equals(device.CoordinateStatus, CoordinateStateCatalog.Missing, StringComparison.OrdinalIgnoreCase))
        {
            reason = "cached_missing";
            return true;
        }

        if (string.Equals(device.CoordinateStatus, CoordinateStateCatalog.Failed, StringComparison.OrdinalIgnoreCase))
        {
            reason = "cached_failed";
            return true;
        }

        if (string.Equals(device.CoordinateStatus, CoordinateStateCatalog.RateLimited, StringComparison.OrdinalIgnoreCase)
            && device.SyncedAt.Add(RateLimitRetryWindow) > now)
        {
            reason = "cached_rate_limited";
            return true;
        }

        reason = "refresh_required";
        return false;
    }

    private static bool IsRateLimited(OpenPlatformCallResult<OpenPlatformDeviceInfoPayload> result)
    {
        return string.Equals(result.PlatformCode, "30041", StringComparison.OrdinalIgnoreCase)
               || result.BuildMessage().Contains("30041", StringComparison.OrdinalIgnoreCase);
    }

    private static InspectionDevice BuildPlatformDevice(
        InspectionDevice device,
        OpenPlatformDeviceInfoPayload payload,
        DateTimeOffset resolvedAt)
    {
        var latitude = NormalizeText(payload.Latitude);
        var longitude = NormalizeText(payload.Longitude);
        var location = NormalizeText(payload.Location) ?? device.Location;
        var hasCoordinate = !string.IsNullOrWhiteSpace(latitude) && !string.IsNullOrWhiteSpace(longitude);
        var canReuseMapCache = hasCoordinate
            && device.HasCachedMapCoordinate
            && string.Equals(device.RawLatitude, latitude, StringComparison.Ordinal)
            && string.Equals(device.RawLongitude, longitude, StringComparison.Ordinal);

        return device with
        {
            DeviceName = string.IsNullOrWhiteSpace(payload.DeviceName) ? device.DeviceName : payload.DeviceName.Trim(),
            Latitude = hasCoordinate ? latitude : null,
            Longitude = hasCoordinate ? longitude : null,
            MapLatitude = canReuseMapCache ? device.MapLatitude : null,
            MapLongitude = canReuseMapCache ? device.MapLongitude : null,
            Location = location,
            CoordinateSource = hasCoordinate ? "platform" : "none",
            CoordinateStatus = hasCoordinate ? CoordinateStateCatalog.Available : CoordinateStateCatalog.Missing,
            CoordinateStatusMessage = hasCoordinate
                ? "平台原始坐标来自 getDeviceInfoByDeviceCode。"
                : "平台未提供坐标。",
            SyncedAt = resolvedAt
        };
    }

    private static InspectionDevice BuildRateLimitedDevice(
        InspectionDevice device,
        string message,
        DateTimeOffset resolvedAt)
    {
        if (device.HasRawCoordinate)
        {
            return device;
        }

        var statusMessage = string.IsNullOrWhiteSpace(message)
            ? "坐标获取限频，稍后重试。"
            : $"坐标获取限频，稍后重试：{message}";

        return device with
        {
            MapLatitude = null,
            MapLongitude = null,
            CoordinateSource = "none",
            CoordinateStatus = CoordinateStateCatalog.RateLimited,
            CoordinateStatusMessage = statusMessage,
            SyncedAt = resolvedAt
        };
    }

    private static InspectionDevice BuildLookupFailedDevice(
        InspectionDevice device,
        string message,
        DateTimeOffset resolvedAt)
    {
        if (device.HasRawCoordinate)
        {
            return device;
        }

        var statusMessage = string.IsNullOrWhiteSpace(message)
            ? "坐标获取失败，需人工确认。"
            : $"坐标获取失败，需人工确认：{message}";

        return device with
        {
            MapLatitude = null,
            MapLongitude = null,
            CoordinateSource = "none",
            CoordinateStatus = CoordinateStateCatalog.Failed,
            CoordinateStatusMessage = statusMessage,
            SyncedAt = resolvedAt
        };
    }

    private void LogCoordinateClassification(InspectionDevice device)
    {
        logger.LogInformation(
            "Coordinate state classified for {DeviceCode}. Status={CoordinateStatus}, RawLatitude={RawLatitude}, RawLongitude={RawLongitude}, HasMapCache={HasMapCache}.",
            device.DeviceCode,
            string.IsNullOrWhiteSpace(device.CoordinateStatus) ? "none" : device.CoordinateStatus,
            device.RawLatitude ?? "null",
            device.RawLongitude ?? "null",
            device.HasCachedMapCoordinate);
    }

    private static bool HasDeviceChanged(InspectionDevice original, InspectionDevice resolved)
    {
        return !string.Equals(original.DeviceName, resolved.DeviceName, StringComparison.Ordinal)
               || !string.Equals(original.RawLatitude, resolved.RawLatitude, StringComparison.Ordinal)
               || !string.Equals(original.RawLongitude, resolved.RawLongitude, StringComparison.Ordinal)
               || !string.Equals(original.MapLatitude, resolved.MapLatitude, StringComparison.Ordinal)
               || !string.Equals(original.MapLongitude, resolved.MapLongitude, StringComparison.Ordinal)
               || !string.Equals(original.Location, resolved.Location, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateSource, resolved.CoordinateSource, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateStatus, resolved.CoordinateStatus, StringComparison.Ordinal)
               || !string.Equals(original.CoordinateStatusMessage, resolved.CoordinateStatusMessage, StringComparison.Ordinal)
               || original.SyncedAt != resolved.SyncedAt;
    }

    private static string? NormalizeText(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
