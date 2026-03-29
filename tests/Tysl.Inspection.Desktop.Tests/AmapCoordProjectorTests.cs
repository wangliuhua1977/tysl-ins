using System.Text.Json;
using Tysl.Inspection.Desktop.App.Services;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class AmapCoordProjectorTests
{
    [Fact]
    public void ParseBatchPayload_UnwrapsStringifiedEnvelopeAndReadsItems()
    {
        const string batchJson = """
            {
              "type": "coord-conversion-batch",
              "protocolVersion": "1.0",
              "requestedCount": 2,
              "successCount": 1,
              "failedCount": 1,
              "missingCount": 0,
              "errorCount": 0,
              "items": [
                {
                  "deviceCode": "dev-001",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": true,
                  "coordinateState": "available",
                  "coordinateStateText": "已获取并转换坐标",
                  "coordinateWarning": "地图 marker 仅使用转换后的高德坐标。",
                  "mapLatitude": "31.224361",
                  "mapLongitude": "121.469170"
                },
                {
                  "deviceCode": "dev-002",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": false,
                  "coordinateState": "failed",
                  "coordinateStateText": "坐标转换失败，需人工确认",
                  "coordinateWarning": "status=error / invalid params",
                  "mapLatitude": null,
                  "mapLongitude": null
                }
              ],
              "errors": []
            }
            """;
        var responseJson = JsonSerializer.Serialize(batchJson);

        var payload = AmapCoordProjector.ParseBatchPayload(responseJson);

        Assert.Equal("coord-conversion-batch", payload.Type);
        Assert.Equal("1.0", payload.ProtocolVersion);
        Assert.Equal("String", payload.TransportRootKind);
        Assert.Equal(2, payload.Items.Count);
        Assert.Equal("dev-001", payload.Items[0].DeviceCode);
        Assert.True(payload.Items[0].HasMapCoordinate);
        Assert.Equal("31.224361", payload.Items[0].MapLatitude);
        Assert.Equal("121.469170", payload.Items[0].MapLongitude);
    }

    [Fact]
    public void ParseBatchPayload_ConvertsMalformedItemToFailedPayloadWithoutDroppingValidItems()
    {
        const string responseJson = """
            {
              "type": "coord-conversion-batch",
              "protocolVersion": "1.0",
              "requestedCount": 2,
              "successCount": 1,
              "failedCount": 1,
              "missingCount": 0,
              "errorCount": 0,
              "items": [
                {
                  "deviceCode": "dev-001",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": true,
                  "coordinateState": "available",
                  "coordinateStateText": "已获取并转换坐标",
                  "coordinateWarning": "地图 marker 仅使用转换后的高德坐标。",
                  "mapLatitude": "31.224361",
                  "mapLongitude": "121.469170"
                },
                {
                  "deviceCode": "dev-002",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": "oops",
                  "coordinateState": "available",
                  "coordinateStateText": "已获取并转换坐标",
                  "coordinateWarning": "地图 marker 仅使用转换后的高德坐标。"
                }
              ],
              "errors": []
            }
            """;

        var payload = AmapCoordProjector.ParseBatchPayload(responseJson);
        var valid = Assert.Single(payload.Items, item => item.HasMapCoordinate);
        var malformed = Assert.Single(payload.Items, item => string.Equals(item.DeviceCode, "dev-002", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("dev-001", valid.DeviceCode);
        Assert.False(malformed.HasMapCoordinate);
        Assert.Equal(CoordinateStateCatalog.Failed, malformed.CoordinateState);
        Assert.Contains("回传解析失败", malformed.CoordinateWarning);
        Assert.Contains(payload.Errors, error => string.Equals(error.DeviceCode, "dev-002", StringComparison.OrdinalIgnoreCase));
    }
}
