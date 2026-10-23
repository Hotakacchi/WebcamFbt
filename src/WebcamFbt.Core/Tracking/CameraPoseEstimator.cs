using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// カメラの置き方（床からの高さと、下向きの傾き）。体のキャリブレーションのときに、立ち姿から求める。
/// これが分かると、単眼でも「床に着いた足」の位置を、カメラからの視線と床の交点として正しい大きさで求められる。
/// </summary>
/// <param name="HeightMeters">カメラの床からの高さ [m]。</param>
/// <param name="PitchDownDeg">カメラの下向きの傾き [度]（下を向いていると正）。</param>
/// <param name="FitErrorPx">立ち姿の各部の写り方とのずれ（RMS）[px]。</param>
public sealed record CameraPose(double HeightMeters, double PitchDownDeg, double FitErrorPx);

/// <summary>
/// 立ち姿の写り方から、カメラの高さと下向きの傾きを求める。
///
/// まっすぐ立った人の耳・肩・股関節・膝・足首は、床からの高さが身長に対してほぼ決まった比になる。
/// それらが画面のどの高さに写るかは、カメラの高さ・傾き・人までの水平距離で決まるので、
/// 写り方（複数フレームの中央値）に一番合う 3 つの値を探す（粗い刻みで全体を探してから、細かく詰める）。
/// 横向きの傾き（ロール）は 0 とみなす。
/// </summary>
public static class CameraPoseEstimator
{
    /// <summary>立ち姿の各部の床からの高さ（身長に対する比。人体寸法の標準的な値）。</summary>
    public const double EarHeightRatio = 0.935;
    public const double ShoulderHeightRatio = 0.818;
    public const double HipHeightRatio = 0.530;
    public const double KneeHeightRatio = 0.285;
    public const double AnkleHeightRatio = 0.045;
    public const double HeelHeightRatio = 0.015;
    public const double ToeHeightRatio = 0.012;

    private static readonly (Joint A, Joint B, double Ratio)[] Levels =
    [
        (Joint.LeftEar, Joint.RightEar, EarHeightRatio),
        (Joint.LeftShoulder, Joint.RightShoulder, ShoulderHeightRatio),
        (Joint.LeftHip, Joint.RightHip, HipHeightRatio),
        (Joint.LeftKnee, Joint.RightKnee, KneeHeightRatio),
        (Joint.LeftAnkle, Joint.RightAnkle, AnkleHeightRatio),
    ];

    /// <summary>
    /// 求める。体の各部が十分に写っていない、またはうまく合わない（まっすぐ立っていない等）ときは null。
    /// </summary>
    /// <param name="frames">立ち姿のフレームごとのキーポイント（信頼度ゲート後）。</param>
    public static CameraPose? Estimate(
        IReadOnlyList<KeypointGate.GatedPoint[]> frames, KeypointLayout layout, CameraIntrinsics camera, double heightMeters)
    {
        if (frames.Count == 0 || heightMeters <= 0) return null;

        // 各部の画面上の縦位置（左右の中点。片方だけならその点）の、フレームをまたいだ中央値
        var observed = new List<(double V, double Y)>();
        foreach (var (a, b, ratio) in Levels)
        {
            var vs = new List<double>();
            int ia = layout.IndexOf(a), ib = layout.IndexOf(b);
            foreach (var p in frames)
            {
                bool va = ia >= 0 && p[ia].Valid, vb = ib >= 0 && p[ib].Valid;
                if (va && vb) vs.Add((p[ia].Y + p[ib].Y) * 0.5);
                else if (va) vs.Add(p[ia].Y);
                else if (vb) vs.Add(p[ib].Y);
            }
            if (vs.Count >= frames.Count / 2 && vs.Count > 0) observed.Add((Median(vs), ratio * heightMeters));
        }
        // 足首は床の位置を決めるのに要る。上半身も 1 つ以上要る
        bool hasAnkle = observed.Any(o => o.Y < 0.1 * heightMeters);
        bool hasUpper = observed.Any(o => o.Y > 0.7 * heightMeters);
        if (observed.Count < 4 || !hasAnkle || !hasUpper) return null;

        double Error(double camH, double pitchRad, double dist)
        {
            double sum = 0;
            foreach (var (v, y) in observed)
            {
                double ang = System.Math.Atan2(camH - y, dist) - pitchRad;
                if (ang <= -1.5 || ang >= 1.5) return double.MaxValue;
                double d = camera.Cy + camera.Fy * System.Math.Tan(ang) - v;
                sum += d * d;
            }
            return sum;
        }

        // 粗く全体を探す（高さ 0.2〜3m、傾き -30〜80°、距離 0.5〜8m）
        double bestErr = double.MaxValue, bestH = 0, bestP = 0, bestD = 0;
        for (double camH = 0.2; camH <= 3.0; camH += 0.05)
            for (double pd = -30; pd <= 80; pd += 1)
                for (double dist = 0.5; dist <= 8.0; dist += 0.1)
                {
                    double e = Error(camH, pd * System.Math.PI / 180, dist);
                    if (e < bestErr) (bestErr, bestH, bestP, bestD) = (e, camH, pd, dist);
                }
        // 細かく詰める
        double h0 = bestH, p0 = bestP, d0 = bestD;
        for (double camH = h0 - 0.05; camH <= h0 + 0.05; camH += 0.005)
            for (double pd = p0 - 1; pd <= p0 + 1; pd += 0.1)
                for (double dist = d0 - 0.1; dist <= d0 + 0.1; dist += 0.01)
                {
                    if (camH <= 0 || dist <= 0) continue;
                    double e = Error(camH, pd * System.Math.PI / 180, dist);
                    if (e < bestErr) (bestErr, bestH, bestP, bestD) = (e, camH, pd, dist);
                }

        double rms = System.Math.Sqrt(bestErr / observed.Count);
        // 画面の高さの 3% より合わなければ、立ち姿として信用しない
        if (rms > 0.03 * camera.Height) return null;
        return new CameraPose(bestH, bestP, rms);
    }

    private static double Median(List<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) * 0.5;
    }
}
