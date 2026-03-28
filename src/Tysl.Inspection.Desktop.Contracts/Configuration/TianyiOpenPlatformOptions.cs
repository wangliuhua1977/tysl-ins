namespace Tysl.Inspection.Desktop.Contracts.Configuration;

public sealed class TianyiOpenPlatformOptions
{
    public string BaseUrl { get; set; } = "https://vcp.21cn.com";

    public string ApiVersion { get; set; } = "2.0";

    public int ClientType { get; set; } = 3;

    public string Version { get; set; } = "1.1";

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    public string RsaPrivateKey { get; set; } = string.Empty;

    public string EnterpriseUser { get; set; } = string.Empty;

    public string? ParentUser { get; set; }

    public bool HasRequiredSecrets()
    {
        return HasRealValue(AppId)
            && HasRealValue(AppSecret)
            && HasRealValue(RsaPrivateKey)
            && HasRealValue(EnterpriseUser);
    }

    private static bool HasRealValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !value.Contains("__SET_IN_LOCAL_FILE__", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("__OPTIONAL__", StringComparison.OrdinalIgnoreCase);
    }
}
