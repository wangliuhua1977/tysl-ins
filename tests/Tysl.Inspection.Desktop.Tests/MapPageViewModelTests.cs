using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.App.ViewModels;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class MapPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_BuildsBootstrapWithSeparatedRawAndRenderCoordinates()
    {
        var viewModel = new MapPageViewModel(
            new StubMapService(
                new MapLoadResult(
                    true,
                    string.Empty,
                    [
                        new InspectionDevice(
                            "dev-001",
                            "测试设备",
                            "group-001",
                            "31.2304",
                            "121.4737",
                            "上海",
                            1,
                            1,
                            1,
                            0,
                            DateTimeOffset.Parse("2026-03-29T09:30:00+08:00"))
                    ])
                {
                    ProjectionByDeviceCode = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dev-001"] = new(
                            "dev-001",
                            true,
                            true,
                            "converted",
                            "已转换为高德",
                            "地图 marker 仅使用转换后的高德坐标。",
                            "31.224361",
                            "121.469170")
                    }
                }),
            new MapOptions
            {
                JsKey = "__SET_IN_LOCAL_FILE__",
                SecurityJsCode = "__SET_IN_LOCAL_FILE__"
            },
            NullLogger<MapPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        using var document = JsonDocument.Parse(viewModel.MapBootstrapJson);
        var map = document.RootElement.GetProperty("map");
        var point = document.RootElement.GetProperty("points")[0];

        Assert.Equal("baidu", map.GetProperty("rawCoordinateSystem").GetString());
        Assert.Equal("gaode", map.GetProperty("renderCoordinateSystem").GetString());
        Assert.Equal("31.2304", point.GetProperty("rawLatitude").GetString());
        Assert.Equal("121.4737", point.GetProperty("rawLongitude").GetString());
        Assert.Equal("31.224361", point.GetProperty("mapLatitude").GetString());
        Assert.Equal("121.469170", point.GetProperty("mapLongitude").GetString());
        Assert.True(point.GetProperty("hasRawCoordinate").GetBoolean());
        Assert.True(point.GetProperty("hasMapCoordinate").GetBoolean());
        Assert.Equal("converted", point.GetProperty("coordinateState").GetString());
        Assert.False(point.TryGetProperty("latitude", out _));
        Assert.False(point.TryGetProperty("longitude", out _));
    }

    [Fact]
    public async Task InitializeAsync_MarksMissingCoordinatePointAsMissing()
    {
        var viewModel = new MapPageViewModel(
            new StubMapService(
                new MapLoadResult(
                    true,
                    string.Empty,
                    [
                        new InspectionDevice(
                            "dev-002",
                            "无坐标设备",
                            "group-001",
                            null,
                            null,
                            "未定位",
                            0,
                            1,
                            0,
                            0,
                            DateTimeOffset.Parse("2026-03-29T09:35:00+08:00"))
                    ])
                {
                    ProjectionByDeviceCode = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dev-002"] = new(
                            "dev-002",
                            false,
                            false,
                            "missing",
                            "平台未提供坐标",
                            "平台未提供坐标，当前不进入上图。",
                            null,
                            null)
                    }
                }),
            new MapOptions
            {
                JsKey = "__SET_IN_LOCAL_FILE__",
                SecurityJsCode = "__SET_IN_LOCAL_FILE__"
            },
            NullLogger<MapPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        using var document = JsonDocument.Parse(viewModel.MapBootstrapJson);
        var point = document.RootElement.GetProperty("points")[0];

        Assert.False(point.GetProperty("hasRawCoordinate").GetBoolean());
        Assert.False(point.GetProperty("hasMapCoordinate").GetBoolean());
        Assert.Equal("missing", point.GetProperty("coordinateState").GetString());
        Assert.Equal("平台未提供坐标", point.GetProperty("coordinateStateText").GetString());
        Assert.Contains("平台未提供坐标", point.GetProperty("coordinateWarning").GetString());
    }

    [Fact]
    public async Task InitializeAsync_KeepsFailedProjectionOutOfMapRendering()
    {
        var viewModel = new MapPageViewModel(
            new StubMapService(
                new MapLoadResult(
                    true,
                    string.Empty,
                    [
                        new InspectionDevice(
                            "dev-003",
                            "转换失败设备",
                            "group-001",
                            "31.2304",
                            "121.4737",
                            "上海",
                            1,
                            1,
                            1,
                            0,
                            DateTimeOffset.Parse("2026-03-29T09:40:00+08:00"),
                            "platform",
                            "available",
                            "平台原始坐标来自 getDeviceInfoByDeviceCode。")
                    ])
                {
                    ProjectionByDeviceCode = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dev-003"] = new(
                            "dev-003",
                            true,
                            false,
                            "failed",
                            "坐标转换失败，需人工确认",
                            "高德坐标转换未完成，需人工确认。",
                            null,
                            null)
                    }
                }),
            new MapOptions
            {
                JsKey = "__SET_IN_LOCAL_FILE__",
                SecurityJsCode = "__SET_IN_LOCAL_FILE__"
            },
            NullLogger<MapPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        using var document = JsonDocument.Parse(viewModel.MapBootstrapJson);
        var point = document.RootElement.GetProperty("points")[0];

        Assert.False(point.GetProperty("hasMapCoordinate").GetBoolean());
        Assert.Equal("failed", point.GetProperty("coordinateState").GetString());
        Assert.Equal("坐标转换失败，需人工确认", point.GetProperty("coordinateStateText").GetString());
    }

    [Fact]
    public async Task InitializeAsync_ShowsRateLimitedCoordinateStateInBootstrap()
    {
        var viewModel = new MapPageViewModel(
            new StubMapService(
                new MapLoadResult(
                    true,
                    string.Empty,
                    [
                        new InspectionDevice(
                            "dev-004",
                            "限频设备",
                            "group-001",
                            null,
                            null,
                            "上海",
                            1,
                            1,
                            1,
                            0,
                            DateTimeOffset.Parse("2026-03-29T09:45:00+08:00"),
                            "none",
                            CoordinateStateCatalog.RateLimited,
                            "坐标获取限频，稍后重试。")
                    ])
                {
                    ProjectionByDeviceCode = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dev-004"] = new(
                            "dev-004",
                            false,
                            false,
                            CoordinateStateCatalog.RateLimited,
                            "坐标获取限频，稍后重试",
                            "坐标获取限频，稍后重试。",
                            null,
                            null)
                    }
                }),
            new MapOptions
            {
                JsKey = "__SET_IN_LOCAL_FILE__",
                SecurityJsCode = "__SET_IN_LOCAL_FILE__"
            },
            NullLogger<MapPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        using var document = JsonDocument.Parse(viewModel.MapBootstrapJson);
        var point = document.RootElement.GetProperty("points")[0];

        Assert.Equal("rate_limited", point.GetProperty("coordinateState").GetString());
        Assert.Equal("坐标获取限频，稍后重试", point.GetProperty("coordinateStateText").GetString());
        Assert.Contains("限频", point.GetProperty("coordinateWarning").GetString());
    }

    [Fact]
    public async Task InitializeAsync_UsesUnifiedUnmappedStatsInBootstrap()
    {
        InspectionDevice[] devices =
        [
            new InspectionDevice(
                "dev-001",
                "已上图设备",
                "group-001",
                "31.2304",
                "121.4737",
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
                "platform",
                CoordinateStateCatalog.Available,
                "平台原始坐标来自 getDeviceInfoByDeviceCode。")
            {
                MapLatitude = "31.224361",
                MapLongitude = "121.469170"
            },
            new InspectionDevice(
                "dev-002",
                "缺坐标设备",
                "group-001",
                null,
                null,
                "上海",
                0,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
                "none",
                CoordinateStateCatalog.Missing,
                "平台未提供坐标。"),
            new InspectionDevice(
                "dev-003",
                "限频设备",
                "group-001",
                null,
                null,
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
                "none",
                CoordinateStateCatalog.RateLimited,
                "坐标获取限频，稍后重试。"),
            new InspectionDevice(
                "dev-004",
                "失败设备",
                "group-001",
                "31.2304",
                "121.4737",
                "上海",
                1,
                1,
                1,
                0,
                DateTimeOffset.Parse("2026-03-29T10:00:00+08:00"),
                "platform",
                CoordinateStateCatalog.Failed,
                "高德回传值非法。")
        ];
        var viewModel = new MapPageViewModel(
            new StubMapService(
                new MapLoadResult(true, string.Empty, devices)
                {
                    ProjectionByDeviceCode = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dev-001"] = new(
                            "dev-001",
                            true,
                            true,
                            CoordinateStateCatalog.Available,
                            "已获取并转换坐标",
                            "地图 marker 仅使用转换后的高德坐标。",
                            "31.224361",
                            "121.469170"),
                        ["dev-002"] = new(
                            "dev-002",
                            false,
                            false,
                            CoordinateStateCatalog.Missing,
                            "平台未提供坐标",
                            "平台未提供坐标，当前不进入上图。",
                            null,
                            null),
                        ["dev-003"] = new(
                            "dev-003",
                            false,
                            false,
                            CoordinateStateCatalog.RateLimited,
                            "坐标获取限频，稍后重试",
                            "坐标获取限频，稍后重试。",
                            null,
                            null),
                        ["dev-004"] = new(
                            "dev-004",
                            true,
                            false,
                            CoordinateStateCatalog.Failed,
                            "高德回传值非法",
                            "高德坐标转换结果无效。",
                            null,
                            null)
                    }
                }),
            new MapOptions
            {
                JsKey = "__SET_IN_LOCAL_FILE__",
                SecurityJsCode = "__SET_IN_LOCAL_FILE__"
            },
            NullLogger<MapPageViewModel>.Instance);

        await viewModel.InitializeAsync();

        using var document = JsonDocument.Parse(viewModel.MapBootstrapJson);
        var stats = document.RootElement.GetProperty("stats");

        Assert.Equal(1, stats.GetProperty("renderedCount").GetInt32());
        Assert.Equal(3, stats.GetProperty("unmappedCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("missingCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("rateLimitedCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("failedCount").GetInt32());
        Assert.Contains("未上图", stats.GetProperty("unmappedSummaryText").GetString());
    }

    private sealed class StubMapService(MapLoadResult result) : IMapService
    {
        public Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }
}
