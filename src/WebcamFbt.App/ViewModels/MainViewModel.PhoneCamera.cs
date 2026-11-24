using CommunityToolkit.Mvvm.ComponentModel;

namespace WebcamFbt.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _usePhoneCamera;
    [ObservableProperty] private int _phoneCameraPort = 8765;
    [ObservableProperty] private string _phoneCameraAddress = "";
    [ObservableProperty] private string _phoneCameraInfo = "スマホカメラを選んで開始すると接続先を表示します。";
}
