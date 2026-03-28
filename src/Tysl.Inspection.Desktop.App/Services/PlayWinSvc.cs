using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.App.ViewModels;
using Tysl.Inspection.Desktop.App.Views;

namespace Tysl.Inspection.Desktop.App.Services;

public interface IPlayWinSvc
{
    string? Open(PlayWinArgs args);
}

public sealed record PlayWinArgs(
    string DeviceName,
    string DeviceCode,
    string RtspUrl);

public sealed class PlayWinSvc(
    IServiceProvider services,
    ILogger<PlayWinSvc> logger) : IPlayWinSvc
{
    public string? Open(PlayWinArgs args)
    {
        PlayWinViewModel? viewModel = null;

        try
        {
            var window = new PlayWin();
            viewModel = ActivatorUtilities.CreateInstance<PlayWinViewModel>(
                services,
                args,
                (Action)(() => window.Close()));

            window.DataContext = viewModel;
            window.Owner = System.Windows.Application.Current?.MainWindow;
            window.Show();
            window.Activate();

            logger.LogInformation(
                "Opened single-device play window for {DeviceCode}. Rtsp={RtspUrl}",
                args.DeviceCode,
                MaskUrl(args.RtspUrl));

            return null;
        }
        catch (Exception exception)
        {
            viewModel?.Dispose();

            logger.LogError(
                exception,
                "Failed to open single-device play window for {DeviceCode}. Rtsp={RtspUrl}",
                args.DeviceCode,
                MaskUrl(args.RtspUrl));

            return "打开播放窗口失败，请查看日志。";
        }
    }

    private static string MaskUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= 12
            ? "****"
            : $"{value[..6]}****{value[^6..]}";
    }
}
