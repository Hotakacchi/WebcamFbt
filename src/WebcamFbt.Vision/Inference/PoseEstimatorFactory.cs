using OpenCvSharp;
using WebcamFbt.Core.Pose;
using WebcamFbt.Vision.Models;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// 既定の姿勢推定器（YOLOX-tiny + RTMPose-m Halpe26）を用意する。
/// </summary>
public static class PoseEstimatorFactory
{
    /// <summary>
    /// 必要ならモデルをダウンロードし、推定器を作ってウォームアップまで済ませる。
    /// 時間がかかる（数秒〜）ので UI スレッドからは await で呼ぶこと。
    /// </summary>
    public static async Task<IPoseEstimator2D> CreateDefaultAsync(
        ModelStore store,
        InferenceDevice device,
        IProgress<(double Ratio, string Message)>? progress,
        CancellationToken ct)
    {
        var detPath = await store.EnsureAsync(ModelCatalog.YoloxTinyHuman, progress, ct);
        var posePath = await store.EnsureAsync(ModelCatalog.RtmPoseMHalpe26, progress, ct);

        progress?.Report((1, "推論エンジンを初期化中…（初回は時間がかかります）"));
        return await Task.Run<IPoseEstimator2D>(() =>
        {
            YoloxDetector? detector = null;
            RtmPoseModel? poseModel = null;
            try
            {
                detector = new YoloxDetector(detPath, device);
                poseModel = new RtmPoseModel(posePath, KeypointLayout.Halpe26, device);

                // ウォームアップ（最初の推論は GPU の準備で遅いので、開始前に 1 回ずつ動かしておく）
                using (var dummy = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0)))
                {
                    detector.Detect(dummy);
                    poseModel.Predict(dummy, new BoundingBox(200, 80, 440, 400));
                }

                return new TopDownPoseEstimator(ModelCatalog.RtmPoseMHalpe26.DisplayName, detector, poseModel);
            }
            catch
            {
                detector?.Dispose();
                poseModel?.Dispose();
                throw;
            }
        }, ct);
    }
}
