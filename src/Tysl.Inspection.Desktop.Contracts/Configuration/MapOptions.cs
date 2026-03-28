namespace Tysl.Inspection.Desktop.Contracts.Configuration;

public sealed class MapOptions
{
    public string JsKey { get; set; } = string.Empty;

    public string SecurityJsCode { get; set; } = string.Empty;

    public string JsApiVersion { get; set; } = "2.0";

    public string CoordinateSystem { get; set; } = string.Empty;

    public bool HasJsKey()
    {
        return HasRealValue(JsKey);
    }

    public bool HasSecurityJsCode()
    {
        return HasRealValue(SecurityJsCode);
    }

    private static bool HasRealValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !value.Contains("__SET_IN_LOCAL_FILE__", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("__OPTIONAL__", StringComparison.OrdinalIgnoreCase);
    }
}
