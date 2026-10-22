using OpenCvSharp;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// 画像 1 枚から 1 人分の 2D 姿勢を推定する。
/// モデルを差し替えるときはこのインターフェースを実装する（RTMPose、BlazePose など）。
/// 1 つのインスタンスを複数スレッドから同時に呼ばないこと（カメラごとに別インスタンスを作る）。
/// </summary>
public interface IPoseEstimator2D : IDisposable
{
    /// <summary>表示用の名前（モデル名など）。</summary>
    string Name { get; }

    /// <summary>実際に使われている実行プロバイダー（"DirectML" / "CPU" など）。</summary>
    string Provider { get; }

    /// <summary>出力するキーポイントの並び。</summary>
    KeypointLayout Layout { get; }

    /// <summary>
    /// 姿勢を推定する。人が見つからなければ Pose は null。
    /// </summary>
    /// <param name="bgrImage">BGR 8bit 3ch の画像。</param>
    PoseEstimateResult Estimate(Mat bgrImage);

    /// <summary>前フレームの情報（追跡中の人物の位置など）を捨てる。</summary>
    void Reset();
}

/// <summary>推定結果と、処理時間の内訳。</summary>
public sealed record PoseEstimateResult(
    Pose2D? Pose,
    double DetectionMs,
    double PoseMs,
    bool DetectorRan);

/// <summary>画像から人物の矩形を検出する。</summary>
public interface IPersonDetector : IDisposable
{
    string Provider { get; }

    /// <summary>人物の矩形をスコアの高い順で返す。</summary>
    IReadOnlyList<BoundingBox> Detect(Mat bgrImage);
}

/// <summary>人物の矩形を受け取り、その範囲のキーポイントを推定する（トップダウン方式の後段）。</summary>
public interface IKeypointModel : IDisposable
{
    string Provider { get; }
    KeypointLayout Layout { get; }
    Keypoint2D[] Predict(Mat bgrImage, BoundingBox box);
}
