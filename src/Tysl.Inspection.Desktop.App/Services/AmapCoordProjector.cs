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
                results[item.DeviceCode] = BuildFailedResult(item, "缺少高德地图 Key，无法执行坐标转换。");
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
            var responseJson = await ExecuteScriptAsync(script);
            var payloads = JsonSerializer.Deserialize<List<ProjectionPayload>>(responseJson, JsonOptions) ?? [];
            var payloadByDeviceCode = payloads
                .Where(item => !string.IsNullOrWhiteSpace(item.DeviceCode))
                .ToDictionary(item => item.DeviceCode!, StringComparer.OrdinalIgnoreCase);

            foreach (var item in conversionCandidates)
            {
                results[item.DeviceCode] = payloadByDeviceCode.TryGetValue(item.DeviceCode, out var payload)
                    ? ToResult(item, payload)
                    : BuildFailedResult(item, "坐标转换失败，需人工确认。");
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
                results[item.DeviceCode] = BuildFailedResult(item, "坐标转换失败，需人工确认。");
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

    private static CoordinateProjectionResult ToResult(CoordinateProjectionRequest request, ProjectionPayload payload)
    {
        if (payload.HasMapCoordinate)
        {
            return new CoordinateProjectionResult(
                request.DeviceCode,
                true,
                true,
                CoordinateStateCatalog.Available,
                CoordinateStateCatalog.GetStateText(CoordinateStateCatalog.Available, true),
                string.IsNullOrWhiteSpace(payload.CoordinateWarning)
                    ? CoordinateStateCatalog.GetWarningText(CoordinateStateCatalog.Available, null, true)
                    : payload.CoordinateWarning,
                payload.MapLatitude,
                payload.MapLongitude);
        }

        return BuildFailedResult(
            request,
            string.IsNullOrWhiteSpace(payload.CoordinateWarning)
                ? "坐标转换失败，需人工确认。"
                : payload.CoordinateWarning);
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

    private static CoordinateProjectionResult BuildFailedResult(CoordinateProjectionRequest request, string warning)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            true,
            false,
            CoordinateStateCatalog.Failed,
            "坐标转换失败，需人工确认",
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

    private sealed record ProjectionPayload(
        string? DeviceCode,
        bool HasRawCoordinate,
        bool HasMapCoordinate,
        string? CoordinateState,
        string? CoordinateStateText,
        string? CoordinateWarning,
        string? MapLatitude,
        string? MapLongitude);
}
