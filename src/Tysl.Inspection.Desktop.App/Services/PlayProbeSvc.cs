using System.Threading;
using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;

namespace Tysl.Inspection.Desktop.App.Services;

public sealed class PlayProbeSvc(
    ILoggerFactory loggerFactory,
    ILogger<PlayProbeSvc> logger) : IPlayProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<PlayProbeResult> ProbeAsync(PlayProbeArgs args, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        var signal = new TaskCompletionSource<PlayProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackStartedFlag = 0;

        using var player = new VlcPlaySvc(loggerFactory.CreateLogger<VlcPlaySvc>());
        player.Updated += OnUpdated;

        try
        {
            logger.LogInformation(
                "Playback probe started for {DeviceCode}. Rtsp={RtspUrl}",
                args.DeviceCode,
                MaskUrl(args.RtspUrl));

            var started = player.Start(args.RtspUrl);
            if (started)
            {
                Interlocked.Exchange(ref playbackStartedFlag, 1);
            }
            else if (!signal.Task.IsCompleted)
            {
                signal.TrySetResult(
                    new PlayProbeResult(
                        false,
                        false,
                        PlayText.ToStatus(PlayStage.LinkFailed),
                        PlayText.ToHint(PlayStage.LinkFailed)));
            }

            using var registration = timeoutSource.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    signal.TrySetCanceled(cancellationToken);
                    return;
                }

                var playbackStarted = Interlocked.CompareExchange(ref playbackStartedFlag, 0, 0) == 1;
                var stage = playbackStarted ? PlayStage.AddressExpired : PlayStage.LinkFailed;
                signal.TrySetResult(
                    new PlayProbeResult(
                        playbackStarted,
                        false,
                        PlayText.ToStatus(stage),
                        playbackStarted
                            ? "在限定时间内未进入 Playing，地址可能失效。"
                            : "在限定时间内未完成播放建链。"));
            });

            var result = await signal.Task;
            if (result.EnteredPlaying)
            {
                logger.LogInformation(
                    "Playback probe entered playing for {DeviceCode}.",
                    args.DeviceCode);
            }
            else
            {
                logger.LogWarning(
                    "Playback probe failed for {DeviceCode}. FailureCategory={FailureCategory}.",
                    args.DeviceCode,
                    result.FailureCategory);
            }

            return result;
        }
        finally
        {
            player.Updated -= OnUpdated;
            player.Stop();
        }

        void OnUpdated(object? sender, PlayUpdate update)
        {
            if (update.Stage is PlayStage.Connecting
                or PlayStage.Playing
                or PlayStage.Interrupted
                or PlayStage.AddressExpired)
            {
                Interlocked.Exchange(ref playbackStartedFlag, 1);
            }

            switch (update.Stage)
            {
                case PlayStage.Playing:
                    signal.TrySetResult(
                        new PlayProbeResult(
                            true,
                            true,
                            string.Empty,
                            "播放器已进入 Playing 播放态。"));
                    break;

                case PlayStage.InitFailed:
                case PlayStage.LinkFailed:
                case PlayStage.Interrupted:
                case PlayStage.AddressExpired:
                    signal.TrySetResult(
                        new PlayProbeResult(
                            Interlocked.CompareExchange(ref playbackStartedFlag, 0, 0) == 1,
                            false,
                            PlayText.ToStatus(update.Stage),
                            BuildDetail(update)));
                    break;
            }
        }
    }

    private static string BuildDetail(PlayUpdate update)
    {
        return string.IsNullOrWhiteSpace(update.HintText)
            ? update.StatusText
            : update.HintText;
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
