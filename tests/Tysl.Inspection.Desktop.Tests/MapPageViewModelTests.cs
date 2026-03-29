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
                    ])),
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
        Assert.Equal(string.Empty, point.GetProperty("mapLatitude").GetString());
        Assert.Equal(string.Empty, point.GetProperty("mapLongitude").GetString());
        Assert.True(point.GetProperty("hasRawCoordinate").GetBoolean());
        Assert.False(point.GetProperty("hasMapCoordinate").GetBoolean());
        Assert.Equal("pending", point.GetProperty("coordinateState").GetString());
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
                    ])),
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
        Assert.Equal("无坐标", point.GetProperty("coordinateStateText").GetString());
        Assert.Contains("无法执行百度转高德", point.GetProperty("coordinateWarning").GetString());
    }

    private sealed class StubMapService(MapLoadResult result) : IMapService
    {
        public Task<MapLoadResult> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(result);
        }
    }
}
