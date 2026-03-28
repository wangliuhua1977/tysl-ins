using CommunityToolkit.Mvvm.ComponentModel;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class ShellWindowViewModel : ObservableObject
{
    public ShellWindowViewModel(
        OverviewPageViewModel overview,
        BasicConfigurationPageViewModel basicConfiguration,
        MapPageViewModel map,
        ThemeSettingsPageViewModel themeSettings)
    {
        Overview = overview;
        BasicConfiguration = basicConfiguration;
        Map = map;
        ThemeSettings = themeSettings;

        BasicConfiguration.SyncCompleted += OnSyncCompleted;
    }

    public OverviewPageViewModel Overview { get; }

    public BasicConfigurationPageViewModel BasicConfiguration { get; }

    public MapPageViewModel Map { get; }

    public ThemeSettingsPageViewModel ThemeSettings { get; }

    public string PhaseText => "Phase 0 / 点位同步最小链路";

    public string ScopeText => "当前轮次仅实现分组与分组下设备同步、SQLite 落地、基础配置同步入口与运行总览最小统计。";

    public async Task InitializeAsync()
    {
        await Overview.RefreshAsync();
        await BasicConfiguration.LoadAsync();
    }

    private async void OnSyncCompleted(object? sender, EventArgs e)
    {
        await Overview.RefreshAsync();
    }
}
