using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class MapPageViewModel(
    IMapService mapService,
    MapOptions mapOptions,
    ILogger<MapPageViewModel> logger) : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private bool hasLoaded;

    [ObservableProperty]
    private string statusText = "正在加载本地点位...";

    [ObservableProperty]
    private int renderedCount;

    [ObservableProperty]
    private int onlineCount;

    [ObservableProperty]
    private int offlineCount;

    [ObservableProperty]
    private int unlocatedCount;

    [ObservableProperty]
    private string mapBootstrapJson = string.Empty;

    public async Task InitializeAsync()
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        logger.LogInformation("Loading local map points from SQLite.");

        var result = await mapService.LoadAsync(CancellationToken.None);
        Apply(result);
    }

    public void ReportWebViewFailure(Exception exception)
    {
        logger.LogError(exception, "WebView2 initialization failed for map page.");
        StatusText = $"地图页初始化失败：{exception.Message}";
        MapBootstrapJson = BuildBootstrapJson(Array.Empty<InspectionDevice>(), StatusText, false, false);
    }

    private void Apply(MapLoadResult result)
    {
        var devices = result.Devices;
        RenderedCount = devices.Count(IsRenderable);
        OnlineCount = devices.Count(device => device.OnlineStatus == 1);
        OfflineCount = devices.Count(device => device.OnlineStatus == 0);
        UnlocatedCount = devices.Count(device => !IsRenderable(device));

        var hasMapKey = mapOptions.HasJsKey();
        StatusText = result.Success
            ? hasMapKey
                ? $"已读取 {devices.Count} 个本地点位，地图已准备就绪。"
                : $"已读取 {devices.Count} 个本地点位，但缺少高德地图配置。"
            : result.Message;

        MapBootstrapJson = BuildBootstrapJson(devices, StatusText, hasMapKey, result.Success);
    }

    private string BuildBootstrapJson(
        IReadOnlyList<InspectionDevice> devices,
        string statusText,
        bool hasMapKey,
        bool shouldLoadMap)
    {
        return JsonSerializer.Serialize(
            new
            {
                type = "bootstrap",
                statusText,
                map = new
                {
                    jsKey = mapOptions.JsKey,
                    securityJsCode = mapOptions.SecurityJsCode,
                    jsApiVersion = string.IsNullOrWhiteSpace(mapOptions.JsApiVersion) ? "2.0" : mapOptions.JsApiVersion,
                    coordinateSystem = mapOptions.CoordinateSystem,
                    hasMapKey,
                    shouldLoadMap
                },
                stats = new
                {
                    renderedCount = RenderedCount,
                    onlineCount = OnlineCount,
                    offlineCount = OfflineCount,
                    unlocatedCount = UnlocatedCount
                },
                points = devices.Select(ProjectDevice).ToArray()
            },
            JsonOptions);
    }

    private static object ProjectDevice(InspectionDevice device)
    {
        return new
        {
            deviceCode = device.DeviceCode,
            deviceName = device.DeviceName,
            groupId = device.GroupId,
            location = device.Location ?? string.Empty,
            latitude = device.Latitude ?? string.Empty,
            longitude = device.Longitude ?? string.Empty,
            onlineStatus = device.OnlineStatus,
            cloudStatus = device.CloudStatus,
            bandStatus = device.BandStatus,
            sourceTypeFlag = device.SourceTypeFlag,
            syncedAt = device.SyncedAt,
            hasCoordinate = IsRenderable(device)
        };
    }

    private static bool IsRenderable(InspectionDevice device)
    {
        return TryReadCoordinate(device.Latitude, out var latitude)
            && TryReadCoordinate(device.Longitude, out var longitude)
            && latitude is >= -90 and <= 90
            && longitude is >= -180 and <= 180;
    }

    private static bool TryReadCoordinate(string? value, out double coordinate)
    {
        return double.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out coordinate);
    }
}
