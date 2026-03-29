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

    public string PhaseText => "Phase 0 / 首版最小闭环收口";

    public string ScopeText => "当前轮次聚焦监控目录树主链路切换：目录递归、设备分页拉全、SQLite 全量快照和最小对账；不扩工单、通知、批量巡检。";

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
