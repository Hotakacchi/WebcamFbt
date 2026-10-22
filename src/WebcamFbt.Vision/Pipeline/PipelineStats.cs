namespace WebcamFbt.Vision.Pipeline;

/// <summary>UI 表示用の統計情報（ある時点のスナップショット）。</summary>
public sealed record PipelineStats(
    double CameraFps,
    double ProcessFps,
    double DetectionMs,
    double PoseMs,
    double TotalProcessMs,
    double LatencyMs,
    bool PersonFound,
    string Provider,
    int FrameWidth,
    int FrameHeight)
{
    public static PipelineStats Empty { get; } = new(0, 0, 0, 0, 0, 0, false, "-", 0, 0);
}
