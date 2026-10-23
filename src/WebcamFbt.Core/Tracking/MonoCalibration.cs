using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 単眼の体のキャリブレーションの仕上げ。
///
/// 1. 立ち姿の 2D からカメラの置き方（床からの高さ・下向きの傾き）を求める（<see cref="CameraPoseEstimator"/>）
/// 2. その置き方で、貯めた 2D を床の面を使って 3D に直す（向きは正面とみなす）
/// 3. 直した 3D から体の比率を測り、ありえない値は既定値に戻す
///
/// 骨の長さから距離を出す従来の 3D 化は、見下ろしたカメラだと脚が縮んで見えて比率が狂うため、
/// 床の面で遠近を正しく扱ってから測る。床はもともと Y0、正面の補正もしない。
/// </summary>
public static class MonoCalibration
{
    /// <summary>この傾き [度] より下を向いていたら、置き方の注意を出す。</summary>
    public const double SteepPitchDeg = 15;

    /// <summary>この高さ [m] より高く置いていたら、置き方の注意を出す。</summary>
    public const double HighCameraMeters = 1.5;

    /// <summary>
    /// 作る。カメラの置き方が求まらない（全身が写っていない・まっすぐ立っていない）ときは null。
    /// </summary>
    /// <param name="frames">立ち姿のフレームごとの 2D キーポイント（信頼度ゲート後・歪み補正後）。</param>
    public static CalibrationData? Build(
        IReadOnlyList<KeypointGate.GatedPoint[]> frames, KeypointLayout layout, CameraIntrinsics camera, TrackingSettings settings)
    {
        double h = settings.HeightMeters;
        var pose = CameraPoseEstimator.Estimate(frames, layout, camera, h);
        if (pose is null) return null;

        // 床の面を使って 3D に直し、比率を測る（既定の比率で直す。立ち姿では向きも膝の出も 0 なので比率はほぼ効かない）
        var lifter = new MonocularLifter();
        var toTracking = new CameraToTracking { PitchDownDeg = pose.PitchDownDeg };
        var calibrator = new Calibrator();
        var shoulderWidths = new List<double>();
        for (int i = 0; i < frames.Count; i++)
        {
            var p3 = lifter.Lift(frames[i], layout, camera, toTracking, settings, BodyProportions.Default, i / 30.0,
                floor: pose, forceFrontal: true);
            if (p3 is null) continue;
            calibrator.Add(p3);
            if (p3.TryGet(Joint.LeftShoulder, out var ls) && p3.TryGet(Joint.RightShoulder, out var rs))
            {
                shoulderWidths.Add(System.Math.Abs(ls.X - rs.X));
            }
        }
        var measured = calibrator.Build(h);
        if (measured is null) return null;

        var warnings = new List<string>(measured.Warnings ?? Array.Empty<string>());
        if (pose.PitchDownDeg > SteepPitchDeg || pose.HeightMeters > HighCameraMeters)
        {
            warnings.Add($"カメラが高い・下を向いています（高さ {pose.HeightMeters:F1} m、下向き {pose.PitchDownDeg:F0}°）。" +
                         "腰の高さ（0.8〜1 m）に水平に置くと、足の高さと距離が安定します");
        }

        // 正面での肩幅の見え方。体の面の上での肩幅 ÷ 比率どおりの肩幅（比率を戻したときは 1 からずれる）
        double? shoulderRef = null;
        if (shoulderWidths.Count > 0)
        {
            shoulderWidths.Sort();
            double median = shoulderWidths[shoulderWidths.Count / 2];
            double r = median / (measured.Body.ShoulderWidth * h);
            if (double.IsFinite(r) && r > 0.3) shoulderRef = r;
        }

        return new CalibrationData(measured.Body, float.NaN, 0, Camera: pose, ShoulderRatioRef: shoulderRef, Warnings: warnings);
    }
}
