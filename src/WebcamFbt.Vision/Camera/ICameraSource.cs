using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Threading;

namespace WebcamFbt.Vision.Camera;

/// <summary>推論パイプラインに最新の画像を渡すカメラ入力。</summary>
public interface ICameraSource : IDisposable
{
    LatestSlot<Frame> Frames { get; }
    RateCounter CaptureRate { get; }
    void Start();
    void Stop();
}
