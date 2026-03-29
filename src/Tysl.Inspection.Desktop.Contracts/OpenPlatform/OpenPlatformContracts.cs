using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tysl.Inspection.Desktop.Contracts.OpenPlatform;

public sealed record OpenPlatformAccessTokenPayload(
    string AccessToken,
    string RefreshToken,
    int ExpiresInSeconds,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshExpiresAt);

public sealed record OpenPlatformRegionDto(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string Id,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string RegionCode,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? HasChildren,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? HavDevice,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string Name,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? Level,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? RegionGBId);

public sealed record OpenPlatformRegionDeviceDto(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string DeviceCode,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string DeviceName);

public sealed record OpenPlatformRegionDevicePageDto(
    IReadOnlyList<OpenPlatformRegionDeviceDto> Items,
    int PageNo,
    int PageSize,
    int TotalCount);

public sealed record OpenPlatformRegionDeviceCountDto(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string RegionCode,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? DeviceCount,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? OnlineCount);

public sealed record OpenPlatformDeviceStatusPayload(
    string DeviceCode,
    int? OnlineStatus);

public sealed record OpenPlatformDeviceInfoPayload(
    string DeviceCode,
    string DeviceName,
    string? Latitude,
    string? Longitude,
    string? Location);

public sealed record OpenPlatformPreviewUrlPayload(
    string Url,
    string? ExpireTime);

public sealed record OpenPlatformCallResult<T>
{
    public bool Success { get; init; }

    public string EndpointName { get; init; } = string.Empty;

    public string RequestUrl { get; init; } = string.Empty;

    public HttpStatusCode? HttpStatusCode { get; init; }

    public string? PlatformCode { get; init; }

    public string? PlatformMessage { get; init; }

    public string? MaskedResponse { get; init; }

    public string? ErrorMessage { get; init; }

    public T? Payload { get; init; }

    public string BuildMessage()
    {
        return ErrorMessage
            ?? PlatformMessage
            ?? $"Open platform call {EndpointName} failed.";
    }
}

public interface IOpenPlatformClient
{
    Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(
        string regionId,
        CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(
        string regionId,
        int pageNo,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(
        string regionCode,
        CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<OpenPlatformDeviceInfoPayload>> GetDeviceInfoByDeviceCodeAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(
        string deviceCode,
        CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(
        string deviceCode,
        CancellationToken cancellationToken);
}

public sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            _ => JsonDocument.ParseValue(ref reader).RootElement.ToString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class FlexibleNullableIntJsonConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number,
            JsonTokenType.String => Parse(reader.GetString()),
            JsonTokenType.True => 1,
            JsonTokenType.False => 0,
            _ => Parse(JsonDocument.ParseValue(ref reader).RootElement.ToString())
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static int? Parse(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : null;
    }
}
