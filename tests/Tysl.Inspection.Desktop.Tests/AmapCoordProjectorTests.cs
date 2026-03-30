using System.Text.Json;
using Tysl.Inspection.Desktop.App.Services;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Tests;

public sealed class AmapCoordProjectorTests
{
    [Fact]
    public void ParseHostReadyPayload_UnwrapsWrappedReadyEnvelope()
    {
        const string readyJson = """
            {
              "type": "coord-conv-ready",
              "protocolVersion": "1.0",
              "ready": true,
              "scriptLoaded": true,
              "hasAmap": true,
              "hasConvertFrom": true,
              "errorStage": "",
              "errorMessage": ""
            }
            """;
        var responseJson = JsonSerializer.Serialize(readyJson);

        var payload = AmapCoordProjector.ParseHostReadyPayload(responseJson);

        Assert.Equal("coord-conv-ready", payload.Type);
        Assert.Equal("1.0", payload.ProtocolVersion);
        Assert.True(payload.Ready);
        Assert.True(payload.ScriptLoaded);
        Assert.True(payload.HasAmap);
        Assert.True(payload.HasConvertFrom);
        Assert.Equal("String", payload.TransportRootKind);
    }

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
              "errorCount": 0,
              "items": [
                {
                  "deviceCode": "dev-001",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": true,
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "available",
                  "coordinateStateText": "Coordinate converted to GCJ-02.",
                  "mapLatitude": "31.224361",
                  "mapLongitude": "121.469170",
                  "amapStatus": "complete",
                  "amapInfo": "ok"
                },
                {
                  "deviceCode": "dev-002",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": false,
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "failed",
                  "coordinateStateText": "Coordinate conversion failed.",
                  "errorStage": "convert_from",
                  "errorCode": "convert_from_failed",
                  "errorMessage": "AMap.convertFrom failed: invalid params",
                  "amapStatus": "error",
                  "amapInfo": "invalid params",
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
              "errorCount": 0,
              "items": [
                {
                  "deviceCode": "dev-001",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": true,
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "available",
                  "coordinateStateText": "Coordinate converted to GCJ-02.",
                  "mapLatitude": "31.224361",
                  "mapLongitude": "121.469170"
                },
                {
                  "deviceCode": "dev-002",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": "oops",
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "available",
                  "coordinateStateText": "Coordinate converted to GCJ-02."
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
        Assert.Contains(payload.Errors, error => string.Equals(error.DeviceCode, "dev-002", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("string", "complete", "ok")]
    [InlineData("object", "complete", "ok")]
    [InlineData("array", "complete", "ok")]
    public void ParseBatchPayload_ReadsPerItemDiagnosticsForDifferentResultLocationKinds(
        string resultLocationKind,
        string amapStatus,
        string amapInfo)
    {
        var responseJson = $$"""
            {
              "type": "coord-conversion-batch",
              "protocolVersion": "1.0",
              "requestedCount": 1,
              "successCount": 1,
              "failedCount": 0,
              "errorCount": 0,
              "items": [
                {
                  "deviceCode": "dev-003",
                  "rawLatitude": "31.2304",
                  "rawLongitude": "121.4737",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": true,
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "available",
                  "coordinateStateText": "Coordinate converted to GCJ-02.",
                  "mapLatitude": "31.224361",
                  "mapLongitude": "121.469170",
                  "amapStatus": "{{amapStatus}}",
                  "amapInfo": "{{amapInfo}}",
                  "resultLocationKind": "{{resultLocationKind}}",
                  "resultLatitude": "31.224361",
                  "resultLongitude": "121.469170"
                }
              ],
              "errors": []
            }
            """;

        var payload = AmapCoordProjector.ParseBatchPayload(responseJson);
        var item = Assert.Single(payload.Items);

        Assert.Equal("dev-003", item.DeviceCode);
        Assert.Equal("31.2304", item.RawLatitude);
        Assert.Equal("121.4737", item.RawLongitude);
        Assert.Equal(amapStatus, item.AmapStatus);
        Assert.Equal(amapInfo, item.AmapInfo);
        Assert.Equal(resultLocationKind, item.ResultLocationKind);
        Assert.Equal("31.224361", item.ResultLatitude);
        Assert.Equal("121.469170", item.ResultLongitude);
    }

    [Fact]
    public void ClassifyPayloadKind_DetectsEmptyObjectAfterStringWrapping()
    {
        var responseJson = JsonSerializer.Serialize("{}");

        var kind = AmapCoordProjector.ClassifyPayloadKind(responseJson);

        Assert.Equal("empty_object", kind);
    }

    [Fact]
    public void ParseBatchPayload_ReadsErrorFieldsUsingUnifiedEnvelopeShape()
    {
        const string responseJson = """
            {
              "type": "coord-conversion-batch",
              "protocolVersion": "1.0",
              "requestedCount": 1,
              "successCount": 0,
              "failedCount": 1,
              "errorCount": 1,
              "items": [
                {
                  "deviceCode": "dev-009",
                  "rawLatitude": "31.2304",
                  "rawLongitude": "121.4737",
                  "hasRawCoordinate": true,
                  "hasMapCoordinate": false,
                  "mapSource": "amap_js_convert_from_baidu",
                  "coordinateState": "failed",
                  "coordinateStateText": "AMap returned an invalid converted coordinate.",
                  "amapStatus": "error",
                  "amapInfo": "ok",
                  "errorStage": "result_parse",
                  "errorCode": "result_location_invalid",
                  "errorMessage": "AMap.convertFrom returned invalid coordinates.",
                  "rawResultSnippet": "{\"lng\":null,\"lat\":31.2}",
                  "resultLocationKind": "object"
                }
              ],
              "errors": [
                {
                  "stage": "result_parse",
                  "deviceCode": "dev-009",
                  "errorCode": "result_location_invalid",
                  "message": "AMap.convertFrom returned invalid coordinates."
                }
              ]
            }
            """;

        var payload = AmapCoordProjector.ParseBatchPayload(responseJson);
        var item = Assert.Single(payload.Items);
        var error = Assert.Single(payload.Errors);

        Assert.Equal("result_parse", item.ErrorStage);
        Assert.Equal("result_location_invalid", item.ErrorCode);
        Assert.Equal("AMap.convertFrom returned invalid coordinates.", item.ErrorMessage);
        Assert.Equal("{\"lng\":null,\"lat\":31.2}", item.RawResultSnippet);
        Assert.Equal("error", item.AmapStatus);
        Assert.Equal("result_parse", error.Stage);
        Assert.Equal("result_location_invalid", error.ErrorCode);
    }
}
