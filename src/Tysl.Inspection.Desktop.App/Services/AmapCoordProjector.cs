using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
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
    private const string ReadyProtocolType = "coord-conv-ready";
    private const string ReadyProtocolVersion = "1.0";
    private const string BatchFunctionName = "convertBd09Batch";
    private const int MaxBatchSize = 40;
    private const int PayloadSummaryLimit = 300;
    private static readonly TimeSpan HostReadyTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim gate = new(1, 1);
    private System.Windows.Window? hostWindow;
    private WebView2? webView;
    private TaskCompletionSource<bool>? hostReadyTcs;
    private bool hostReady;

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
            logger.LogInformation(
                "Coordinate raw parse started. DeviceCode={DeviceCode}, RawLongitude={RawLongitude}, RawLatitude={RawLatitude}, SourceSystem={SourceSystem}.",
                item.DeviceCode,
                item.RawLongitude ?? "null",
                item.RawLatitude ?? "null",
                "bd09");

            if (HasReusableCachedMapCoordinate(item))
            {
                results[item.DeviceCode] = BuildCachedMapResult(item);
                cachedRenderCount++;
                logger.LogInformation(
                    "Coordinate raw parse completed. DeviceCode={DeviceCode}, ParseResult={ParseResult}, Status={Status}, Diagnostics={Diagnostics}.",
                    item.DeviceCode,
                    "cached_map_coordinate",
                    CoordinateStateCatalog.Available,
                    "Reused cached GCJ-02 map coordinate.");
                continue;
            }

            var analysis = AnalyzeRawCoordinate(item);
            switch (analysis.Outcome)
            {
                case RawCoordinateOutcome.Candidate:
                    conversionCandidates.Add(item);
                    break;
                case RawCoordinateOutcome.Missing:
                    results[item.DeviceCode] = BuildMissingResult(item, analysis.Warning);
                    break;
                default:
                    results[item.DeviceCode] = BuildFailedResult(item, analysis.Warning, analysis.StateText);
                    break;
            }

            logger.LogInformation(
                "Coordinate raw parse completed. DeviceCode={DeviceCode}, ParseResult={ParseResult}, Status={Status}, Diagnostics={Diagnostics}.",
                item.DeviceCode,
                analysis.ParseResult,
                analysis.Status,
                analysis.Diagnostics);
        }

        logger.LogInformation(
            "BD-09 to GCJ-02 projection started. TotalCount={TotalCount}, CachedRenderCount={CachedRenderCount}, ConversionCandidateCount={ConversionCandidateCount}.",
            items.Length,
            cachedRenderCount,
            conversionCandidates.Count);

        if (conversionCandidates.Count == 0)
        {
            logger.LogInformation(
                "BD-09 to GCJ-02 projection completed. RenderedCount={RenderedCount}, FailedCount={FailedCount}, CachedRenderCount={CachedRenderCount}.",
                results.Values.Count(item => item.HasMapCoordinate),
                results.Values.Count(item => string.Equals(item.CoordinateState, CoordinateStateCatalog.Failed, StringComparison.OrdinalIgnoreCase)),
                cachedRenderCount);
            return results;
        }

        if (!mapOptions.HasJsKey())
        {
            logger.LogWarning("BD-09 to GCJ-02 projection failed because AMap JS key is missing.");
            foreach (var item in conversionCandidates)
            {
                results[item.DeviceCode] = BuildFailedResult(item, "Missing AMap JS key.", "高德脚本加载失败");
            }

            return results;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var configJson = JsonSerializer.Serialize(
                new
                {
                    jsKey = mapOptions.JsKey,
                    securityJsCode = mapOptions.SecurityJsCode,
                    jsApiVersion = string.IsNullOrWhiteSpace(mapOptions.JsApiVersion) ? "2.0" : mapOptions.JsApiVersion,
                    rawCoordinateSystem = "baidu"
                },
                JsonOptions);

            await EnsureHostReadyAsync(configJson, cancellationToken);

            var batches = conversionCandidates.Chunk(MaxBatchSize).ToArray();
            foreach (var batch in batches.Select((value, index) => (Items: value, Index: index + 1)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batchId = $"coord-batch-{Guid.NewGuid():N}";
                var startedAt = DateTimeOffset.Now;
                var stopwatch = Stopwatch.StartNew();
                var skippedCount = results.Count - cachedRenderCount;
                logger.LogInformation(
                    "AMap JS projection candidate batch built. BatchId={BatchId}, TotalCount={TotalCount}, DirectRenderableCount={DirectRenderableCount}, BaiduCandidateCount={BaiduCandidateCount}, SkippedCount={SkippedCount}, BatchIndex={BatchIndex}, BatchSize={BatchSize}, BatchDeviceCodes={BatchDeviceCodes}.",
                    batchId,
                    items.Length,
                    cachedRenderCount,
                    conversionCandidates.Count,
                    skippedCount,
                    batch.Index,
                    batch.Items.Length,
                    string.Join(", ", batch.Items.Select(item => item.DeviceCode)));
                foreach (var batchItem in batch.Items)
                {
                    logger.LogInformation(
                        "AMap JS projection request item. BatchId={BatchId}, DeviceCode={DeviceCode}, RawLongitude={RawLongitude}, RawLatitude={RawLatitude}, MapSource={MapSource}, CoordinateState={CoordinateState}.",
                        batchId,
                        batchItem.DeviceCode,
                        batchItem.RawLongitude ?? "null",
                        batchItem.RawLatitude ?? "null",
                        "amap_js_convert_from_baidu",
                        CoordinateStateCatalog.Available);
                }

                var requestJson = JsonSerializer.Serialize(
                    new
                    {
                        batchId,
                        items = batch.Items.Select(item => new
                        {
                            deviceCode = item.DeviceCode,
                            rawLatitude = item.RawLatitude ?? string.Empty,
                            rawLongitude = item.RawLongitude ?? string.Empty,
                            mapSource = "amap_js_convert_from_baidu",
                            coordinateState = CoordinateStateCatalog.Available
                        })
                    },
                    JsonOptions);
                var script = $$"""
                    (async () => {
                        return await window.convertBd09Batch({{requestJson}}, {{configJson}});
                    })()
                    """;

                logger.LogInformation(
                    "AMap JS projection request dispatched. BatchId={BatchId}, BatchIndex={BatchIndex}, CandidateCount={CandidateCount}, FunctionName={FunctionName}, StartedAt={StartedAt}, RawScriptLength={RawScriptLength}.",
                    batchId,
                    batch.Index,
                    batch.Items.Length,
                    BatchFunctionName,
                    startedAt,
                    script.Length);

                string responseJson;
                try
                {
                    responseJson = await ExecuteScriptAsync(script) ?? "null";
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "AMap JS projection ExecuteScriptAsync failed. BatchId={BatchId}, InvalidReason={InvalidReason}, FailureMessage={FailureMessage}.",
                        batchId,
                        "execute_script_failed",
                        exception.Message);

                    foreach (var item in batch.Items)
                    {
                        results[item.DeviceCode] = BuildFailedResult(item, "ExecuteScriptAsync failed.", "高德批量转换失败");
                    }

                    continue;
                }

                ScriptTransportInfo transportInfo;
                ProjectionBatchPayload batchPayload;
                try
                {
                    transportInfo = UnwrapExecuteScriptResult(responseJson!);
                    logger.LogInformation(
                        "WebView2 ExecuteScriptAsync payload received. BatchId={BatchId}, ResponseLength={ResponseLength}, RawSummary={RawSummary}, UnwrappedSummary={UnwrappedSummary}.",
                        batchId,
                        transportInfo.RawResponseLength,
                        transportInfo.RawSummary,
                        transportInfo.UnwrappedSummary);
                    batchPayload = ParseBatchPayload(transportInfo);
                }
                catch (Exception exception)
                {
                    var invalidReason = $"{ClassifyPayloadKind(responseJson)}:{exception.Message}";
                    var rawSummary = SummarizePayload(responseJson);
                    var unwrappedSummary = TrySummarizeUnwrappedPayload(responseJson);
                    logger.LogWarning(
                        "AMap JS projection payload invalid. BatchId={BatchId}, InvalidReason={InvalidReason}, RawPayloadSummary={RawPayloadSummary}, UnwrappedPayloadSummary={UnwrappedPayloadSummary}.",
                        batchId,
                        invalidReason,
                        rawSummary,
                        unwrappedSummary);

                    foreach (var item in batch.Items)
                    {
                        results[item.DeviceCode] = BuildFailedResult(item, "Invalid conversion payload returned from WebView2.", "高德批量转换失败");
                    }

                    continue;
                }

                logger.LogInformation(
                    "AMap JS projection envelope parsed. BatchId={BatchId}, Type={Type}, ProtocolVersion={ProtocolVersion}, RequestedCount={RequestedCount}, SuccessCount={SuccessCount}, FailedCount={FailedCount}, ItemCount={ItemCount}, ErrorCount={ErrorCount}.",
                    batchId,
                    batchPayload.Type,
                    batchPayload.ProtocolVersion,
                    batchPayload.RequestedCount,
                    batchPayload.SuccessCount,
                    batchPayload.FailedCount,
                    batchPayload.Items.Count,
                    batchPayload.ErrorCount);

                if (batchPayload.Errors.Count > 0)
                {
                    logger.LogWarning(
                        "AMap JS projection payload contains errors. BatchId={BatchId}, ErrorSummary={ErrorSummary}.",
                        batchId,
                        string.Join(" | ", batchPayload.Errors.Select(error => $"{error.Stage}:{error.ErrorCode}:{error.DeviceCode ?? "<none>"}:{error.Message}")));
                }

                var payloadByDeviceCode = batchPayload.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.DeviceCode))
                    .ToDictionary(item => item.DeviceCode!, StringComparer.OrdinalIgnoreCase);

                foreach (var item in batch.Items)
                {
                    if (!payloadByDeviceCode.TryGetValue(item.DeviceCode, out var payload))
                    {
                        results[item.DeviceCode] = BuildFailedResult(item, "The current device is missing from the WebView2 payload.", "地图未收到转换结果");
                        logger.LogWarning(
                            "AMap JS projection item parsed. BatchId={BatchId}, DeviceCode={DeviceCode}, RawLongitude={RawLongitude}, RawLatitude={RawLatitude}, MapLongitude={MapLongitude}, MapLatitude={MapLatitude}, CoordinateState={CoordinateState}, AmapStatus={AmapStatus}, ErrorStage={ErrorStage}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}.",
                            batchId,
                            item.DeviceCode,
                            item.RawLongitude ?? "null",
                            item.RawLatitude ?? "null",
                            "null",
                            "null",
                            CoordinateStateCatalog.Failed,
                            "error",
                            "payload-lookup",
                            "item_missing",
                            "The current device is missing from the WebView2 payload.");
                        continue;
                    }

                    var result = ToResult(item, payload);
                    results[item.DeviceCode] = result;
                    logger.LogInformation(
                        "AMap JS projection item parsed. BatchId={BatchId}, DeviceCode={DeviceCode}, RawLongitude={RawLongitude}, RawLatitude={RawLatitude}, MapLongitude={MapLongitude}, MapLatitude={MapLatitude}, CoordinateState={CoordinateState}, AmapStatus={AmapStatus}, ErrorStage={ErrorStage}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}.",
                        batchId,
                        item.DeviceCode,
                        payload.RawLongitude ?? item.RawLongitude ?? "null",
                        payload.RawLatitude ?? item.RawLatitude ?? "null",
                        result.MapLongitude ?? "null",
                        result.MapLatitude ?? "null",
                        result.CoordinateState,
                        payload.AmapStatus ?? string.Empty,
                        payload.ErrorStage ?? string.Empty,
                        payload.ErrorCode ?? string.Empty,
                        payload.ErrorMessage ?? string.Empty);
                }

                stopwatch.Stop();
                logger.LogInformation(
                    "AMap JS projection request completed. BatchId={BatchId}, BatchIndex={BatchIndex}, RequestedCount={RequestedCount}, SuccessCount={SuccessCount}, FailedCount={FailedCount}, DurationMilliseconds={DurationMilliseconds}, CompletedAt={CompletedAt}.",
                    batchId,
                    batch.Index,
                    batch.Items.Length,
                    batchPayload.SuccessCount,
                    batchPayload.FailedCount,
                    stopwatch.ElapsedMilliseconds,
                    DateTimeOffset.Now);
            }

            var resultStats = BuildProjectionStats(results.Values);
            logger.LogInformation(
                "BD-09 to GCJ-02 projection completed. RenderedCount={RenderedCount}, MissingCount={MissingCount}, RateLimitedCount={RateLimitedCount}, FailedCount={FailedCount}, CachedRenderCount={CachedRenderCount}, RenderedDeviceCodes={RenderedDeviceCodes}, UnmappedSummary={UnmappedSummary}.",
                resultStats.RenderedCount,
                resultStats.MissingCount,
                resultStats.RateLimitedCount,
                resultStats.FailedCount,
                cachedRenderCount,
                BuildDeviceCodeSummary(results.Where(pair => pair.Value.HasMapCoordinate).Select(pair => pair.Key)),
                BuildUnmappedSummary(results));

            return results;
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "BD-09 to GCJ-02 projection failed unexpectedly.");
            foreach (var item in conversionCandidates.Where(item => !results.ContainsKey(item.DeviceCode)))
            {
                results[item.DeviceCode] = BuildFailedResult(item, "CoordConv host ready timeout.", "高德宿主页未就绪");
            }

            return results;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "BD-09 to GCJ-02 projection failed unexpectedly.");
            foreach (var item in conversionCandidates.Where(item => !results.ContainsKey(item.DeviceCode)))
            {
                results[item.DeviceCode] = BuildFailedResult(item, "Coordinate conversion interrupted unexpectedly.", "坐标转换异常中断");
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
                if (webView?.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }

                webView?.Dispose();
                webView = null;
                hostWindow?.Close();
                hostWindow = null;
                return Task.CompletedTask;
            });
        }
    }

    internal static ProjectionBatchPayload ParseBatchPayload(string responseJson)
    {
        return ParseBatchPayload(UnwrapExecuteScriptResult(responseJson));
    }

    internal static HostReadyPayload ParseHostReadyPayload(string responseJson)
    {
        var transport = UnwrapExecuteScriptResult(responseJson);
        using var document = JsonDocument.Parse(transport.UnwrappedJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected a JSON object ready payload but received {root.ValueKind}.");
        }

        var type = ReadString(root, "type") ?? string.Empty;
        var version = ReadString(root, "protocolVersion") ?? string.Empty;
        if (!string.Equals(type, ReadyProtocolType, StringComparison.Ordinal) || !string.Equals(version, ReadyProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid ready protocol. Type={type}, ProtocolVersion={version}.");
        }

        if (!TryReadBool(root, "ready", out var ready, out var readyError))
        {
            throw new InvalidOperationException(readyError ?? "Ready payload is missing ready.");
        }

        return new HostReadyPayload(
            type,
            version,
            ready,
            ReadBool(root, "scriptLoaded"),
            ReadBool(root, "hasAmap"),
            ReadBool(root, "hasConvertFrom"),
            ReadString(root, "errorStage"),
            ReadString(root, "errorMessage"),
            transport.TransportRootKind,
            transport.RawResponseLength,
            transport.RawSummary,
            transport.UnwrappedSummary);
    }

    private static ProjectionBatchPayload ParseBatchPayload(ScriptTransportInfo transport)
    {
        using var document = JsonDocument.Parse(transport.UnwrappedJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected a JSON object envelope but received {root.ValueKind}.");
        }

        var type = ReadString(root, "type") ?? string.Empty;
        var version = ReadString(root, "protocolVersion") ?? string.Empty;
        if (!string.Equals(type, ProtocolType, StringComparison.Ordinal) || !string.Equals(version, ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid envelope protocol. Type={type}, ProtocolVersion={version}.");
        }

        if (!root.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Envelope does not contain a valid items array.");
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
            ReadInt(root, "errorCount"),
            transport.TransportRootKind,
            transport.RawResponseLength,
            transport.RawSummary,
            transport.UnwrappedSummary,
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
                $"transport={payload.TransportRootKind},type={payload.Type},protocolVersion={payload.ProtocolVersion},requested={payload.RequestedCount},items={payload.Items.Count},success={payload.SuccessCount},failed={payload.FailedCount},errors={payload.ErrorCount}");
        }
        catch
        {
            return SummarizePayload(responseJson);
        }
    }

    internal static string ClassifyPayloadKind(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return "empty_string";
        }

        var current = responseJson.Trim();
        for (var depth = 0; depth < 4; depth++)
        {
            if (string.Equals(current, "undefined", StringComparison.OrdinalIgnoreCase))
            {
                return "undefined";
            }

            if (string.Equals(current, "null", StringComparison.OrdinalIgnoreCase))
            {
                return "null";
            }

            try
            {
                using var document = JsonDocument.Parse(current);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    var inner = root.GetString();
                    if (string.IsNullOrWhiteSpace(inner))
                    {
                        return "empty_string";
                    }

                    current = inner.Trim();
                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    return root.EnumerateObject().Any() ? "object" : "empty_object";
                }

                return root.ValueKind.ToString().ToLowerInvariant();
            }
            catch
            {
                return "invalid_json";
            }
        }

        return "too_many_string_wrappers";
    }

    internal static ScriptTransportInfo UnwrapExecuteScriptResult(string responseJson)
    {
        var normalizedJson = NormalizeScriptResultJson(responseJson, out var transportRootKind);
        return new ScriptTransportInfo(
            responseJson,
            normalizedJson,
            transportRootKind,
            responseJson?.Length ?? 0,
            SummarizePayload(responseJson),
            SummarizePayload(normalizedJson));
    }

    internal static string TrySummarizeUnwrappedPayload(string responseJson)
    {
        try
        {
            return UnwrapExecuteScriptResult(responseJson).UnwrappedSummary;
        }
        catch (Exception exception)
        {
            return $"<unwrap-failed:{exception.Message}>";
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
                throw new FileNotFoundException("CoordConvHost.html not found.", htmlPath);
            }

            logger.LogInformation(
                "CoordConv host load started. HtmlPath={HtmlPath}, HasJsKey={HasJsKey}, HasSecurityJsCode={HasSecurityJsCode}.",
                htmlPath,
                mapOptions.HasJsKey(),
                !string.IsNullOrWhiteSpace(mapOptions.SecurityJsCode));

            var navigationTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var localWebView = new WebView2();
            localWebView.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    navigationTcs.TrySetResult();
                    return;
                }

                navigationTcs.TrySetException(new InvalidOperationException($"CoordConvHost navigation failed: {args.WebErrorStatus}."));
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
            localWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            localWebView.Source = new Uri(htmlPath);
            await navigationTcs.Task.WaitAsync(cancellationToken);
            window.Hide();

            logger.LogInformation("CoordConv host load completed. HtmlPath={HtmlPath}.", htmlPath);

            hostReady = false;
            hostWindow = window;
            webView = localWebView;
        });
    }

    private async Task EnsureHostReadyAsync(string configJson, CancellationToken cancellationToken)
    {
        if (hostReady)
        {
            return;
        }

        hostReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readySignal = hostReadyTcs;
        logger.LogInformation(
            "CoordConv host ready wait started. TimeoutMilliseconds={TimeoutMilliseconds}.",
            (int)HostReadyTimeout.TotalMilliseconds);

        var script = $$"""
            (async () => {
                return await window.__coordConvHost.ensureReady({{configJson}});
            })()
            """;
        var responseJson = await ExecuteScriptAsync(script);
        var readyPayload = ParseHostReadyPayload(responseJson);
        logger.LogInformation(
            "CoordConv host ready payload parsed. Type={Type}, ProtocolVersion={ProtocolVersion}, Ready={Ready}, ScriptLoaded={ScriptLoaded}, HasAmap={HasAmap}, HasConvertFrom={HasConvertFrom}, RawPayloadSummary={RawPayloadSummary}, UnwrappedPayloadSummary={UnwrappedPayloadSummary}.",
            readyPayload.Type,
            readyPayload.ProtocolVersion,
            readyPayload.Ready,
            readyPayload.ScriptLoaded,
            readyPayload.HasAmap,
            readyPayload.HasConvertFrom,
            readyPayload.RawSummary,
            readyPayload.UnwrappedSummary);

        if (!readyPayload.Ready)
        {
            throw new InvalidOperationException(readyPayload.ErrorMessage ?? "CoordConv host initialization failed.");
        }

        hostReady = true;
        hostReadyTcs?.TrySetResult(true);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HostReadyTimeout);
        try
        {
            await readySignal.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "CoordConv host ready timeout. TimeoutMilliseconds={TimeoutMilliseconds}.",
                (int)HostReadyTimeout.TotalMilliseconds);
            throw new TimeoutException("CoordConv host ready timeout.");
        }
        finally
        {
            hostReadyTcs = null;
        }
    }

    private async Task<string> ExecuteScriptAsync(string script)
    {
        return await RunOnUiAsync(async () =>
        {
            if (webView is null)
            {
                throw new InvalidOperationException("CoordConv host WebView2 is not initialized.");
            }

            return await webView.ExecuteScriptAsync(script);
        });
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var type = ReadString(root, "type") ?? string.Empty;
            switch (type)
            {
                case "coord-conv-ready":
                    hostReady = true;
                    logger.LogInformation(
                        "CoordConv host ready received. Ready={Ready}, ScriptLoaded={ScriptLoaded}, HasAmap={HasAmap}, HasConvertFrom={HasConvertFrom}.",
                        true,
                        ReadString(root, "scriptLoaded") ?? "true",
                        ReadString(root, "hasAmap") ?? "true",
                        ReadString(root, "hasConvertFrom") ?? "true");
                    hostReadyTcs?.TrySetResult(true);
                    break;
                case "coord-conv-ready-failed":
                {
                    hostReady = false;
                    var message = ReadString(root, "errorMessage") ?? "CoordConv host initialization failed.";
                    logger.LogWarning(
                        "CoordConv host ready failed. ErrorStage={ErrorStage}, Message={Message}.",
                        ReadString(root, "errorStage") ?? "script_load",
                        message);
                    hostReadyTcs?.TrySetException(new InvalidOperationException(message));
                    break;
                }
                case "coord-conv-log":
                    LogHostMessage(root);
                    break;
                case "coord-conv-host-loaded":
                    logger.LogInformation("CoordConv host page loaded. Loaded={Loaded}.", ReadString(root, "loaded") ?? "true");
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to parse CoordConv host WebMessage payload.");
        }
    }

    private void LogHostMessage(JsonElement root)
    {
        var level = ReadString(root, "level") ?? "info";
        var message = ReadString(root, "message") ?? "CoordConv host log";
        var summary = string.Join(", ", EnumerateHostLogFields(root).Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => $"{pair.Key}={pair.Value}"));

        switch (level.ToLowerInvariant())
        {
            case "error":
                logger.LogError("{Message}. {Summary}", message, summary);
                break;
            case "warning":
            case "warn":
                logger.LogWarning("{Message}. {Summary}", message, summary);
                break;
            default:
                logger.LogInformation("{Message}. {Summary}", message, summary);
                break;
        }
    }

    private static IEnumerable<KeyValuePair<string, string?>> EnumerateHostLogFields(JsonElement root)
    {
        string[] propertyNames =
        [
            "deviceCode",
            "batchId",
            "batchIndex",
            "requestedCount",
            "successCount",
            "failedCount",
            "functionName",
            "startedAt",
            "durationMs",
            "rawLongitude",
            "rawLatitude",
            "mapLongitude",
            "mapLatitude",
            "mapSource",
            "coordinateState",
            "status",
            "info",
            "locationsKind",
            "locationCount",
            "resultLocationKind",
            "amapStatus",
            "amapInfo",
            "errorStage",
            "errorCode",
            "errorMessage",
            "jsApiVersion",
            "hasJsKey",
            "hasSecurityJsCode",
            "hasAmap",
            "hasConvertFrom"
        ];

        foreach (var propertyName in propertyNames)
        {
            yield return new KeyValuePair<string, string?>(propertyName, ReadString(root, propertyName));
        }
    }

    private static CoordinateProjectionResult ToResult(CoordinateProjectionRequest request, ProjectionPayload payload)
    {
        if (payload.HasMapCoordinate)
        {
            if (!TryReadLatitude(payload.MapLatitude, out _) || !TryReadLongitude(payload.MapLongitude, out _))
            {
                return BuildFailedResult(request, BuildInvalidMapCoordinateMessage(payload), "C# 坐标字段非法");
            }

            return new CoordinateProjectionResult(
                request.DeviceCode,
                true,
                true,
                CoordinateStateCatalog.Available,
                string.IsNullOrWhiteSpace(payload.CoordinateStateText) ? CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, true) : payload.CoordinateStateText,
                string.IsNullOrWhiteSpace(payload.CoordinateWarning) ? CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Available, null, true) : payload.CoordinateWarning,
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
                string.IsNullOrWhiteSpace(payload.CoordinateStateText) ? CoordinateStateCatalog.GetStateText(state, false) : payload.CoordinateStateText,
                string.IsNullOrWhiteSpace(payload.CoordinateWarning) ? CoordinateStateCatalog.GetWarningText(state, payload.ErrorMessage, false) : payload.CoordinateWarning,
                null,
                null);
        }

        var warning = !string.IsNullOrWhiteSpace(payload.CoordinateWarning)
            ? payload.CoordinateWarning
            : !string.IsNullOrWhiteSpace(payload.ErrorMessage)
                ? payload.ErrorMessage
                : "Coordinate conversion or parsing failed.";
        var stateText = !string.IsNullOrWhiteSpace(payload.CoordinateStateText) ? payload.CoordinateStateText : "坐标转换或解析失败";
        return BuildFailedResult(request, warning, stateText);
    }

    private static ProjectionStats BuildProjectionStats(IEnumerable<CoordinateProjectionResult> results)
    {
        var renderedCount = 0;
        var missingCount = 0;
        var rateLimitedCount = 0;
        var failedCount = 0;

        foreach (var result in results)
        {
            if (result.HasMapCoordinate)
            {
                renderedCount++;
                continue;
            }

            switch (NormalizeState(result.CoordinateState))
            {
                case CoordinateStateCatalog.Missing:
                    missingCount++;
                    break;
                case CoordinateStateCatalog.RateLimited:
                    rateLimitedCount++;
                    break;
                default:
                    failedCount++;
                    break;
            }
        }

        return new ProjectionStats(renderedCount, missingCount, rateLimitedCount, failedCount);
    }

    private static string BuildDeviceCodeSummary(IEnumerable<string> deviceCodes)
    {
        var codes = deviceCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        return codes.Length == 0 ? "<none>" : string.Join(", ", codes);
    }

    private static string BuildUnmappedSummary(IReadOnlyDictionary<string, CoordinateProjectionResult> results)
    {
        var items = results
            .Where(pair => !pair.Value.HasMapCoordinate)
            .Select(pair => $"{pair.Key}:{pair.Value.CoordinateState}")
            .Take(20)
            .ToArray();

        return items.Length == 0 ? "<none>" : string.Join(" | ", items);
    }

    private static CoordinateProjectionResult BuildCachedMapResult(CoordinateProjectionRequest request)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            true,
            true,
            CoordinateStateCatalog.Available,
            CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, true),
            "Using cached GCJ-02 map coordinate.",
            request.CachedMapLatitude,
            request.CachedMapLongitude);
    }

    private static CoordinateProjectionResult BuildMissingResult(CoordinateProjectionRequest request, string? warning)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            false,
            false,
            CoordinateStateCatalog.Missing,
            CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Missing, false),
            string.IsNullOrWhiteSpace(warning) ? CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Missing, null, false) : warning,
            null,
            null);
    }

    private static CoordinateProjectionResult BuildFailedResult(CoordinateProjectionRequest request, string warning, string stateText = "坐标转换或解析失败")
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            HasRawCoordinate(request),
            false,
            CoordinateStateCatalog.Failed,
            stateText,
            warning,
            null,
            null);
    }

    private static RawCoordinateAnalysis AnalyzeRawCoordinate(CoordinateProjectionRequest request)
    {
        var hasLatitude = !string.IsNullOrWhiteSpace(request.RawLatitude);
        var hasLongitude = !string.IsNullOrWhiteSpace(request.RawLongitude);
        if (!hasLatitude && !hasLongitude)
        {
            return new RawCoordinateAnalysis(RawCoordinateOutcome.Missing, "missing", CoordinateStateCatalog.Missing, CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Missing, false), CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Missing, null, false), "Raw latitude and longitude are both empty.");
        }

        if (hasLatitude != hasLongitude)
        {
            return new RawCoordinateAnalysis(RawCoordinateOutcome.Failed, "half_filled", CoordinateStateCatalog.Failed, "原始坐标半填", "Only one of latitude or longitude is provided.", "Only one of latitude or longitude is provided.");
        }

        if (!TryReadLatitude(request.RawLatitude, out var latitude) || !TryReadLongitude(request.RawLongitude, out var longitude))
        {
            return new RawCoordinateAnalysis(RawCoordinateOutcome.Failed, "format_invalid", CoordinateStateCatalog.Failed, "原始坐标格式非法", "Raw coordinates are not valid numeric latitude/longitude values.", "Raw coordinates are not valid numeric latitude/longitude values.");
        }

        if (latitude == 0 && longitude == 0)
        {
            return new RawCoordinateAnalysis(RawCoordinateOutcome.Failed, "origin_zero", CoordinateStateCatalog.Failed, "原始坐标为原点", "Raw coordinates are the 0,0 origin.", "Raw coordinates are the 0,0 origin.");
        }

        return new RawCoordinateAnalysis(RawCoordinateOutcome.Candidate, "bd09_candidate", CoordinateStateCatalog.Available, CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, false), CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Available, null, false), "Validated raw BD-09 coordinate and queued for AMap JS conversion.");
    }

    private static bool HasRawCoordinate(CoordinateProjectionRequest request)
    {
        return TryReadCoordinate(request.RawLatitude, out _) && TryReadCoordinate(request.RawLongitude, out _);
    }

    private static bool HasReusableCachedMapCoordinate(CoordinateProjectionRequest request)
    {
        return TryReadCoordinate(request.RawLatitude, out _)
               && TryReadCoordinate(request.RawLongitude, out _)
               && TryReadCoordinate(request.CachedMapLatitude, out _)
               && TryReadCoordinate(request.CachedMapLongitude, out _)
               && string.Equals(request.CoordinateStatus, CoordinateStateCatalog.Available, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadCoordinate(string? value, out double coordinate)
    {
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out coordinate);
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
            CoordinateStateCatalog.Missing => CoordinateStateCatalog.Missing,
            CoordinateStateCatalog.RateLimited => CoordinateStateCatalog.RateLimited,
            CoordinateStateCatalog.Failed => CoordinateStateCatalog.Failed,
            _ => CoordinateStateCatalog.Failed
        };
    }

    private static string NormalizeScriptResultJson(string responseJson, out string transportRootKind)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            throw new InvalidOperationException("ExecuteScriptAsync returned an empty string.");
        }

        var current = responseJson.Trim();
        transportRootKind = JsonValueKind.Undefined.ToString();
        for (var depth = 0; depth < 4; depth++)
        {
            if (string.Equals(current, "undefined", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ExecuteScriptAsync returned undefined.");
            }

            if (string.Equals(current, "null", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ExecuteScriptAsync returned null.");
            }

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
                throw new InvalidOperationException("ExecuteScriptAsync returned an empty wrapped string.");
            }

            current = innerJson.Trim();
        }

        throw new InvalidOperationException("ExecuteScriptAsync returned too many nested string wrappers.");
    }

    private static List<ProjectionErrorPayload> ParseErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errorsElement) || errorsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var errors = new List<ProjectionErrorPayload>(errorsElement.GetArrayLength());
        foreach (var errorElement in errorsElement.EnumerateArray())
        {
            if (errorElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ProjectionErrorPayload("payload", null, null, "errors[] must contain objects."));
                continue;
            }

            errors.Add(new ProjectionErrorPayload(
                ReadStringAny(errorElement, "stage", "errorStage") ?? "payload",
                ReadString(errorElement, "deviceCode"),
                ReadStringAny(errorElement, "errorCode", "code"),
                ReadStringAny(errorElement, "message", "errorMessage") ?? "No detailed error message returned."));
        }

        return errors;
    }

    private static ProjectionPayload? ParseItem(JsonElement itemElement, List<ProjectionErrorPayload> errors)
    {
        if (itemElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ProjectionErrorPayload("item-parse", null, "payload_malformed", "items[] must contain objects."));
            return null;
        }

        var deviceCode = ReadString(itemElement, "deviceCode");
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            errors.Add(new ProjectionErrorPayload("item-parse", null, "device_code_missing", "items[] is missing deviceCode."));
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

        return new ProjectionPayload(
            deviceCode,
            hasRawCoordinate,
            hasMapCoordinate,
            ReadString(itemElement, "mapSource"),
            ReadString(itemElement, "coordinateState"),
            ReadString(itemElement, "coordinateStateText"),
            ReadString(itemElement, "coordinateWarning"),
            ReadString(itemElement, "rawLatitude"),
            ReadString(itemElement, "rawLongitude"),
            ReadString(itemElement, "mapLatitude"),
            ReadString(itemElement, "mapLongitude"),
            ReadString(itemElement, "amapStatus"),
            ReadString(itemElement, "amapInfo"),
            ReadString(itemElement, "errorStage"),
            ReadString(itemElement, "errorCode"),
            ReadString(itemElement, "errorMessage"),
            ReadString(itemElement, "rawResultSnippet"),
            ReadString(itemElement, "resultLocationKind"),
            ReadString(itemElement, "resultLongitude"),
            ReadString(itemElement, "resultLatitude"));
    }

    private static ProjectionPayload BuildMalformedItem(string deviceCode, string? errorMessage, List<ProjectionErrorPayload> errors)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage)
            ? "坐标转换回传解析失败。"
            : $"坐标转换回传解析失败：{errorMessage}";
        errors.Add(new ProjectionErrorPayload("item-parse", deviceCode, "payload_malformed", message));

        return new ProjectionPayload(
            deviceCode,
            true,
            false,
            null,
            CoordinateStateCatalog.Failed,
            "坐标转换回传解析失败",
            message,
            null,
            null,
            null,
            null,
            "error",
            null,
            "item-parse",
            "payload_malformed",
            message,
            null,
            null,
            null,
            null);
    }

    private static string BuildInvalidMapCoordinateMessage(ProjectionPayload payload)
    {
        return string.Create(CultureInfo.InvariantCulture, $"C# parsed invalid map coordinate values. mapLatitude={payload.MapLatitude ?? "null"}, mapLongitude={payload.MapLongitude ?? "null"}.");
    }

    private static string SummarizePayload(string? responseJson)
    {
        if (string.IsNullOrEmpty(responseJson))
        {
            return "<empty>";
        }

        var normalized = responseJson.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= PayloadSummaryLimit ? normalized : normalized[..PayloadSummaryLimit];
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

    private static string? ReadStringAny(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadString(element, propertyName);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
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

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool value, out string? errorMessage)
    {
        errorMessage = null;
        value = false;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            errorMessage = $"Missing {propertyName}.";
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

                errorMessage = $"{propertyName} is not a valid boolean string.";
                return false;
            case JsonValueKind.Number:
                if (property.TryGetInt32(out var number))
                {
                    value = number != 0;
                    return true;
                }

                errorMessage = $"{propertyName} is not a valid numeric boolean.";
                return false;
            default:
                errorMessage = $"{propertyName} has an invalid JSON kind: {property.ValueKind}.";
                return false;
        }
    }

    private static bool ReadBool(JsonElement element, string propertyName)
    {
        return TryReadBool(element, propertyName, out var value, out _) && value;
    }

    private static async Task RunOnUiAsync(Func<Task> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? throw new InvalidOperationException("WPF Dispatcher is not initialized.");
        var operation = dispatcher.InvokeAsync(action);
        var innerTask = await operation;
        await innerTask;
    }

    private static async Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? throw new InvalidOperationException("WPF Dispatcher is not initialized.");
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
        int ErrorCount,
        string TransportRootKind,
        int RawResponseLength,
        string RawSummary,
        string UnwrappedSummary,
        IReadOnlyList<ProjectionPayload> Items,
        IReadOnlyList<ProjectionErrorPayload> Errors);

    internal sealed record HostReadyPayload(
        string Type,
        string ProtocolVersion,
        bool Ready,
        bool ScriptLoaded,
        bool HasAmap,
        bool HasConvertFrom,
        string? ErrorStage,
        string? ErrorMessage,
        string TransportRootKind,
        int RawResponseLength,
        string RawSummary,
        string UnwrappedSummary);

    internal sealed record ScriptTransportInfo(
        string RawResponseJson,
        string UnwrappedJson,
        string TransportRootKind,
        int RawResponseLength,
        string RawSummary,
        string UnwrappedSummary);

    internal sealed record ProjectionErrorPayload(string Stage, string? DeviceCode, string? ErrorCode, string Message);

    internal sealed record ProjectionPayload(
        string? DeviceCode,
        bool HasRawCoordinate,
        bool HasMapCoordinate,
        string? MapSource,
        string? CoordinateState,
        string? CoordinateStateText,
        string? CoordinateWarning,
        string? RawLatitude,
        string? RawLongitude,
        string? MapLatitude,
        string? MapLongitude,
        string? AmapStatus,
        string? AmapInfo,
        string? ErrorStage,
        string? ErrorCode,
        string? ErrorMessage,
        string? RawResultSnippet,
        string? ResultLocationKind,
        string? ResultLongitude,
        string? ResultLatitude);

    private sealed record ProjectionStats(
        int RenderedCount,
        int MissingCount,
        int RateLimitedCount,
        int FailedCount);

    private enum RawCoordinateOutcome
    {
        Candidate,
        Missing,
        Failed
    }

    private sealed record RawCoordinateAnalysis(
        RawCoordinateOutcome Outcome,
        string ParseResult,
        string Status,
        string StateText,
        string Warning,
        string Diagnostics);
}
