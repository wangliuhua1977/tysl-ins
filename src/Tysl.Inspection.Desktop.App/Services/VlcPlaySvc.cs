using System.Threading;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace Tysl.Inspection.Desktop.App.Services;

public enum PlayStage
{
    Initializing,
    Connecting,
    Playing,
    Stopped,
    InitFailed,
    LinkFailed,
    Interrupted
}

public sealed record PlayUpdate(
    PlayStage Stage,
    string StatusText,
    string HintText);

public static class PlayText
{
    public static string ToStatus(PlayStage stage)
    {
        return stage switch
        {
            PlayStage.Initializing => "正在初始化播放器",
            PlayStage.Connecting => "正在建立播放链路",
            PlayStage.Playing => "正在播放",
            PlayStage.Stopped => "已停止播放",
            PlayStage.InitFailed => "播放初始化失败",
            PlayStage.LinkFailed => "播放建链失败",
            PlayStage.Interrupted => "播放过程中断",
            _ => "播放状态未知"
        };
    }

    public static string ToHint(PlayStage stage)
    {
        return stage switch
        {
            PlayStage.Initializing => "正在准备 VLC 播放器。",
            PlayStage.Connecting => "播放器已按 RTSP 文档要求固定走 rtp over tcp。",
            PlayStage.Playing => "当前地址来自预览页，不会在播放窗口内再次鉴权或重取。",
            PlayStage.Stopped => "如需继续播放，请回到预览页重新打开窗口。",
            PlayStage.InitFailed => "播放器未能完成初始化，请查看应用日志。",
            PlayStage.LinkFailed => "地址可能失效，请回到预览页重新获取。",
            PlayStage.Interrupted => "地址可能失效，请回到预览页重新获取。",
            _ => string.Empty
        };
    }
}

public sealed class VlcPlaySvc(ILogger<VlcPlaySvc> logger) : IDisposable
{
    private static int coreInitialized;
    private LibVLC? libVlc;
    private bool isDisposed;
    private bool hasPlayed;
    private bool stopRequested;
    private string maskedRtspUrl = string.Empty;

    public MediaPlayer? MediaPlayer { get; private set; }

    public event EventHandler<PlayUpdate>? Updated;

    public bool Initialize()
    {
        if (MediaPlayer is not null)
        {
            return true;
        }

        try
        {
            EnsureCoreInitialized();

            libVlc = new LibVLC("--rtsp-tcp");
            MediaPlayer = new MediaPlayer(libVlc);
            MediaPlayer.Opening += OnOpening;
            MediaPlayer.Buffering += OnBuffering;
            MediaPlayer.Playing += OnPlaying;
            MediaPlayer.Stopped += OnStopped;
            MediaPlayer.EndReached += OnEndReached;
            MediaPlayer.EncounteredError += OnEncounteredError;

            Publish(PlayStage.Initializing);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Play init failed before media load.");
            Publish(PlayStage.InitFailed);
            return false;
        }
    }

    public bool Start(string rtspUrl)
    {
        maskedRtspUrl = MaskUrl(rtspUrl);
        hasPlayed = false;
        stopRequested = false;

        if (!Initialize())
        {
            logger.LogWarning("Play init failed. Rtsp={RtspUrl}", maskedRtspUrl);
            return false;
        }

        if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri))
        {
            logger.LogWarning("Play init failed because rtsp url is invalid. Rtsp={RtspUrl}", maskedRtspUrl);
            Publish(PlayStage.InitFailed, "RTSP 地址格式无效，请回到预览页重新获取。");
            return false;
        }

        try
        {
            using var media = new Media(libVlc!, uri);
            var started = MediaPlayer!.Play(media);
            if (!started)
            {
                logger.LogWarning("Play link failed to start. Rtsp={RtspUrl}", maskedRtspUrl);
                Publish(PlayStage.LinkFailed);
            }

            return started;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Play init failed while starting media. Rtsp={RtspUrl}", maskedRtspUrl);
            Publish(PlayStage.InitFailed);
            return false;
        }
    }

    public void Stop()
    {
        if (MediaPlayer is null || isDisposed)
        {
            return;
        }

        stopRequested = true;
        logger.LogInformation("Play stop requested. Rtsp={RtspUrl}", maskedRtspUrl);
        MediaPlayer.Stop();
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        if (MediaPlayer is not null)
        {
            MediaPlayer.Opening -= OnOpening;
            MediaPlayer.Buffering -= OnBuffering;
            MediaPlayer.Playing -= OnPlaying;
            MediaPlayer.Stopped -= OnStopped;
            MediaPlayer.EndReached -= OnEndReached;
            MediaPlayer.EncounteredError -= OnEncounteredError;

            try
            {
                MediaPlayer.Stop();
            }
            catch
            {
                // Ignore stop failures while window is tearing down.
            }

            MediaPlayer.Dispose();
        }

        libVlc?.Dispose();
    }

    private static void EnsureCoreInitialized()
    {
        if (Interlocked.Exchange(ref coreInitialized, 1) == 0)
        {
            Core.Initialize();
        }
    }

    private void OnOpening(object? sender, EventArgs e)
    {
        Publish(PlayStage.Connecting);
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        Publish(PlayStage.Connecting, $"正在建立播放链路，缓冲 {e.Cache:0}%");
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        hasPlayed = true;
        logger.LogInformation("Play started. Rtsp={RtspUrl}", maskedRtspUrl);
        Publish(PlayStage.Playing);
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        if (isDisposed)
        {
            return;
        }

        Publish(PlayStage.Stopped);
        stopRequested = false;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (isDisposed || stopRequested)
        {
            return;
        }

        logger.LogWarning("Play interrupted because media ended. Classification={Stage}, Rtsp={RtspUrl}", PlayStage.Interrupted, maskedRtspUrl);
        Publish(PlayStage.Interrupted);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        if (isDisposed || stopRequested)
        {
            return;
        }

        var stage = hasPlayed ? PlayStage.Interrupted : PlayStage.LinkFailed;
        logger.LogWarning("Play failed. Classification={Stage}, Rtsp={RtspUrl}", stage, maskedRtspUrl);
        Publish(stage);
    }

    private void Publish(PlayStage stage, string? hint = null)
    {
        Updated?.Invoke(
            this,
            new PlayUpdate(
                stage,
                PlayText.ToStatus(stage),
                string.IsNullOrWhiteSpace(hint) ? PlayText.ToHint(stage) : hint));
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
