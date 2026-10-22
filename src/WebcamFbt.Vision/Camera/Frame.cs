using OpenCvSharp;

namespace WebcamFbt.Vision.Camera;

/// <summary>
/// カメラから取得した 1 フレーム。Mat の所有権を持ち、Dispose で解放する。
/// </summary>
public sealed class Frame : IDisposable
{
    /// <summary>BGR 8bit 3ch の画像。</summary>
    public Mat Image { get; }

    /// <summary>どのカメラのフレームか（0 始まり）。</summary>
    public int CameraIndex { get; }

    /// <summary>カメラごとの連番。</summary>
    public long FrameId { get; }

    /// <summary>取得した時刻（Stopwatch.GetTimestamp の値）。遅延の計測に使う。</summary>
    public long CapturedTimestamp { get; }

    public Frame(Mat image, int cameraIndex, long frameId, long capturedTimestamp)
    {
        Image = image;
        CameraIndex = cameraIndex;
        FrameId = frameId;
        CapturedTimestamp = capturedTimestamp;
    }

    public void Dispose() => Image.Dispose();
}
