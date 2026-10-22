namespace WebcamFbt.Vision.Camera;

/// <summary>カメラの取得 API。</summary>
public enum CameraBackend
{
    /// <summary>Media Foundation（Windows 標準。多くのカメラで安定）。</summary>
    MediaFoundation,

    /// <summary>DirectShow（古いカメラや、MJPG 指定が効かない場合に）。</summary>
    DirectShow,
}

/// <summary>カメラを開くときの設定。</summary>
public sealed record CameraSettings(
    int DeviceIndex,
    int Width = 1280,
    int Height = 720,
    int Fps = 60,
    CameraBackend Backend = CameraBackend.MediaFoundation,
    bool UseMjpg = true);
