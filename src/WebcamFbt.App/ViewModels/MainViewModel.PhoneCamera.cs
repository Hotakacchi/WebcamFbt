using CommunityToolkit.Mvvm.ComponentModel;
using WebcamFbt.Vision.Camera;

namespace WebcamFbt.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _usePhoneCamera;
    [ObservableProperty] private int _phoneCameraPort = 8765;
    [ObservableProperty] private string _phoneCameraAddress = "";
    [ObservableProperty] private string _phoneCameraInfo = "スマホカメラを選んで開始すると接続先を表示します。";
    private PhoneCameraSource? _activePhoneCamera;
    private int _phoneCameraGeneration;

    private void PhoneCameraChanged(PhoneCameraSource camera)
    {
        if (!ReferenceEquals(_activePhoneCamera, camera)) return;
        _phoneCameraGeneration++;
        if (IsCameraCalibrating) EndCameraCapture();
        _stereoCalibration = null;
        UpdateTrackingMode();
        ClearCalibration();
        CameraCalibrationText = DescribeCameraCalibration();
        Status = "スマホのカメラが変わりました。カメラと体のキャリブレーションをやり直してください";
    }

    // 毎回オンで起動する。共有の開始をOSから検出したものではなく、画面自体から映像を隠す。
    [ObservableProperty] private bool _cameraPrivacyEnabled = true;

    public string PreviewPlaceholder => CameraPrivacyEnabled
        ? "カメラ映像は非表示です（トラッキングは動作します）"
        : "「開始」を押すとカメラ映像が表示されます";

    partial void OnCameraPrivacyEnabledChanged(bool value)
    {
        ApplyPreviewEnabled();
        if (value)
        {
            PreviewImage = null;
            PreviewImage2 = null;
            _pipeline?.Preview.Clear();
            _pipeline2?.Preview.Clear();
        }
        OnPropertyChanged(nameof(PreviewPlaceholder));
    }
}
