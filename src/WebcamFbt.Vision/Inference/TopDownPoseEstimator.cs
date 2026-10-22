using System.Diagnostics;
using OpenCvSharp;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// トップダウン方式の姿勢推定（人物検出 → 切り出し → キーポイント推定）。
/// 検出器は重いので毎フレームは動かさず、前フレームのキーポイントから次の切り出し範囲を決める。
/// 人を見失ったときと、一定フレームごとにだけ検出器を動かす。
/// </summary>
public sealed class TopDownPoseEstimator : IPoseEstimator2D
{
    private readonly IPersonDetector _detector;
    private readonly IKeypointModel _keypointModel;
    private BoundingBox? _trackedBox;
    private int _framesSinceDetection;

    public string Name { get; }
    public string Provider => _keypointModel.Provider == _detector.Provider
        ? _keypointModel.Provider
        : $"検出:{_detector.Provider} / 姿勢:{_keypointModel.Provider}";
    public KeypointLayout Layout => _keypointModel.Layout;

    /// <summary>追跡を続けるのに必要な、信頼できるキーポイントの最小数。</summary>
    public int MinConfidentKeypoints { get; set; } = 8;

    /// <summary>キーポイントを「信頼できる」とみなすしきい値（追跡の判定用）。</summary>
    public float TrackingScoreThreshold { get; set; } = 0.3f;

    /// <summary>追跡がうまくいっていても、このフレーム数ごとに検出をやり直す（0 で無効）。</summary>
    public int RedetectInterval { get; set; } = 60;

    public TopDownPoseEstimator(string name, IPersonDetector detector, IKeypointModel keypointModel)
    {
        Name = name;
        _detector = detector;
        _keypointModel = keypointModel;
    }

    public PoseEstimateResult Estimate(Mat bgrImage)
    {
        double detMs = 0;
        bool detectorRan = false;

        bool needDetection = _trackedBox is null
            || (RedetectInterval > 0 && _framesSinceDetection >= RedetectInterval);

        if (needDetection)
        {
            long t0 = Stopwatch.GetTimestamp();
            var boxes = _detector.Detect(bgrImage);
            detMs = TimeUtil.ElapsedMsSince(t0);
            detectorRan = true;
            _framesSinceDetection = 0;

            // 複数人いる場合は、いちばん大きく写っている人（＝カメラに近い本人）を選ぶ
            BoundingBox? best = null;
            foreach (var b in boxes)
            {
                if (best is null || b.Area > best.Value.Area) best = b;
            }
            if (best is null)
            {
                _trackedBox = null;
                return new PoseEstimateResult(null, detMs, 0, detectorRan);
            }
            _trackedBox = best;
        }
        else
        {
            _framesSinceDetection++;
        }

        long t1 = Stopwatch.GetTimestamp();
        var box = _trackedBox!.Value;
        var keypoints = _keypointModel.Predict(bgrImage, box);
        double poseMs = TimeUtil.ElapsedMsSince(t1);
        var pose = new Pose2D(Layout, keypoints, box);

        // 次フレームの切り出し範囲を、今回のキーポイントから決める
        if (pose.CountConfident(TrackingScoreThreshold) >= MinConfidentKeypoints)
        {
            _trackedBox = BoxFromKeypoints(keypoints, TrackingScoreThreshold, bgrImage.Cols, bgrImage.Rows);
        }
        else
        {
            // 見失ったので、次フレームで検出からやり直す
            _trackedBox = null;
            if (!detectorRan)
            {
                // 追跡範囲がずれただけの可能性があるので、このフレームの結果は捨てる
                return new PoseEstimateResult(null, detMs, poseMs, detectorRan);
            }
        }

        return new PoseEstimateResult(pose, detMs, poseMs, detectorRan);
    }

    /// <summary>
    /// 信頼できるキーポイントを囲む矩形を作る。
    /// キーポイントは関節の中心なので、体の厚みや頭・足先の分を少し広げる
    /// （RtmPoseModel 側でさらに 1.25 倍される）。
    /// </summary>
    private static BoundingBox BoxFromKeypoints(Keypoint2D[] keypoints, float threshold, int imageW, int imageH)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var k in keypoints)
        {
            if (k.Score < threshold) continue;
            minX = Math.Min(minX, k.X);
            minY = Math.Min(minY, k.Y);
            maxX = Math.Max(maxX, k.X);
            maxY = Math.Max(maxY, k.Y);
        }
        float w = maxX - minX;
        float h = maxY - minY;
        float margin = Math.Max(w, h) * 0.1f;
        return new BoundingBox(
            Math.Clamp(minX - margin, 0, imageW - 1),
            Math.Clamp(minY - margin, 0, imageH - 1),
            Math.Clamp(maxX + margin, 0, imageW - 1),
            Math.Clamp(maxY + margin, 0, imageH - 1));
    }

    public void Reset()
    {
        _trackedBox = null;
        _framesSinceDetection = 0;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _keypointModel.Dispose();
    }
}
