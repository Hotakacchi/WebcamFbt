using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// カメラ 2 台の 2D キーポイントから、三角測量で 3D 姿勢を求める（フェーズ 4c）。
///
/// 1. 各カメラの歪みを補正し、正規化座標（焦点距離 1 の画像面）にする
/// 2. 線形三角測量: カメラ 1 を P1 = [I|0]、カメラ 2 を P2 = [R|T] として、両方の視線に最も近い点を最小二乗で求める
/// 3. どちらかのカメラの後ろに来る点や、再投影誤差が大きい点は捨てる（2 台で別の点を指している＝推定の失敗）
///
/// 単眼（<see cref="MonocularLifter"/>）と違い、骨の長さの仮定を使わないので、背面や足を後ろに引く動きも測れる。
/// 片方のカメラにしか写っていない関節は無効になる（キーポイントの保持で一瞬の隠れは吸収される）。
/// </summary>
public static class StereoTriangulator
{
    /// <summary>カメラからこの距離 [m] より近い点は誤りとみなす。</summary>
    private const double MinDepth = 0.1;

    /// <summary>三角測量できなかった組に付ける誤差（左右の取り違えの判定用）。</summary>
    private const double FailurePenaltyPx = 1000;

    /// <summary>推定どおりの左右で 1 点あたりの再投影誤差がこれ [px] を超えたときだけ、入れ替えを検討する。</summary>
    private const double SwapMinErrorPx = 8;

    private static readonly Dictionary<Joint, Joint> Mirror = BuildMirror();

    /// <summary>1 点の三角測量の結果。</summary>
    /// <param name="Camera1Point">カメラ 1 の座標（OpenCV: +X 右、+Y 下、+Z 奥、m）。</param>
    /// <param name="ReprojErrorPx">2 台の再投影誤差の大きい方 [px]。</param>
    public readonly record struct PointResult(Vector3 Camera1Point, double ReprojErrorPx);

    /// <summary>
    /// 1 点を三角測量する。解けない・どちらかのカメラの後ろに来る場合は null。
    /// </summary>
    /// <param name="uv1">カメラ 1 の画像上の点（歪んだまま）。</param>
    /// <param name="uv2">カメラ 2 の画像上の点（歪んだまま）。</param>
    public static PointResult? TriangulatePoint(Vector2 uv1, Vector2 uv2, StereoCalibration stereo)
    {
        var c1 = stereo.Camera1;
        var c2 = stereo.Camera2;
        var p1 = c1.Undistort(uv1);
        var p2 = c2.Undistort(uv2);
        double x1 = (p1.X - c1.Cx) / c1.Fx, y1 = (p1.Y - c1.Cy) / c1.Fy;
        double x2 = (p2.X - c2.Cx) / c2.Fx, y2 = (p2.Y - c2.Cy) / c2.Fy;
        var r = stereo.R;
        var t = stereo.T;

        // 各カメラの「視線の上にある」条件を 2 本ずつ並べる: (x·P3 − P1)·X = 0、(y·P3 − P2)·X = 0
        // カメラ 1 は P = [I|0]、カメラ 2 は P = [R|T] なので、未知数 X（3 要素）の 4 本の一次式になる
        Span<double> a = stackalloc double[12];
        Span<double> b = stackalloc double[4];
        SetRow(a, b, 0, -1, 0, x1, 0);
        SetRow(a, b, 1, 0, -1, y1, 0);
        SetRow(a, b, 2, x2 * r[6] - r[0], x2 * r[7] - r[1], x2 * r[8] - r[2], t[0] - x2 * t[2]);
        SetRow(a, b, 3, y2 * r[6] - r[3], y2 * r[7] - r[4], y2 * r[8] - r[5], t[1] - y2 * t[2]);

        // 最小二乗: (AᵀA) X = Aᵀb をクラメルの公式で解く
        double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0, v0 = 0, v1 = 0, v2 = 0;
        for (int i = 0; i < 4; i++)
        {
            double ax = a[i * 3], ay = a[i * 3 + 1], az = a[i * 3 + 2], bi = b[i];
            m00 += ax * ax; m01 += ax * ay; m02 += ax * az;
            m11 += ay * ay; m12 += ay * az; m22 += az * az;
            v0 += ax * bi; v1 += ay * bi; v2 += az * bi;
        }
        double det = m00 * (m11 * m22 - m12 * m12) - m01 * (m01 * m22 - m12 * m02) + m02 * (m01 * m12 - m11 * m02);
        if (System.Math.Abs(det) < 1e-12) return null;
        double px = (v0 * (m11 * m22 - m12 * m12) - m01 * (v1 * m22 - m12 * v2) + m02 * (v1 * m12 - m11 * v2)) / det;
        double py = (m00 * (v1 * m22 - m12 * v2) - v0 * (m01 * m22 - m12 * m02) + m02 * (m01 * v2 - v1 * m02)) / det;
        double pz = (m00 * (m11 * v2 - v1 * m12) - m01 * (m01 * v2 - v1 * m02) + v0 * (m01 * m12 - m11 * m02)) / det;

        // カメラ 2 の座標
        double qx = r[0] * px + r[1] * py + r[2] * pz + t[0];
        double qy = r[3] * px + r[4] * py + r[5] * pz + t[1];
        double qz = r[6] * px + r[7] * py + r[8] * pz + t[2];
        if (pz < MinDepth || qz < MinDepth) return null;

        // 再投影誤差（歪み補正後の画像上で比べる）
        double e1 = Hypot(px / pz * c1.Fx + c1.Cx - p1.X, py / pz * c1.Fy + c1.Cy - p1.Y);
        double e2 = Hypot(qx / qz * c2.Fx + c2.Cx - p2.X, qy / qz * c2.Fy + c2.Cy - p2.Y);
        return new PointResult(new Vector3((float)px, (float)py, (float)pz), System.Math.Max(e1, e2));
    }

    /// <summary>
    /// 姿勢全体を三角測量して、トラッキング座標の 3D 姿勢にする。使える関節が 1 つもなければ null。
    /// </summary>
    /// <param name="view1">カメラ 1 のキーポイント（信頼度ゲート後）。</param>
    /// <param name="view2">カメラ 2 のキーポイント（信頼度ゲート後）。</param>
    /// <param name="maxReprojErrorPx">これより再投影誤差が大きい関節は捨てる。</param>
    public static Pose3D? Triangulate(
        KeypointGate.GatedPoint[] view1,
        KeypointGate.GatedPoint[] view2,
        KeypointLayout layout,
        StereoCalibration stereo,
        CameraToTracking toTracking,
        double maxReprojErrorPx)
    {
        // 姿勢推定は人の左右を取り違えることがある（特に横向き・後ろ向き）。カメラ 2 だけ取り違えると
        // 左足と右足を組み合わせて三角測量してしまうので、左右が矛盾していて入れ替えると解消するなら入れ替える
        bool swap = ShouldSwapCamera2(view1, view2, layout, stereo);

        var cam1Points = new Vector3?[layout.Count];
        int valid = 0;
        for (int i = 0; i < layout.Count; i++)
        {
            int i2 = swap ? MirrorIndex(layout, i) : i;
            if (!view1[i].Valid || !view2[i2].Valid) continue;
            var result = TriangulatePoint(new Vector2(view1[i].X, view1[i].Y), new Vector2(view2[i2].X, view2[i2].Y), stereo);
            if (result is null || result.Value.ReprojErrorPx > maxReprojErrorPx) continue;
            cam1Points[i] = result.Value.Camera1Point;
            valid++;
        }
        if (valid == 0) return null;

        // 体までの距離（カメラ 1 から骨盤）と体の向き
        Vector3? Get(Joint j)
        {
            int i = layout.IndexOf(j);
            return i >= 0 ? cam1Points[i] : null;
        }
        var lh = Get(Joint.LeftHip);
        var rh = Get(Joint.RightHip);
        double depth = 0, yawDeg = 0;
        if (lh is not null && rh is not null)
        {
            depth = (lh.Value.Z + rh.Value.Z) * 0.5;
            // トラッキング座標で「左股関節 → 右股関節」が体の右方向。正面（+X が右）から右を向くと正
            var right = toTracking.Transform(rh.Value) - toTracking.Transform(lh.Value);
            yawDeg = System.Math.Atan2(-right.Z, right.X) * 180.0 / System.Math.PI;
        }
        else if (Get(Joint.Pelvis) is { } pelvis)
        {
            depth = pelvis.Z;
        }

        var pose = new Pose3D { BodyDepth = depth, BodyYawDeg = yawDeg };
        for (int i = 0; i < layout.Count; i++)
        {
            if (cam1Points[i] is { } p) pose.Set(layout.Joints[i], toTracking.Transform(p));
        }
        return pose;
    }

    /// <summary>
    /// 左右の関節の組について、カメラ 2 の左右を入れ替えた方が再投影誤差の合計がはっきり小さいか。
    /// 2 台を横に並べた配置では、同じ高さの左右（腰の左右など）を入れ替えても再投影誤差はほとんど変わらない
    /// （どちらの組み合わせでも矛盾なく解けてしまう）。誤差だけで比べるとノイズで勝手に入れ替わるので、
    /// 推定どおりの左右の誤差が明らかに大きい（矛盾している）ときだけ入れ替える。
    /// 同じ高さの左右の取り違えはこの方法では見分けられない（残る弱点）。
    /// </summary>
    private static bool ShouldSwapCamera2(
        KeypointGate.GatedPoint[] view1, KeypointGate.GatedPoint[] view2, KeypointLayout layout, StereoCalibration stereo)
    {
        double normal = 0, swapped = 0;
        int pairs = 0;
        for (int i = 0; i < layout.Count; i++)
        {
            var joint = layout.Joints[i];
            if (!KeypointLayout.IsLeft(joint)) continue;
            int j = layout.IndexOf(Mirror[joint]);
            if (j < 0 || !view1[i].Valid || !view1[j].Valid || !view2[i].Valid || !view2[j].Valid) continue;
            normal += Error(view1[i], view2[i], stereo) + Error(view1[j], view2[j], stereo);
            swapped += Error(view1[i], view2[j], stereo) + Error(view1[j], view2[i], stereo);
            pairs++;
        }
        if (pairs == 0) return false;
        double meanNormal = normal / (2 * pairs);
        return meanNormal > SwapMinErrorPx && swapped < normal * 0.5;
    }

    private static double Error(KeypointGate.GatedPoint a, KeypointGate.GatedPoint b, StereoCalibration stereo)
        => TriangulatePoint(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), stereo)?.ReprojErrorPx ?? FailurePenaltyPx;

    private static int MirrorIndex(KeypointLayout layout, int i)
    {
        if (!Mirror.TryGetValue(layout.Joints[i], out var mirrored)) return i;
        int j = layout.IndexOf(mirrored);
        return j >= 0 ? j : i;
    }

    private static Dictionary<Joint, Joint> BuildMirror()
    {
        var map = new Dictionary<Joint, Joint>();
        foreach (var j in Enum.GetValues<Joint>())
        {
            var name = j.ToString();
            string? other = name.StartsWith("Left", StringComparison.Ordinal) ? "Right" + name[4..]
                : name.StartsWith("Right", StringComparison.Ordinal) ? "Left" + name[5..]
                : null;
            if (other != null && Enum.TryParse<Joint>(other, out var m)) map[j] = m;
        }
        return map;
    }

    private static void SetRow(Span<double> a, Span<double> b, int row, double ax, double ay, double az, double bv)
    {
        a[row * 3] = ax;
        a[row * 3 + 1] = ay;
        a[row * 3 + 2] = az;
        b[row] = bv;
    }

    private static double Hypot(double x, double y) => System.Math.Sqrt(x * x + y * y);
}
