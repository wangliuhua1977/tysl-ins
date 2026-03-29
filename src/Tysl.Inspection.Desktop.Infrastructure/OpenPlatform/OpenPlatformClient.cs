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
    private const string RegionListPath = "/open/token/device/getReginWithGroupList";
    private const string RegionDeviceListPath = "/open/token/device/getDeviceList";
    private const string RegionDeviceCountPath = "/open/token/vcpTree/getCusDeviceCount";
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
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private OpenPlatformAccessTokenPayload? tokenCache;

    public OpenPlatformClient(
        IOptions<TianyiOpenPlatformOptions> options,
        AppRuntimePaths runtimePaths,
        ILogger<OpenPlatformClient> logger)
        : this(options.Value, runtimePaths, logger)
    {
    }

    public OpenPlatformClient(
        TianyiOpenPlatformOptions options,
        AppRuntimePaths runtimePaths,
        ILogger<OpenPlatformClient> logger,
        HttpClient? httpClient = null)
    {
        this.options = options;
        this.runtimePaths = runtimePaths;
        this.logger = logger;

        this.httpClient = httpClient ?? new HttpClient();
        ownsHttpClient = httpClient is null;
        ConfigureHttpClient(this.httpClient, this.options.ApiVersion);
    }

    public Task<OpenPlatformCallResult<OpenPlatformAccessTokenPayload>> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        return EnsureAccessTokenAsync(cancellationToken);
    }

    public async Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDto>>> GetRegionListAsync(
        string regionId,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<IReadOnlyList<OpenPlatformRegionDto>>(
                "getReginWithGroupList",
                BuildUrl(RegionListPath),
                accessTokenResult.BuildMessage());
        }

        var requestParameters = CreateAuthorizedParameters(accessTokenResult.Payload.AccessToken);
        requestParameters.Add(new("regionId", regionId));

        return await SendAsync(
            endpointName: "getReginWithGroupList",
            path: RegionListPath,
            privateParameters: requestParameters,
            parsePayload: ParseRegionList,
            cancellationToken: cancellationToken);
    }

    public async Task<OpenPlatformCallResult<OpenPlatformRegionDevicePageDto>> GetRegionDevicePageAsync(
        string regionId,
        int pageNo,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<OpenPlatformRegionDevicePageDto>(
                "getDeviceList",
                BuildUrl(RegionDeviceListPath),
                accessTokenResult.BuildMessage());
        }

        var requestParameters = CreateAuthorizedParameters(accessTokenResult.Payload.AccessToken);
        requestParameters.Add(new("regionId", regionId));
        requestParameters.Add(new("pageNo", pageNo.ToString(CultureInfo.InvariantCulture)));
        requestParameters.Add(new("pageSize", Math.Clamp(pageSize, 1, 50).ToString(CultureInfo.InvariantCulture)));

        return await SendAsync(
            endpointName: "getDeviceList",
            path: RegionDeviceListPath,
            privateParameters: requestParameters,
            parsePayload: ParseRegionDevicePage,
            cancellationToken: cancellationToken);
    }

    public async Task<OpenPlatformCallResult<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>> GetRegionDeviceCountsAsync(
        string regionCode,
        CancellationToken cancellationToken)
    {
        var accessTokenResult = await EnsureAccessTokenAsync(cancellationToken);
        if (!accessTokenResult.Success || accessTokenResult.Payload is null)
        {
            return Fail<IReadOnlyList<OpenPlatformRegionDeviceCountDto>>(
                "getCusDeviceCount",
                BuildUrl(RegionDeviceCountPath),
                accessTokenResult.BuildMessage());
        }

        var requestParameters = CreateAuthorizedParameters(accessTokenResult.Payload.AccessToken);
        requestParameters.Add(new("regionCode", regionCode));

        return await SendAsync(
            endpointName: "getCusDeviceCount",
            path: RegionDeviceCountPath,
            privateParameters: requestParameters,
            parsePayload: ParseRegionDeviceCounts,
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

        var requestParameters = CreateAuthorizedParameters(accessTokenResult.Payload.AccessToken);
        requestParameters.Add(new("deviceCode", deviceCode));

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

        var requestParameters = CreateAuthorizedParameters(accessTokenResult.Payload.AccessToken);
        requestParameters.Add(new("deviceCode", deviceCode));

        return await SendAsync(
            endpointName: "getDeviceMediaUrlRtsp",
            path: DevicePreviewPath,
            privateParameters: requestParameters,
            parsePayload: ParsePreviewUrl,
            cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        tokenLock.Dispose();
    }

    private static void ConfigureHttpClient(HttpClient httpClient, string apiVersion)
    {
        httpClient.Timeout = TimeSpan.FromSeconds(30);

        if (!httpClient.DefaultRequestHeaders.Accept.Any(header =>
                string.Equals(header.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
        {
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!httpClient.DefaultRequestHeaders.Contains("apiVersion"))
        {
            httpClient.DefaultRequestHeaders.Add("apiVersion", apiVersion);
        }
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
                .Where(pair => pair.Value is not null)
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
        HttpStatusCode? statusCode = null;
        var maskedResponse = string.Empty;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl)
            {
                Content = new FormUrlEncodedContent(requestParameters!)
            };

            using var response = await httpClient.SendAsync(request, cancellationToken);
            statusCode = response.StatusCode;
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            maskedResponse = MaskResponseForLogging(endpointName, responseText);
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
                return BuildPlatformFailureResult<T>(
                    endpointName,
                    requestUrl,
                    response.StatusCode,
                    platformCode,
                    platformMessage,
                    maskedResponse,
                    $"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }

            if (!IsSuccessCode(platformCode))
            {
                return BuildPlatformFailureResult<T>(
                    endpointName,
                    requestUrl,
                    response.StatusCode,
                    platformCode,
                    platformMessage,
                    maskedResponse,
                    platformMessage ?? $"Platform returned code {platformCode}");
            }

            var payloadElement = ExtractPayloadElement(root, endpointName);
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
        catch (PayloadProcessingException exception)
        {
            stopwatch.Stop();
            LogApiAccess(
                endpointName,
                requestUrl,
                statusCode,
                stopwatch.ElapsedMilliseconds,
                maskedRequest,
                maskedHeaders,
                maskedResponse,
                exception.LogSummary);

            return Fail<T>(endpointName, requestUrl, exception.UserMessage);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            LogApiAccess(
                endpointName,
                requestUrl,
                statusCode,
                stopwatch.ElapsedMilliseconds,
                maskedRequest,
                maskedHeaders,
                maskedResponse,
                exception.Message);

            return Fail<T>(endpointName, requestUrl, exception.Message);
        }
    }

    private JsonElement ExtractPayloadElement(JsonElement root, string endpointName)
    {
        if (!root.TryGetProperty("data", out var payloadElement))
        {
            if (IsPreviewEndpoint(endpointName))
            {
                throw new PayloadProcessingException("RTSP 接口业务失败", "RTSP 接口返回缺少 data 字段。");
            }

            throw new InvalidOperationException("Response does not contain data.");
        }

        if (IsPreviewEndpoint(endpointName))
        {
            return ExtractPreviewPayloadElement(payloadElement);
        }

        return ExtractGenericPayloadElement(payloadElement);
    }

    private JsonElement ExtractGenericPayloadElement(JsonElement payloadElement)
    {
        if (payloadElement.ValueKind == JsonValueKind.String)
        {
            var dataText = payloadElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dataText))
            {
                return payloadElement.Clone();
            }

            var decrypted = DecryptResponseDataStrict(dataText);
            using var decryptedDocument = JsonDocument.Parse(decrypted);
            return decryptedDocument.RootElement.Clone();
        }

        return payloadElement.Clone();
    }

    private JsonElement ExtractPreviewPayloadElement(JsonElement payloadElement)
    {
        if (payloadElement.ValueKind != JsonValueKind.String)
        {
            throw new PayloadProcessingException(
                "RTSP 接口业务失败",
                $"RTSP 响应 data 类型不支持：{payloadElement.ValueKind}。");
        }

        var decryptedJson = DecryptPreviewPayload(payloadElement.GetString() ?? string.Empty);
        try
        {
            using var document = JsonDocument.Parse(decryptedJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PayloadProcessingException(
                    "RTSP 解密后 JSON 解析失败",
                    $"RTSP 解密后根节点类型不是 JSON object，而是 {document.RootElement.ValueKind}。");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new PayloadProcessingException(
                "RTSP 解密后 JSON 解析失败",
                $"RTSP 解密后 JSON 解析失败：{exception.Message}",
                exception);
        }
    }

    private string DecryptPreviewPayload(string encryptedPayload)
    {
        if (string.IsNullOrWhiteSpace(encryptedPayload))
        {
            throw new PayloadProcessingException("RTSP 响应解密失败", "RTSP 响应 data 为空。");
        }

        try
        {
            return DecryptPreviewResponseDataStrict(encryptedPayload);
        }
        catch (RsaDecryptException exception)
        {
            throw new PayloadProcessingException(
                "RTSP 响应解密失败",
                exception.StageSummary,
                exception);
        }
        catch (PayloadProcessingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new PayloadProcessingException(
                "RTSP 响应解密失败",
                $"RTSP 响应解密失败：{exception.Message}",
                exception);
        }
    }

    private string DecryptResponseDataStrict(string encryptedPayload)
    {
        if (string.IsNullOrWhiteSpace(options.Version))
        {
            throw new InvalidOperationException("开放平台 version 未配置。");
        }

        return options.Version switch
        {
            "1.1" => RsaDecryptor.Decrypt(encryptedPayload, options.RsaPrivateKey).PlainText,
            "v1.0" => XxTea.DecryptFromHex(encryptedPayload, options.AppSecret),
            _ => throw new InvalidOperationException($"不支持的开放平台 version：{options.Version}")
        };
    }

    private string DecryptPreviewResponseDataStrict(string encryptedPayload)
    {
        if (string.IsNullOrWhiteSpace(options.Version))
        {
            throw new InvalidOperationException("开放平台 version 未配置。");
        }

        return options.Version switch
        {
            "1.1" => DecryptPreviewResponseDataWithRsa(encryptedPayload),
            "v1.0" => XxTea.DecryptFromHex(encryptedPayload, options.AppSecret),
            _ => throw new InvalidOperationException($"不支持的开放平台 version：{options.Version}")
        };
    }

    private string DecryptPreviewResponseDataWithRsa(string encryptedPayload)
    {
        if (!RsaDecryptor.TryGetCipherEncoding(encryptedPayload, out var cipherEncoding))
        {
            logger.LogWarning("RTSP data 输入编码识别：无法识别输入编码");
            throw new PayloadProcessingException(
                "RTSP 响应解密失败",
                "RTSP data 输入编码识别：无法识别输入编码。");
        }

        logger.LogInformation("RTSP data 输入编码识别：识别为 {CipherEncoding}", FormatCipherEncoding(cipherEncoding));

        var decryptResult = RsaDecryptor.Decrypt(encryptedPayload, options.RsaPrivateKey);
        logger.LogInformation("RTSP RSA 私钥导入：已按 {PrivateKeyFormat} 导入", FormatPrivateKeyFormat(decryptResult.PrivateKeyFormat));
        logger.LogInformation("RTSP RSA 解密：已按 {DecryptMode} 完成", FormatDecryptMode(decryptResult.DecryptMode));
        return decryptResult.PlainText;
    }

    private static string ComputeSignature(string source, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash);
    }

    private List<KeyValuePair<string, string?>> CreateAuthorizedParameters(string accessToken)
    {
        var parameters = new List<KeyValuePair<string, string?>>
        {
            new("accessToken", accessToken),
            new("enterpriseUser", options.EnterpriseUser)
        };

        if (!string.IsNullOrWhiteSpace(options.ParentUser))
        {
            parameters.Add(new("parentUser", options.ParentUser));
        }

        return parameters;
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

    private static bool IsPreviewEndpoint(string endpointName)
    {
        return string.Equals(endpointName, "getDeviceMediaUrlRtsp", StringComparison.Ordinal);
    }

    private static string MaskResponseForLogging(string endpointName, string responseText)
    {
        return IsPreviewEndpoint(endpointName)
            ? SensitiveDataMasker.MaskJson(responseText, "data")
            : SensitiveDataMasker.MaskJson(responseText);
    }

    private static string FormatCipherEncoding(RsaCipherEncoding cipherEncoding)
    {
        return cipherEncoding switch
        {
            RsaCipherEncoding.Hex => "Hex",
            RsaCipherEncoding.Base64 => "Base64",
            _ => "Unknown"
        };
    }

    private static string FormatPrivateKeyFormat(RsaPrivateKeyFormat privateKeyFormat)
    {
        return privateKeyFormat switch
        {
            RsaPrivateKeyFormat.Pkcs8 => "PKCS#8",
            RsaPrivateKeyFormat.Pkcs1 => "PKCS#1/RSA",
            _ => "Unknown"
        };
    }

    private static string FormatDecryptMode(RsaDecryptMode decryptMode)
    {
        return decryptMode switch
        {
            RsaDecryptMode.SingleBlock => "单段",
            RsaDecryptMode.Chunked => "分段",
            _ => "未知方式"
        };
    }

    private static OpenPlatformCallResult<T> BuildPlatformFailureResult<T>(
        string endpointName,
        string requestUrl,
        HttpStatusCode? statusCode,
        string? platformCode,
        string? platformMessage,
        string maskedResponse,
        string defaultMessage)
    {
        return new OpenPlatformCallResult<T>
        {
            Success = false,
            EndpointName = endpointName,
            RequestUrl = requestUrl,
            HttpStatusCode = statusCode,
            PlatformCode = platformCode,
            PlatformMessage = platformMessage,
            MaskedResponse = maskedResponse,
            ErrorMessage = IsPreviewEndpoint(endpointName)
                ? "RTSP 接口业务失败"
                : defaultMessage
        };
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

    private static IReadOnlyList<OpenPlatformRegionDto> ParseRegionList(JsonElement root)
    {
        var arrayElement = ExtractSingleArrayPayload(root, "getReginWithGroupList");
        return JsonSerializer.Deserialize<List<OpenPlatformRegionDto>>(arrayElement.GetRawText(), JsonSerializerOptions) ?? [];
    }

    private static OpenPlatformRegionDevicePageDto ParseRegionDevicePage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("getDeviceList 返回结构不是分页对象；文档未说明，需人工确认。");
        }

        if (!root.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("getDeviceList 返回结构缺少 list 数组；文档未说明，需人工确认。");
        }

        var items = JsonSerializer.Deserialize<List<OpenPlatformRegionDeviceDto>>(listElement.GetRawText(), JsonSerializerOptions) ?? [];
        var pageNo = GetInt32(root, "pageNo")
            ?? throw new InvalidOperationException("getDeviceList 返回结构缺少 pageNo；文档未说明，需人工确认。");
        var pageSize = GetInt32(root, "pageSize")
            ?? throw new InvalidOperationException("getDeviceList 返回结构缺少 pageSize；文档未说明，需人工确认。");
        var totalCount = GetInt32(root, "totalCount")
            ?? throw new InvalidOperationException("getDeviceList 返回结构缺少 totalCount；文档未说明，需人工确认。");

        return new OpenPlatformRegionDevicePageDto(items, pageNo, pageSize, totalCount);
    }

    private static IReadOnlyList<OpenPlatformRegionDeviceCountDto> ParseRegionDeviceCounts(JsonElement root)
    {
        var arrayElement = ExtractSingleArrayPayload(root, "getCusDeviceCount");
        return JsonSerializer.Deserialize<List<OpenPlatformRegionDeviceCountDto>>(arrayElement.GetRawText(), JsonSerializerOptions) ?? [];
    }

    private static OpenPlatformPreviewUrlPayload ParsePreviewUrl(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new PayloadProcessingException(
                "RTSP 解密后 JSON 解析失败",
                $"RTSP 解密后根节点类型不是 JSON object，而是 {root.ValueKind}。");
        }

        var url = GetString(root, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PayloadProcessingException(
                "RTSP 返回缺少 url",
                $"RTSP 返回缺少 url。payload={SensitiveDataMasker.MaskJson(root.GetRawText())}");
        }

        var expireTime = GetString(root, "expireTime");
        return new OpenPlatformPreviewUrlPayload(url, expireTime);
    }

    private static JsonElement ExtractSingleArrayPayload(JsonElement root, string endpointName)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.Clone();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{endpointName} 返回结构不是数组，也不是包含数组的对象；文档未说明，需人工确认。");
        }

        JsonElement? arrayElement = null;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (arrayElement is not null)
            {
                throw new InvalidOperationException($"{endpointName} 返回结构包含多个数组字段；文档未说明，需人工确认。");
            }

            arrayElement = property.Value.Clone();
        }

        if (arrayElement is null)
        {
            throw new InvalidOperationException($"{endpointName} 返回结构中未找到数组字段；文档未说明，需人工确认。");
        }

        return arrayElement.Value;
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

    private sealed class PayloadProcessingException : Exception
    {
        public PayloadProcessingException(string userMessage, string logSummary, Exception? innerException = null)
            : base(userMessage, innerException)
        {
            UserMessage = userMessage;
            LogSummary = logSummary;
        }

        public string UserMessage { get; }

        public string LogSummary { get; }
    }
}
