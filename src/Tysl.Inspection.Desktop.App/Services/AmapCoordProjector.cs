using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.App.Services;

public sealed class AmapCoordProjector(
    MapOptions mapOptions,
    ILogger<AmapCoordProjector> logger) : ICoordinateProjectionService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ProtocolType = "coord-conversion-batch";
    private const string ProtocolVersion = "1.0";

    private readonly SemaphoreSlim gate = new(1, 1);
    private System.Windows.Window? hostWindow;
    private WebView2? webView;

    public async Task<IReadOnlyDictionary<string, CoordinateProjectionResult>> ProjectBd09ToGcj02Async(
        IReadOnlyCollection<CoordinateProjectionRequest> requests,
        CancellationToken cancellationToken)
    {
        var items = requests.ToArray();
        if (items.Length == 0)
        {
            return new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase);
        }

        var results = new Dictionary<string, CoordinateProjectionResult>(StringComparer.OrdinalIgnoreCase);
        var conversionCandidates = new List<CoordinateProjectionRequest>(items.Length);
        var cachedRenderCount = 0;

        foreach (var item in items)
        {
            if (HasReusableCachedMapCoordinate(item))
            {
                results[item.DeviceCode] = BuildCachedMapResult(item);
                cachedRenderCount++;
                continue;
            }

            if (!HasRawCoordinate(item))
            {
                results[item.DeviceCode] = BuildStateOnlyResult(item);
                continue;
            }

            conversionCandidates.Add(item);
        }

        if (cachedRenderCount > 0)
        {
            logger.LogInformation(
                "Coordinate render cache hit. CachedRenderCount={CachedRenderCount}, DeviceCodes={DeviceCodes}.",
                cachedRenderCount,
                string.Join(", ", items.Where(HasReusableCachedMapCoordinate).Take(5).Select(item => item.DeviceCode)));
        }

        logger.LogInformation(
            "BD-09 to GCJ-02 projection started. TotalCount={TotalCount}, CachedRenderCount={CachedRenderCount}, ConversionCandidateCount={ConversionCandidateCount}.",
            items.Length,
            cachedRenderCount,
            conversionCandidates.Count);

        if (conversionCandidates.Count == 0)
        {
            logger.LogInformation(
                "BD-09 to GCJ-02 projection completed. RenderedCount={RenderedCount}, FailedCount=0, CachedRenderCount={CachedRenderCount}.",
                results.Values.Count(item => item.HasMapCoordinate),
                cachedRenderCount);
            return results;
        }

        if (!mapOptions.HasJsKey())
        {
            logger.LogWarning("BD-09 to GCJ-02 projection failed because AMap JS key is missing.");
            foreach (var item in conversionCandidates)
            {
                results[item.DeviceCode] = BuildFailedResult(item, "缺少高德地图 Key，无法执行坐标转换。", "高德 Key 缺失");
            }

            return results;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var pointsJson = JsonSerializer.Serialize(
                conversionCandidates.Select(item => new
                {
                    deviceCode = item.DeviceCode,
                    rawLatitude = item.RawLatitude ?? string.Empty,
                    rawLongitude = item.RawLongitude ?? string.Empty,
                    hasRawCoordinate = true
                }),
                JsonOptions);
            var configJson = JsonSerializer.Serialize(
                new
                {
                    jsKey = mapOptions.JsKey,
                    securityJsCode = mapOptions.SecurityJsCode,
                    jsApiVersion = string.IsNullOrWhiteSpace(mapOptions.JsApiVersion) ? "2.0" : mapOptions.JsApiVersion,
                    rawCoordinateSystem = "baidu"
                },
                JsonOptions);

            var script = $$"""
                (async () => {
                    return await window.coordConv.convertBatch({{pointsJson}}, {{configJson}});
                })()
                """;

            logger.LogInformation(
                "AMap JS projection request dispatched. CandidateCount={CandidateCount}, DeviceCodes={DeviceCodes}.",
                conversionCandidates.Count,
                string.Join(", ", conversionCandidates.Take(5).Select(item => item.DeviceCode)));
            logger.LogInformation(
                "AMap JS projection input summary. InputSummary={InputSummary}.",
                string.Join(
                    " | ",
                    conversionCandidates
                        .Take(5)
                        .Select(item => $"{item.DeviceCode}:{item.RawLatitude ?? "null"},{item.RawLongitude ?? "null"}")));

            var responseJson = await ExecuteScriptAsync(script);
            logger.LogInformation(
                "AMap JS projection raw payload received. ResponseLength={ResponseLength}, PayloadSummary={PayloadSummary}.",
                responseJson.Length,
                DescribeBatchPayloadSummary(responseJson));

            var batchPayload = ParseBatchPayload(responseJson);
            logger.LogInformation(
                "AMap JS projection payload parsed. ItemCount={ItemCount}, SuccessCount={SuccessCount}, FailedCount={FailedCount}, ErrorCount={ErrorCount}.",
                batchPayload.Items.Count,
                batchPayload.SuccessCount,
                batchPayload.FailedCount,
                batchPayload.Errors.Count);

            if (batchPayload.Errors.Count > 0)
            {
                logger.LogWarning(
                    "AMap JS projection payload contains errors. ErrorSummary={ErrorSummary}.",
                    string.Join(
                        " | ",
                        batchPayload.Errors
                            .Take(3)
                            .Select(error => $"{error.Stage}:{error.DeviceCode ?? "<none>"}:{error.Message}")));
            }

            var payloadByDeviceCode = batchPayload.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.DeviceCode))
                .ToDictionary(item => item.DeviceCode!, StringComparer.OrdinalIgnoreCase);

            foreach (var item in conversionCandidates)
            {
                CoordinateProjectionResult result;
                if (payloadByDeviceCode.TryGetValue(item.DeviceCode, out var payload))
                {
                    result = ToResult(item, payload);
                    logger.LogInformation(
                        "AMap JS projection item processed. DeviceCode={DeviceCode}, InputLatitude={InputLatitude}, InputLongitude={InputLongitude}, HasMapCoordinate={HasMapCoordinate}, CoordinateState={CoordinateState}, StateText={StateText}, Warning={Warning}, JsStatus={JsStatus}, JsInfo={JsInfo}, FailureStage={FailureStage}, FailureReasonCode={FailureReasonCode}, ResultLocationKind={ResultLocationKind}, ResultLatitude={ResultLatitude}, ResultLongitude={ResultLongitude}.",
                        item.DeviceCode,
                        payload.RawLatitude ?? item.RawLatitude ?? "null",
                        payload.RawLongitude ?? item.RawLongitude ?? "null",
                        result.HasMapCoordinate,
                        result.CoordinateState,
                        result.CoordinateStateText,
                        result.CoordinateWarning,
                        payload.ConversionStatus ?? string.Empty,
                        payload.ConversionInfo ?? string.Empty,
                        payload.FailureStage ?? string.Empty,
                        payload.FailureReasonCode ?? string.Empty,
                        payload.ResultLocationKind ?? string.Empty,
                        payload.ResultLatitude ?? string.Empty,
                        payload.ResultLongitude ?? string.Empty);
                }
                else
                {
                    result = BuildFailedResult(item, "地图未收到当前点位的转换结果。", "地图未收到转换结果");
                    logger.LogWarning(
                        "AMap JS projection item missing from payload. DeviceCode={DeviceCode}, InputLatitude={InputLatitude}, InputLongitude={InputLongitude}.",
                        item.DeviceCode,
                        item.RawLatitude ?? "null",
                        item.RawLongitude ?? "null");
                }

                results[item.DeviceCode] = result;
            }

            logger.LogInformation(
                "BD-09 to GCJ-02 projection completed. RenderedCount={RenderedCount}, FailedCount={FailedCount}, CachedRenderCount={CachedRenderCount}.",
                results.Values.Count(item => item.HasMapCoordinate),
                results.Values.Count(item => item.CoordinateState == CoordinateStateCatalog.Failed),
                cachedRenderCount);

            return results;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "BD-09 to GCJ-02 projection failed unexpectedly.");
            foreach (var item in conversionCandidates)
            {
                results[item.DeviceCode] = BuildFailedResult(item, "坐标转换异常中断，需人工确认。", "坐标转换异常中断");
            }

            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        gate.Dispose();

        if (webView is not null || hostWindow is not null)
        {
            _ = RunOnUiAsync(() =>
            {
                webView?.Dispose();
                webView = null;
                hostWindow?.Close();
                hostWindow = null;
                return Task.CompletedTask;
            });
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (webView is not null)
        {
            return;
        }

        await RunOnUiAsync(async () =>
        {
            if (webView is not null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CoordConvHost.html");
            if (!File.Exists(htmlPath))
            {
                throw new FileNotFoundException("坐标转换宿主页不存在。", htmlPath);
            }

            var navigationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var localWebView = new WebView2();
            localWebView.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    navigationTcs.TrySetResult();
                    return;
                }

                navigationTcs.TrySetException(new InvalidOperationException($"坐标转换宿主页加载失败：{args.WebErrorStatus}"));
            };

            var window = new System.Windows.Window
            {
                Width = 1,
                Height = 1,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                AllowsTransparency = true,
                Opacity = 0,
                Content = localWebView
            };

            window.Show();
            await localWebView.EnsureCoreWebView2Async();
            localWebView.Source = new Uri(htmlPath);
            await navigationTcs.Task.WaitAsync(cancellationToken);
            window.Hide();

            hostWindow = window;
            webView = localWebView;
        });
    }

    private async Task<string> ExecuteScriptAsync(string script)
    {
        var result = await RunOnUiAsync(async () =>
        {
            if (webView is null)
            {
                throw new InvalidOperationException("坐标转换宿主页尚未初始化。");
            }

            return await webView.ExecuteScriptAsync(script);
        });

        return result;
    }

    private static CoordinateProjectionResult ToResult(
        CoordinateProjectionRequest request,
        ProjectionPayload payload)
    {
        if (payload.HasMapCoordinate)
        {
            if (!TryReadLatitude(payload.MapLatitude, out _)
                || !TryReadLongitude(payload.MapLongitude, out _))
            {
                var warning = BuildInvalidMapCoordinateMessage(payload);
                return BuildFailedResult(request, warning, "C# 坐标字段非法");
            }

            return new CoordinateProjectionResult(
                request.DeviceCode,
                true,
                true,
                CoordinateStateCatalog.Available,
                string.IsNullOrWhiteSpace(payload.CoordinateStateText)
                    ? CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, true)
                    : payload.CoordinateStateText,
                string.IsNullOrWhiteSpace(payload.CoordinateWarning)
                    ? CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Available, null, true)
                    : payload.CoordinateWarning,
                payload.MapLatitude,
                payload.MapLongitude);
        }

        var state = NormalizeState(payload.CoordinateState);
        if (state == CoordinateStateCatalog.Missing || state == CoordinateStateCatalog.RateLimited)
        {
            return new CoordinateProjectionResult(
                request.DeviceCode,
                payload.HasRawCoordinate,
                false,
                state,
                string.IsNullOrWhiteSpace(payload.CoordinateStateText)
                    ? CoordinateStateCatalog.GetStateText(state, false)
                    : payload.CoordinateStateText,
                string.IsNullOrWhiteSpace(payload.CoordinateWarning)
                    ? CoordinateStateCatalog.GetWarningText(state, null, false)
                    : payload.CoordinateWarning,
                null,
                null);
        }

        return BuildFailedResult(
            request,
            string.IsNullOrWhiteSpace(payload.CoordinateWarning)
                ? "坐标转换或解析失败。"
                : payload.CoordinateWarning,
            string.IsNullOrWhiteSpace(payload.CoordinateStateText)
                ? "坐标转换或解析失败"
                : payload.CoordinateStateText);
    }

    internal static ProjectionBatchPayload ParseBatchPayload(string responseJson)
    {
        var normalizedJson = NormalizeScriptResultJson(responseJson, out var transportRootKind);
        using var document = JsonDocument.Parse(normalizedJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"高德坐标转换回传结构无效：期望对象，实际为 {root.ValueKind}。");
        }

        var type = ReadString(root, "type") ?? string.Empty;
        var version = ReadString(root, "protocolVersion") ?? string.Empty;
        if (!string.Equals(type, ProtocolType, StringComparison.Ordinal)
            || !string.Equals(version, ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"高德坐标转换回传协议无效：type={type}, protocolVersion={version}。");
        }

        if (!root.TryGetProperty("items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("高德坐标转换回传缺少 items 数组。");
        }

        var errors = ParseErrors(root);
        var items = new List<ProjectionPayload>(itemsElement.GetArrayLength());

        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            var parsedItem = ParseItem(itemElement, errors);
            if (parsedItem is not null)
            {
                items.Add(parsedItem);
            }
        }

        return new ProjectionBatchPayload(
            type,
            version,
            ReadInt(root, "requestedCount"),
            ReadInt(root, "successCount"),
            ReadInt(root, "failedCount"),
            ReadInt(root, "missingCount"),
            transportRootKind,
            items,
            errors);
    }

    internal static string DescribeBatchPayloadSummary(string responseJson)
    {
        try
        {
            var payload = ParseBatchPayload(responseJson);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"transport={payload.TransportRootKind},type={payload.Type},protocolVersion={payload.ProtocolVersion},requested={payload.RequestedCount},items={payload.Items.Count},success={payload.SuccessCount},failed={payload.FailedCount},missing={payload.MissingCount},errors={payload.Errors.Count}");
        }
        catch (Exception exception)
        {
            var snippet = responseJson.Length <= 240
                ? responseJson
                : responseJson[..240];
            return string.Create(
                CultureInfo.InvariantCulture,
                $"unparsed:{exception.Message};snippet={snippet}");
        }
    }

    private static CoordinateProjectionResult BuildCachedMapResult(CoordinateProjectionRequest request)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            true,
            true,
            CoordinateStateCatalog.Available,
            CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, true),
            "优先使用已缓存渲染坐标。",
            request.CachedMapLatitude,
            request.CachedMapLongitude);
    }

    private static CoordinateProjectionResult BuildStateOnlyResult(CoordinateProjectionRequest request)
    {
        var state = NormalizeState(request.CoordinateStatus);
        return new CoordinateProjectionResult(
            request.DeviceCode,
            false,
            false,
            state,
            CoordinateStateCatalog.GetStateText(state, false),
            CoordinateStateCatalog.GetWarningText(state, request.CoordinateStatusMessage, false),
            null,
            null);
    }

    private static CoordinateProjectionResult BuildFailedResult(
        CoordinateProjectionRequest request,
        string warning,
        string stateText = "坐标转换或解析失败")
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            true,
            false,
            CoordinateStateCatalog.Failed,
            stateText,
            warning,
            null,
            null);
    }

    private static bool HasRawCoordinate(CoordinateProjectionRequest request)
    {
        return TryReadCoordinate(request.RawLatitude, out _)
            && TryReadCoordinate(request.RawLongitude, out _);
    }

    private static bool HasReusableCachedMapCoordinate(CoordinateProjectionRequest request)
    {
        if (!HasRawCoordinate(request)
            || !TryReadCoordinate(request.CachedMapLatitude, out _)
            || !TryReadCoordinate(request.CachedMapLongitude, out _))
        {
            return false;
        }

        return !string.Equals(request.RawLatitude, request.CachedMapLatitude, StringComparison.Ordinal)
               || !string.Equals(request.RawLongitude, request.CachedMapLongitude, StringComparison.Ordinal);
    }

    private static bool TryReadCoordinate(string? value, out double coordinate)
    {
        return double.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out coordinate);
    }

    private static bool TryReadLatitude(string? value, out double coordinate)
    {
        if (!TryReadCoordinate(value, out coordinate))
        {
            return false;
        }

        return coordinate is >= -90 and <= 90;
    }

    private static bool TryReadLongitude(string? value, out double coordinate)
    {
        if (!TryReadCoordinate(value, out coordinate))
        {
            return false;
        }

        return coordinate is >= -180 and <= 180;
    }

    private static string NormalizeState(string? state)
    {
        return state switch
        {
            CoordinateStateCatalog.Available => CoordinateStateCatalog.Available,
            CoordinateStateCatalog.RateLimited => CoordinateStateCatalog.RateLimited,
            CoordinateStateCatalog.Failed => CoordinateStateCatalog.Failed,
            _ => CoordinateStateCatalog.Missing
        };
    }

    private static string NormalizeScriptResultJson(string responseJson, out string transportRootKind)
    {
        var current = responseJson;
        transportRootKind = JsonValueKind.Undefined.ToString();

        for (var depth = 0; depth < 3; depth++)
        {
            using var document = JsonDocument.Parse(current);
            if (depth == 0)
            {
                transportRootKind = document.RootElement.ValueKind.ToString();
            }

            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return current;
            }

            var innerJson = document.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(innerJson))
            {
                throw new InvalidOperationException("高德坐标转换回传为空字符串。");
            }

            current = innerJson;
        }

        throw new InvalidOperationException("高德坐标转换回传嵌套层级超出预期。");
    }

    private static List<ProjectionErrorPayload> ParseErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errorsElement)
            || errorsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var errors = new List<ProjectionErrorPayload>(errorsElement.GetArrayLength());
        foreach (var errorElement in errorsElement.EnumerateArray())
        {
            if (errorElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ProjectionErrorPayload("payload", null, $"errors[] 节点类型无效：{errorElement.ValueKind}。"));
                continue;
            }

            errors.Add(new ProjectionErrorPayload(
                ReadString(errorElement, "stage") ?? "payload",
                ReadString(errorElement, "deviceCode"),
                ReadString(errorElement, "message") ?? "未返回明确错误信息。"));
        }

        return errors;
    }

    private static ProjectionPayload? ParseItem(JsonElement itemElement, List<ProjectionErrorPayload> errors)
    {
        if (itemElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ProjectionErrorPayload("item-parse", null, $"items[] 节点类型无效：{itemElement.ValueKind}。"));
            return null;
        }

        var deviceCode = ReadString(itemElement, "deviceCode");
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            errors.Add(new ProjectionErrorPayload("item-parse", null, "items[] 缺少 deviceCode。"));
            return null;
        }

        if (!TryReadBool(itemElement, "hasRawCoordinate", out var hasRawCoordinate, out var rawError))
        {
            return BuildMalformedItem(deviceCode, rawError, errors);
        }

        if (!TryReadBool(itemElement, "hasMapCoordinate", out var hasMapCoordinate, out var mapError))
        {
            return BuildMalformedItem(deviceCode, mapError, errors);
        }

        var coordinateState = ReadString(itemElement, "coordinateState");
        var coordinateStateText = ReadString(itemElement, "coordinateStateText");
        var coordinateWarning = ReadString(itemElement, "coordinateWarning");
        var mapLatitude = ReadString(itemElement, "mapLatitude");
        var mapLongitude = ReadString(itemElement, "mapLongitude");
        var rawLatitude = ReadString(itemElement, "rawLatitude");
        var rawLongitude = ReadString(itemElement, "rawLongitude");
        var failureStage = ReadString(itemElement, "failureStage");
        var failureReasonCode = ReadString(itemElement, "failureReasonCode");
        var conversionStatus = ReadString(itemElement, "conversionStatus");
        var conversionInfo = ReadString(itemElement, "conversionInfo");
        var resultLocationKind = ReadString(itemElement, "resultLocationKind");
        var resultLongitude = ReadString(itemElement, "resultLongitude");
        var resultLatitude = ReadString(itemElement, "resultLatitude");

        return new ProjectionPayload(
            deviceCode,
            hasRawCoordinate,
            hasMapCoordinate,
            string.IsNullOrWhiteSpace(coordinateState) ? CoordinateStateCatalog.Failed : coordinateState,
            coordinateStateText,
            coordinateWarning,
            rawLatitude,
            rawLongitude,
            mapLatitude,
            mapLongitude,
            failureStage,
            failureReasonCode,
            conversionStatus,
            conversionInfo,
            resultLocationKind,
            resultLongitude,
            resultLatitude);
    }

    private static ProjectionPayload BuildMalformedItem(
        string deviceCode,
        string? errorMessage,
        List<ProjectionErrorPayload> errors)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? "坐标转换回传解析失败，需人工确认。"
            : $"坐标转换回传解析失败：{errorMessage}";
        errors.Add(new ProjectionErrorPayload("item-parse", deviceCode, message));
        return new ProjectionPayload(
            deviceCode,
            true,
            false,
            CoordinateStateCatalog.Failed,
            "坐标转换回传解析失败",
            message,
            null,
            null,
            null,
            null,
            "item-parse",
            "payload_malformed",
            null,
            null,
            null,
            null,
            null);
    }

    private static string BuildInvalidMapCoordinateMessage(ProjectionPayload payload)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"C# 解析后字段不合法：mapLatitude={payload.MapLatitude ?? "null"}, mapLongitude={payload.MapLongitude ?? "null"}。");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => value.GetRawText()
        };
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
               && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static bool TryReadBool(
        JsonElement element,
        string propertyName,
        out bool value,
        out string? errorMessage)
    {
        errorMessage = null;
        value = false;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            errorMessage = $"缺少 {propertyName}。";
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.String:
                if (bool.TryParse(property.GetString(), out value))
                {
                    return true;
                }

                errorMessage = $"{propertyName} 不是有效布尔值。";
                return false;
            case JsonValueKind.Number:
                if (property.TryGetInt32(out var number))
                {
                    value = number != 0;
                    return true;
                }

                errorMessage = $"{propertyName} 不是有效布尔值。";
                return false;
            default:
                errorMessage = $"{propertyName} 节点类型无效：{property.ValueKind}。";
                return false;
        }
    }

    private static async Task RunOnUiAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("当前应用未初始化 WPF Dispatcher。");
        var operation = dispatcher.InvokeAsync(action);
        var innerTask = await operation;
        await innerTask;
    }

    private static async Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("当前应用未初始化 WPF Dispatcher。");
        var operation = dispatcher.InvokeAsync(action);
        var innerTask = await operation;
        return await innerTask;
    }

    internal sealed record ProjectionBatchPayload(
        string Type,
        string ProtocolVersion,
        int RequestedCount,
        int SuccessCount,
        int FailedCount,
        int MissingCount,
        string TransportRootKind,
        IReadOnlyList<ProjectionPayload> Items,
        IReadOnlyList<ProjectionErrorPayload> Errors);

    internal sealed record ProjectionErrorPayload(
        string Stage,
        string? DeviceCode,
        string Message);

    internal sealed record ProjectionPayload(
        string? DeviceCode,
        bool HasRawCoordinate,
        bool HasMapCoordinate,
        string? CoordinateState,
        string? CoordinateStateText,
        string? CoordinateWarning,
        string? RawLatitude,
        string? RawLongitude,
        string? MapLatitude,
        string? MapLongitude,
        string? FailureStage,
        string? FailureReasonCode,
        string? ConversionStatus,
        string? ConversionInfo,
        string? ResultLocationKind,
        string? ResultLongitude,
        string? ResultLatitude);
}
