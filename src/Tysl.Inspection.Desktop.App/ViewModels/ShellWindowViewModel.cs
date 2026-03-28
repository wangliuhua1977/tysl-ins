using CommunityToolkit.Mvvm.ComponentModel;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class ShellWindowViewModel : ObservableObject
{
    public ShellWindowViewModel(
        OverviewPageViewModel overview,
        BasicConfigurationPageViewModel basicConfiguration,
        MapPageViewModel map,
        PreviewPageViewModel preview,
        ThemeSettingsPageViewModel themeSettings)
    {
        Overview = overview;
        BasicConfiguration = basicConfiguration;
        Map = map;
        Preview = preview;
        ThemeSettings = themeSettings;

        BasicConfiguration.SyncCompleted += OnSyncCompleted;
    }

    public OverviewPageViewModel Overview { get; }

    public BasicConfigurationPageViewModel BasicConfiguration { get; }

    public MapPageViewModel Map { get; }

    public PreviewPageViewModel Preview { get; }

    public ThemeSettingsPageViewModel ThemeSettings { get; }

    public string PhaseText => "Phase 0 / 单点预览最小链路";

    public string ScopeText => "当前轮次聚焦单点视频预览最小链路：复用本地点位、先做状态诊断、再获取 RTSP 地址，不做 RTSP 实际播放。";

    public async Task InitializeAsync()
    {
        await Overview.RefreshAsync();
        await BasicConfiguration.LoadAsync();
        await Preview.InitializeAsync();
    }

    private async void OnSyncCompleted(object? sender, EventArgs e)
    {
        await Overview.RefreshAsync();
        await Preview.RefreshAsync();
    }
}
