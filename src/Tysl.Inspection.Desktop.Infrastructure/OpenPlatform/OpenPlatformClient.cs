using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Contracts.OpenPlatform;
using Tysl.Inspection.Desktop.Infrastructure.Security;
using Tysl.Inspection.Desktop.Infrastructure.Support;

namespace Tysl.Inspection.Desktop.Infrastructure.OpenPlatform;

public sealed class OpenPlatformClient : IOpenPlatformClient, IDisposable
{
    private const string DeviceStatusPath = "/open/token/vpaas/device/getDeviceStatus";
    private const string DevicePreviewPath = "/open/token/cloud/getDeviceMediaUrlRtsp";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly TianyiOpenPlatformOptions options;
    private readonly AppRuntimePaths runtimePaths;
    private readonly ILogger<OpenPlatformClient> logger;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private OpenPlatformAccessTokenPayload? tokenCache;

    public OpenPlatformClient(
        IOptions<TianyiOpenPlatformOptions> options,
        AppRuntimePaths runtimePaths,
        ILogger<OpenPlatformClient> logger)
    {
        this.options = options.Value;
        this.runtimePaths = runtimePaths;
        this.logger = logger;

        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpClient.DefaultRequestHeaders.Add("apiVersion", this.options.ApiVersion);
    }

    public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        return EnsureAccessTokenAsync(cancellationToken);
    }

    public async Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformGroupDto>>> GetGroupListAsync(CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<IReadOnlyList<OpenPlatformGroupDto>>(
                "getGroupList",
                BuildUrl("/open/token/vcpGroup/getGroupList"),
                accessTokenResult.BuildMessage());
        }

        var requestParameters = new List<KeyValuePair<string, string?>>
        {
            new("accessToken", accessTokenResult.Payload.AccessToken),
            new("enterpriseUser", options.EnterpriseUser)
        };

        return await SendAsync(
            endpointName: "getGroupList",
            path: "/open/token/vcpGroup/getGroupList",
            privateParameters: requestParameters,
            parsePayload: root => (IReadOnlyList<OpenPlatformGroupDto>)(JsonSerializer.Deserialize<List<OpenPlatformGroupDto>>(root.GetRawText(), JsonSerializerOptions) ?? []),
            cancellationToken: cancellationToken);
    }

    public async Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformDeviceDto>>> GetGroupDeviceListAsync(
        string groupId,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<IReadOnlyList<OpenPlatformDeviceDto>>(
                "getGroupDeviceList",
                BuildUrl("/open/token/vcpGroup/getGroupDeviceList"),
                accessTokenResult.BuildMessage());
        }

        var requestParameters = new List<KeyValuePair<string, string?>>
        {
            new("accessToken", accessTokenResult.Payload.AccessToken),
            new("enterpriseUser", options.EnterpriseUser),
            new("groupId", groupId)
        };

        return await SendAsync(
            endpointName: "getGroupDeviceList",
            path: "/open/token/vcpGroup/getGroupDeviceList",
            privateParameters: requestParameters,
            parsePayload: root =>
            {
                var devices = JsonSerializer.Deserialize<List<OpenPlatformDeviceDto>>(root.GetRawText(), JsonSerializerOptions) ?? [];
                return (IReadOnlyList<OpenPlatformDeviceDto>)devices
                    .Select(device => device with { GroupId = string.IsNullOrWhiteSpace(device.GroupId) ? groupId : device.GroupId })
                    .ToArray();
            },
            cancellationToken: cancellationToken);
    }

    public async Task<OpenPlatformCallResult<OpenPlatformDeviceStatusPayload>> GetDeviceStatusAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<OpenPlatformDeviceStatusPayload>(
                "getDeviceStatus",
                BuildUrl(DeviceStatusPath),
                $"accessToken 获取失败：{accessTokenResult.BuildMessage()}");
        }

        var requestParameters = new List<KeyValuePair<string, string?>>
        {
            new("accessToken", accessTokenResult.Payload.AccessToken),
            new("enterpriseUser", options.EnterpriseUser),
            new("deviceCode", deviceCode)
        };

        return await SendAsync(
            endpointName: "getDeviceStatus",
            path: DeviceStatusPath,
            privateParameters: requestParameters,
            parsePayload: root => ParseDeviceStatus(root, deviceCode),
            cancellationToken: cancellationToken);
    }

    public async Task<OpenPlatformCallResult<OpenPlatformPreviewUrlPayload>> GetDevicePreviewUrlAsync(
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<OpenPlatformPreviewUrlPayload>(
                "getDeviceMediaUrlRtsp",
                BuildUrl(DevicePreviewPath),
                $"accessToken 获取失败：{accessTokenResult.BuildMessage()}");
        }

        var requestParameters = new List<KeyValuePair<string, string?>>
        {
            new("accessToken", accessTokenResult.Payload.AccessToken),
            new("enterpriseUser", options.EnterpriseUser),
            new("deviceCode", deviceCode)
        };

        return await SendAsync(
            endpointName: "getDeviceMediaUrlRtsp",
            path: DevicePreviewPath,
            privateParameters: requestParameters,
            parsePayload: ParsePreviewUrl,
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        httpClient.Dispose();
        tokenLock.Dispose();
    }

    private async Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            tokenCache ??= await LoadTokenCacheAsync(cancellationToken);
            if (tokenCache is not null && tokenCache.ExpiresAt > now.AddMinutes(1))
            {
                return new OpenPlatformCallResult<OpenPlatformAccessTokenPayload>
                {
                    Success = true,
                    EndpointName = "getAccessToken",
                    RequestUrl = BuildUrl("/open/oauth/getAccessToken"),
                    Payload = tokenCache
                };
            }

            var useRefreshToken = tokenCache is not null && tokenCache.RefreshExpiresAt > now.AddMinutes(1);
            var parameters = new List<KeyValuePair<string, string?>>
            {
                new("grantType", useRefreshToken ? "refresh_token" : "vcp_189")
            };

            if (useRefreshToken)
            {
                parameters.Add(new("refreshToken", tokenCache!.RefreshToken));
            }
            else
            {
                parameters.Add(new("enterpriseUser", options.EnterpriseUser));
                if (!string.IsNullOrWhiteSpace(options.ParentUser))
                {
                    parameters.Add(new("parentUser", options.ParentUser));
                }
            }

            var result = await SendAsync(
                endpointName: "getAccessToken",
                path: "/open/oauth/getAccessToken",
                privateParameters: parameters,
                parsePayload: root =>
                {
                    var requestedAt = DateTimeOffset.UtcNow;
                    var accessToken = GetRequiredString(root, "accessToken");
                    var refreshToken = GetRequiredString(root, "refreshToken");
                    var expiresInSeconds = GetInt32(root, "expiresIn", "expires_in", "expire") ?? 604800;
                    return new OpenPlatformAccessTokenPayload(
                        accessToken,
                        refreshToken,
                        expiresInSeconds,
                        requestedAt,
                        requestedAt.AddSeconds(expiresInSeconds),
                        requestedAt.AddDays(30));
                },
                cancellationToken: cancellationToken);

            if (result.Success && result.Payload is not null)
            {
                tokenCache = result.Payload;
                await SaveTokenCacheAsync(result.Payload, cancellationToken);
            }

            return result;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<OpenPlatformCallResult<T>> SendAsync<T>(
        string endpointName,
        string path,
        IReadOnlyList<KeyValuePair<string, string?>> privateParameters,
        Func<JsonElement, T> parsePayload,
        CancellationToken cancellationToken)
    {
        var requestUrl = BuildUrl(path);
        var requestedAt = DateTimeOffset.UtcNow;
        var plainParams = string.Join(
            "&",
            privateParameters
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var encryptedParams = XxTea.EncryptToHex(plainParams, options.AppSecret);
        var timestamp = requestedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signatureSource = $"{options.AppId}{options.ClientType}{encryptedParams}{timestamp}{options.Version}";
        var signature = ComputeSignature(signatureSource, options.AppSecret);

        var requestParameters = new List<KeyValuePair<string, string?>>
        {
            new("appId", options.AppId),
            new("clientType", options.ClientType.ToString(CultureInfo.InvariantCulture)),
            new("params", encryptedParams),
            new("timestamp", timestamp),
            new("version", options.Version),
            new("signature", signature)
        };

        var maskedHeaders = SensitiveDataMasker.MaskDictionary(
            httpClient.DefaultRequestHeaders.SelectMany(header => header.Value.Select(value => new KeyValuePair<string, string?>(header.Key, value))));
        var maskedRequest = SensitiveDataMasker.MaskDictionary(privateParameters);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new FormUrlEncodedContent(requestParameters!)
            };

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            var maskedResponse = SensitiveDataMasker.MaskJson(responseText);
            LogApiAccess(
                endpointName,
                requestUrl,
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                maskedRequest,
                maskedHeaders,
                maskedResponse,
                null);

            using var envelope = JsonDocument.Parse(responseText);
            var root = envelope.RootElement;
            var platformCode = GetString(root, "code", "resultCode");
            var platformMessage = GetString(root, "msg", "message", "resultMsg");

            if (!response.IsSuccessStatusCode)
            {
                return new OpenPlatformCallResult<T>
                {
                    Success = false,
                    EndpointName = endpointName,
                    RequestUrl = requestUrl,
                    HttpStatusCode = response.StatusCode,
                    PlatformCode = platformCode,
                    PlatformMessage = platformMessage,
                    MaskedResponse = maskedResponse,
                    ErrorMessage = $"HTTP {(int)response.StatusCode} {response.StatusCode}"
                };
            }

            if (!IsSuccessCode(platformCode))
            {
                return new OpenPlatformCallResult<T>
                {
                    Success = false,
                    EndpointName = endpointName,
                    RequestUrl = requestUrl,
                    HttpStatusCode = response.StatusCode,
                    PlatformCode = platformCode,
                    PlatformMessage = platformMessage,
                    MaskedResponse = maskedResponse,
                    ErrorMessage = platformMessage ?? $"Platform returned code {platformCode}"
                };
            }

            var payloadElement = ExtractPayloadElement(root);
            var payload = parsePayload(payloadElement);

            return new OpenPlatformCallResult<T>
            {
                Success = true,
                EndpointName = endpointName,
                RequestUrl = requestUrl,
                HttpStatusCode = response.StatusCode,
                PlatformCode = platformCode,
                PlatformMessage = platformMessage,
                MaskedResponse = maskedResponse,
                Payload = payload
            };
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            LogApiAccess(
                endpointName,
                requestUrl,
                null,
                stopwatch.ElapsedMilliseconds,
                maskedRequest,
                maskedHeaders,
                string.Empty,
                exception.Message);

            return Fail<T>(endpointName, requestUrl, exception.Message);
        }
    }

    private JsonElement ExtractPayloadElement(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var payloadElement))
        {
            throw new InvalidOperationException("Response does not contain data.");
        }

        if (payloadElement.ValueKind == JsonValueKind.String)
        {
            var dataText = payloadElement.GetString() ?? string.Empty;
            var decrypted = TryDecryptResponseData(dataText);
            if (!string.IsNullOrWhiteSpace(decrypted))
            {
                using var decryptedDocument = JsonDocument.Parse(decrypted);
                return decryptedDocument.RootElement.Clone();
            }
        }

        return payloadElement.Clone();
    }

    private string TryDecryptResponseData(string encryptedPayload)
    {
        if (string.IsNullOrWhiteSpace(encryptedPayload))
        {
            return encryptedPayload;
        }

        if (encryptedPayload.StartsWith("{", StringComparison.Ordinal) || encryptedPayload.StartsWith("[", StringComparison.Ordinal))
        {
            return encryptedPayload;
        }

        try
        {
            using var rsa = RSA.Create();
            var keyText = options.RsaPrivateKey.Trim();
            if (keyText.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                rsa.ImportFromPem(keyText);
            }
            else
            {
                var keyBytes = Convert.FromBase64String(keyText);
                rsa.ImportRSAPrivateKey(keyBytes, out _);
            }

            var encryptedBytes = Convert.FromBase64String(encryptedPayload);
            try
            {
                return Encoding.UTF8.GetString(rsa.Decrypt(encryptedBytes, RSAEncryptionPadding.Pkcs1));
            }
            catch (CryptographicException)
            {
                return Encoding.UTF8.GetString(rsa.Decrypt(encryptedBytes, RSAEncryptionPadding.OaepSHA1));
            }
        }
        catch
        {
            return encryptedPayload;
        }
    }

    private static string ComputeSignature(string source, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash);
    }

    private string BuildUrl(string path)
    {
        return $"{options.BaseUrl.TrimEnd('/')}{path}";
    }

    private static bool IsSuccessCode(string? platformCode)
    {
        return string.IsNullOrWhiteSpace(platformCode)
            || platformCode == "0"
            || platformCode == "200"
            || platformCode.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OpenPlatformAccessTokenPayload?> LoadTokenCacheAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(runtimePaths.TokenCachePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(runtimePaths.TokenCachePath, cancellationToken);
        return JsonSerializer.Deserialize<OpenPlatformAccessTokenPayload>(json, JsonSerializerOptions);
    }

    private async Task SaveTokenCacheAsync(OpenPlatformAccessTokenPayload payload, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(runtimePaths.DataPath);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions);
        await File.WriteAllTextAsync(runtimePaths.TokenCachePath, json, cancellationToken);
    }

    private void LogApiAccess(
        string endpointName,
        string requestUrl,
        HttpStatusCode? statusCode,
        long elapsedMilliseconds,
        IReadOnlyDictionary<string, string?> maskedRequest,
        IReadOnlyDictionary<string, string?> maskedHeaders,
        string maskedResponse,
        string? exceptionSummary)
    {
        logger.LogInformation(
            "Open platform call {EndpointName} url={RequestUrl} status={StatusCode} elapsed={ElapsedMilliseconds}ms request={Request} headers={Headers} response={Response} exception={ExceptionSummary}",
            endpointName,
            requestUrl,
            statusCode?.ToString() ?? "n/a",
            elapsedMilliseconds,
            JsonSerializer.Serialize(maskedRequest),
            JsonSerializer.Serialize(maskedHeaders),
            maskedResponse,
            exceptionSummary ?? string.Empty);
    }

    private static string GetRequiredString(JsonElement element, params string[] names)
    {
        return GetString(element, names) ?? throw new InvalidOperationException($"Missing required field: {string.Join("/", names)}");
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            }
        }

        return null;
    }

    private static int? GetInt32(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static OpenPlatformDeviceStatusPayload ParseDeviceStatus(JsonElement root, string requestedDeviceCode)
    {
        var source = UnwrapPayload(root, requestedDeviceCode);
        var deviceCode = GetString(source, "deviceCode", "deviceId", "code") ?? requestedDeviceCode;
        var onlineStatus = GetInt32(source, "onlineStatus", "deviceStatus", "status", "devStatus");
        return new OpenPlatformDeviceStatusPayload(deviceCode, onlineStatus);
    }

    private static OpenPlatformPreviewUrlPayload ParsePreviewUrl(JsonElement root)
    {
        var source = UnwrapPayload(root, null);
        var url = GetString(source, "url", "rtspUrl", "playUrl", "previewUrl")
            ?? throw new InvalidOperationException("Missing required field: url");
        var expireTime = GetString(source, "expireTime", "expire", "expireAt", "expireDateTime");
        return new OpenPlatformPreviewUrlPayload(url, expireTime);
    }

    private static JsonElement UnwrapPayload(JsonElement root, string? requestedDeviceCode)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return PickArrayItem(root, requestedDeviceCode);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return root;
        }

        if (TryGetNestedArrayItem(root, requestedDeviceCode, out var arrayItem))
        {
            return arrayItem;
        }

        foreach (var name in new[] { "item", "device", "result", "data" })
        {
            if (root.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                return nested;
            }
        }

        return root;
    }

    private static bool TryGetNestedArrayItem(JsonElement root, string? requestedDeviceCode, out JsonElement item)
    {
        foreach (var name in new[] { "list", "rows", "devices", "items", "deviceList", "deviceStatusList" })
        {
            if (root.TryGetProperty(name, out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                item = PickArrayItem(nested, requestedDeviceCode);
                return true;
            }
        }

        item = default;
        return false;
    }

    private static JsonElement PickArrayItem(JsonElement array, string? requestedDeviceCode)
    {
        JsonElement? firstItem = null;

        foreach (var item in array.EnumerateArray())
        {
            firstItem ??= item.Clone();
            var itemCode = GetString(item, "deviceCode", "deviceId", "code");
            if (!string.IsNullOrWhiteSpace(requestedDeviceCode)
                && string.Equals(itemCode, requestedDeviceCode, StringComparison.OrdinalIgnoreCase))
            {
                return item.Clone();
            }
        }

        return firstItem?.Clone() ?? array.Clone();
    }

    private static OpenPlatformCallResult<T> Fail<T>(string endpointName, string requestUrl, string message)
    {
        return new OpenPlatformCallResult<T>
        {
            Success = false,
            EndpointName = endpointName,
            RequestUrl = requestUrl,
            ErrorMessage = message
        };
    }
}
