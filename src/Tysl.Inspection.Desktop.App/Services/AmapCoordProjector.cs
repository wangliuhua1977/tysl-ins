using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Wpf;
using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Contracts.Configuration;

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

        var candidateCount = items.Count(HasRawCoordinate);
        logger.LogInformation("BD-09 to GCJ-02 conversion started. CandidateCount={CandidateCount}.", candidateCount);

        if (candidateCount == 0)
        {
            var missingOnly = items.ToDictionary(
                item => item.DeviceCode,
                BuildMissingResult,
                StringComparer.OrdinalIgnoreCase);

            logger.LogInformation("BD-09 to GCJ-02 conversion completed. RenderedCount=0, FailedCount=0.");
            return missingOnly;
        }

        if (!mapOptions.HasJsKey())
        {
            logger.LogWarning("BD-09 to GCJ-02 conversion failed because AMap JS key is missing.");
            return BuildFallbackResults(items, "缺少高德地图 Key，无法执行坐标转换。");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var pointsJson = JsonSerializer.Serialize(
                items.Select(item => new
                {
                    deviceCode = item.DeviceCode,
                    rawLatitude = item.RawLatitude ?? string.Empty,
                    rawLongitude = item.RawLongitude ?? string.Empty,
                    hasRawCoordinate = HasRawCoordinate(item)
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

            var results = items.ToDictionary(
                item => item.DeviceCode,
                item => payloadByDeviceCode.TryGetValue(item.DeviceCode, out var payload)
                    ? ToResult(item, payload)
                    : BuildFailedResult(item, "坐标转换失败，需人工确认。"),
                StringComparer.OrdinalIgnoreCase);

            logger.LogInformation(
                "BD-09 to GCJ-02 conversion completed. RenderedCount={RenderedCount}, FailedCount={FailedCount}.",
                results.Values.Count(item => item.HasMapCoordinate),
                results.Values.Count(item => item.CoordinateState == "failed"));

            return results;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "BD-09 to GCJ-02 conversion failed unexpectedly.");
            return BuildFallbackResults(items, "坐标转换失败，需人工确认。");
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
        return new CoordinateProjectionResult(
            request.DeviceCode,
            payload.HasRawCoordinate,
            payload.HasMapCoordinate,
            payload.CoordinateState ?? string.Empty,
            payload.CoordinateStateText ?? string.Empty,
            payload.CoordinateWarning ?? string.Empty,
            payload.MapLatitude,
            payload.MapLongitude);
    }

    private static IReadOnlyDictionary<string, CoordinateProjectionResult> BuildFallbackResults(
        IReadOnlyCollection<CoordinateProjectionRequest> requests,
        string warning)
    {
        return requests.ToDictionary(
            item => item.DeviceCode,
            item => HasRawCoordinate(item)
                ? BuildFailedResult(item, warning)
                : BuildMissingResult(item),
            StringComparer.OrdinalIgnoreCase);
    }

    private static CoordinateProjectionResult BuildMissingResult(CoordinateProjectionRequest request)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            false,
            false,
            "missing",
            "平台未提供坐标",
            "平台未提供坐标，当前不进入上图。",
            null,
            null);
    }

    private static CoordinateProjectionResult BuildFailedResult(CoordinateProjectionRequest request, string warning)
    {
        return new CoordinateProjectionResult(
            request.DeviceCode,
            true,
            false,
            "failed",
            "坐标转换失败，需人工确认",
            warning,
            null,
            null);
    }

    private static bool HasRawCoordinate(CoordinateProjectionRequest request)
    {
        return double.TryParse(request.RawLatitude, out _)
            && double.TryParse(request.RawLongitude, out _);
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
