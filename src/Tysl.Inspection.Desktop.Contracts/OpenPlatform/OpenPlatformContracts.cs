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

public sealed record OpenPlatformGroupDto(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string GroupId,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string GroupName,
    int DeviceCount);

public sealed record OpenPlatformDeviceDto(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string DeviceCode,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string DeviceName,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string GroupId,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Latitude,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Longitude,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Location,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? OnlineStatus,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? CloudStatus,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? BandStatus,
    [property: JsonConverter(typeof(FlexibleNullableIntJsonConverter))] int? SourceTypeFlag);

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

    Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>> GetGroupListAsync(CancellationToken cancellationToken);

    Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GetGroupDeviceListAsync(
        string groupId,
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
