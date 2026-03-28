using CommunityToolkit.Mvvm.ComponentModel;

namespace Tysl.Inspection.Desktop.App.ViewModels;

public sealed partial class MapPageViewModel : ObservableObject
{
    public string Description => "本轮不做真实点位渲染，此页仅保留 WebView2 地图宿主占位。";
}
