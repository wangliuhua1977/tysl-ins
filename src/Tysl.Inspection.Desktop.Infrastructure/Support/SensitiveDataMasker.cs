using System.Text.Json;

namespace Tysl.Inspection.Desktop.Infrastructure.Support;

internal static class SensitiveDataMasker
{
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "accessToken",
        "refreshToken",
        "appSecret",
        "signature",
        "params",
        "rsaPrivateKey",
        "defaultWebhook",
        "webhook",
        "url",
        "rtspUrl",
        "playUrl",
        "phone",
        "enterpriseUser",
        "parentUser"
    };

    public static IReadOnlyDictionary<string, string?> MaskDictionary(IEnumerable<KeyValuePair<string, string?>> values)
    {
        return values.ToDictionary(
            pair => pair.Key,
            pair => SensitiveKeys.Contains(pair.Key) ? Mask(pair.Value) : pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length <= 8)
        {
            return "****";
        }

        return $"{value[..4]}****{value[^4..]}";
    }

    public static string MaskJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return MaskElement(document.RootElement);
        }
        catch
        {
            return Mask(json);
        }
    }

    private static string MaskElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(
                ",",
                element.EnumerateObject().Select(property =>
                {
                    var value = SensitiveKeys.Contains(property.Name)
                        ? JsonSerializer.Serialize(Mask(property.Value.ToString()))
                        : MaskElement(property.Value);
                    return $"{JsonSerializer.Serialize(property.Name)}:{value}";
                })) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(MaskElement)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
            _ => element.GetRawText()
        };
    }
}
