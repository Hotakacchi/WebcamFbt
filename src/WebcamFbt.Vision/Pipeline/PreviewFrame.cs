using OpenCvSharp;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Pipeline;

/// <summary>
/// 推論スレッドから UI へ渡すプレビュー用のフレーム（骨格を描き込み済み）。
/// UI 側で取り出して表示した後に Dispose する。
/// </summary>
public sealed class PreviewFrame : IDisposable
{
    public Mat Image { get; }
    public Pose2D? Pose { get; }

    public PreviewFrame(Mat image, Pose2D? pose)
    {
        Image = image;
        Pose = pose;
    }

    public void Dispose() => Image.Dispose();
}
