using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.App.Services;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class PlayWinViewModel(
    VlcPlaySvc player,
    ILogger<PlayWinViewModel> logger,
    PlayWinArgs args,
    Action closeWindow) : ObservableObject, IDisposable
{
    private bool hasLoaded;
    private bool isDisposed;

    public string WindowTitle { get; } = $"单点播放 - {args.DeviceName}";

    public string DeviceName { get; } = args.DeviceName;

    public string DeviceCode { get; } = args.DeviceCode;

    public string MaskedRtspUrl { get; } = MaskUrl(args.RtspUrl);

    [ObservableProperty]
    private MediaPlayer? mediaPlayer;

    [ObservableProperty]
    private string statusText = PlayText.ToStatus(PlayStage.Initializing);

    [ObservableProperty]
    private string statusHintText = PlayText.ToHint(PlayStage.Initializing);

    public Task InitializeAsync()
    {
        if (hasLoaded)
        {
            return Task.CompletedTask;
        }

        hasLoaded = true;
        player.Updated += OnUpdated;

        if (player.Initialize())
        {
            MediaPlayer = player.MediaPlayer;
            player.Start(args.RtspUrl);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void StopPlay()
    {
        player.Stop();
    }

    [RelayCommand]
    private void CloseWin()
    {
        player.Stop();
        closeWindow();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;
        player.Updated -= OnUpdated;
        player.Dispose();
    }

    private void OnUpdated(object? sender, PlayUpdate update)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply(update);
            return;
        }

        _ = dispatcher.InvokeAsync(() => Apply(update));
    }

    private void Apply(PlayUpdate update)
    {
        StatusText = update.StatusText;
        StatusHintText = update.HintText;
        MediaPlayer ??= player.MediaPlayer;

        if (update.Stage is PlayStage.InitFailed or PlayStage.LinkFailed or PlayStage.Interrupted)
        {
            logger.LogWarning(
                "Play window status updated. DeviceCode={DeviceCode}, Stage={Stage}",
                DeviceCode,
                update.Stage);
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
