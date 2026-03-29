using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using System.IO;
using Tysl.Inspection.Desktop.App.ViewModels;

namespace Tysl.Inspection.Desktop.App.Views.Pages;

public partial class MapPageView : System.Windows.Controls.UserControl
{
    private bool hasLoaded;
    private bool webViewReady;
    private bool pageReady;
    private MapPageViewModel? viewModel;

    public MapPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;
        viewModel = DataContext as MapPageViewModel;

        try
        {
            if (viewModel is not null)
            {
                await viewModel.InitializeAsync();
            }

            await InitializeWebViewAsync();
        }
        catch (Exception exception)
        {
            viewModel?.ReportWebViewFailure(exception);
            MapHost.NavigateToString(BuildErrorHtml(exception.Message));
        }
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        viewModel = DataContext as MapPageViewModel;
        TrySendBootstrap();
    }

    private async Task InitializeWebViewAsync()
    {
        await MapHost.EnsureCoreWebView2Async();
        webViewReady = true;

        MapHost.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        MapHost.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

        var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MapHostPlaceholder.html");
        if (File.Exists(htmlPath))
        {
            MapHost.Source = new Uri(htmlPath);
            return;
        }

        MapHost.NavigateToString(BuildErrorHtml("地图页 HTML 文件不存在。"));
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            var message = $"WebView2 页面加载失败：{e.WebErrorStatus}";
            viewModel?.ReportWebViewFailure(new InvalidOperationException(message));
            MapHost.NavigateToString(BuildErrorHtml(message));
            return;
        }

        pageReady = true;
        TrySendBootstrap();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            if (!document.RootElement.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var messageType = typeElement.GetString();
            if (messageType is "ready" or "request-bootstrap")
            {
                pageReady = true;
                TrySendBootstrap();
                return;
            }

            if (messageType is "map-error")
            {
                var message = document.RootElement.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;

                viewModel?.ReportMapRenderFailure(message ?? "地图脚本加载失败。");
                return;
            }

            if (messageType is "coordinate-conversion-started")
            {
                var total = ReadInt(document.RootElement, "total");
                viewModel?.ReportCoordinateConversionStarted(total);
                return;
            }

            if (messageType is "coordinate-conversion-completed")
            {
                var renderedCount = ReadInt(document.RootElement, "renderedCount");
                var missingCount = ReadInt(document.RootElement, "missingCount");
                var rateLimitedCount = ReadInt(document.RootElement, "rateLimitedCount");
                var failedCount = ReadInt(document.RootElement, "failedCount");
                viewModel?.ReportCoordinateConversionCompleted(renderedCount, missingCount, rateLimitedCount, failedCount);
                return;
            }

            if (messageType is "coordinate-conversion-failed")
            {
                var failedCount = ReadInt(document.RootElement, "failedCount");
                var message = document.RootElement.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                viewModel?.ReportCoordinateConversionFailed(failedCount, message ?? "坐标转换失败，需人工确认。");
                return;
            }

            if (messageType is "bootstrap-consumed")
            {
                var expectedRenderedCount = ReadInt(document.RootElement, "expectedRenderedCount");
                var actualRenderableCount = ReadInt(document.RootElement, "actualRenderableCount");
                var invalidMapCoordinateCount = ReadInt(document.RootElement, "invalidMapCoordinateCount");

                if (invalidMapCoordinateCount > 0 || actualRenderableCount != expectedRenderedCount)
                {
                    viewModel?.ReportCoordinateConversionFailed(
                        invalidMapCoordinateCount,
                        $"地图宿主页未完全消费转换结果：预期 {expectedRenderedCount} 个可上图点位，实际 {actualRenderableCount} 个，非法渲染坐标 {invalidMapCoordinateCount} 个。");
                }
            }
        }
        catch
        {
        }
    }

    private void TrySendBootstrap()
    {
        if (!webViewReady || !pageReady || viewModel is null)
        {
            return;
        }

        var json = viewModel.MapBootstrapJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        MapHost.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string BuildErrorHtml(string message)
    {
        var safeMessage = System.Security.SecurityElement.Escape(message);
        return string.Concat(
            "<html><head><meta charset=\"utf-8\" />",
            "<style>",
            "body { font-family: 'Microsoft YaHei UI', sans-serif; padding: 24px; color: #0f4c81; background: linear-gradient(135deg, #eef5fb, #d7e9f5); }",
            ".card { max-width: 680px; margin: 60px auto; background: rgba(255,255,255,0.92); border-radius: 18px; padding: 24px; box-shadow: 0 18px 48px rgba(15,76,129,0.16); }",
            "h1 { margin: 0 0 12px; }",
            "p { margin: 0; line-height: 1.7; }",
            "</style></head><body>",
            "<div class=\"card\"><h1>地图页无法初始化</h1><p>",
            safeMessage,
            "</p></div></body></html>");
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
    }
}
