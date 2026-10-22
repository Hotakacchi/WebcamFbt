using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// カメラ 1 台の 2D キーポイントから、おおまかな 3D 姿勢を推定する。
///
/// 考え方:
///  1. 体の距離: 「骨の実際の長さ（身長 × 比率）」と「画像上の長さ」の比から求める。
///     骨がカメラの方へ傾くと画像上で短く見える（＝遠いと誤認する）ので、
///     胴体・太もも・すねのうち、いちばん近いと判定された値を採用する。
///  2. 体の向き: 肩幅が画像上でどれだけ縮んでいるかで角度を、鼻が肩の中心よりどちらにずれているかで左右を決める。
///  3. 膝・つま先の前後: 太もも・足の甲が縮んで見える分だけ、カメラ側（前）に出ているとみなす。
///
/// 制約（カメラ 1 台では原理的に難しい部分。フェーズ 4 のステレオで改善する）:
///  - カメラに背を向けると左右や前後を正しく推定できない
///  - 足を後ろに引く動きは前に出したのと区別できない
/// </summary>
public sealed class MonocularLifter
{
    private OneEuroFilter? _depthFilter;

    /// <summary>この比率（画像上の長さ ÷ 実際の長さ）より長く見えていれば、骨は横向き（前後に傾いていない）とみなす。</summary>
    private const double ForeshortenThreshold = 0.92;

    public Pose3D? Lift(
        KeypointGate.GatedPoint[] points,
        KeypointLayout layout,
        CameraIntrinsics camera,
        CameraToTracking toTracking,
        TrackingSettings settings,
        BodyProportions body,
        double time)
    {
        _depthFilter ??= new OneEuroFilter(settings.DepthFilter);
        double h = settings.HeightMeters;

        bool Get(Joint j, out Vector2 p)
        {
            int i = layout.IndexOf(j);
            if (i >= 0 && points[i].Valid)
            {
                p = new Vector2(points[i].X, points[i].Y);
                return true;
            }
            p = default;
            return false;
        }

        bool GetMid(Joint a, Joint b, out Vector2 p)
        {
            if (Get(a, out var pa) && Get(b, out var pb))
            {
                p = (pa + pb) * 0.5f;
                return true;
            }
            p = default;
            return false;
        }

        // ---- 1. 体の距離 ----
        double? zRaw = null;
        void Candidate(double lengthMeters, double pixels)
        {
            if (pixels < 5) return; // 短すぎる（ほぼ真正面を向いた骨など）と誤差が大きい
            double estimate = camera.Fx * lengthMeters / pixels;
            if (zRaw is null || estimate < zRaw) zRaw = estimate;
        }

        bool hasNeck = Get(Joint.Neck, out var neck) || GetMid(Joint.LeftShoulder, Joint.RightShoulder, out neck);
        bool hasPelvis = Get(Joint.Pelvis, out var pelvis) || GetMid(Joint.LeftHip, Joint.RightHip, out pelvis);
        if (hasNeck && hasPelvis) Candidate(body.Torso * h, Vector2.Distance(neck, pelvis));
        Candidate(body.Thigh * h, MaxBoneLength(Get, Joint.LeftHip, Joint.LeftKnee, Joint.RightHip, Joint.RightKnee));
        Candidate(body.Shin * h, MaxBoneLength(Get, Joint.LeftKnee, Joint.LeftAnkle, Joint.RightKnee, Joint.RightAnkle));

        if (zRaw is null) return null;
        double depth = _depthFilter.Filter(System.Math.Clamp(zRaw.Value, 0.5, 10.0), time);

        // ---- 2. 体の向き（ヨー） ----
        double yaw = 0;
        if (Get(Joint.LeftShoulder, out var ls) && Get(Joint.RightShoulder, out var rs))
        {
            double widthM = camera.PixelsToMeters(Vector2.Distance(ls, rs), depth);
            double ratio = widthM / (body.ShoulderWidth * h);
            const double full = 0.97; // これ以上の比率なら正面とみなす（ブレ防止）
            double mag = System.Math.Acos(System.Math.Clamp(ratio / full, 0, 1));
            // 鼻が肩の中心より画像の左にあれば、人は自分の右（トラッキング座標の +X）を向いている
            if (mag > 0 && Get(Joint.Nose, out var nose))
            {
                yaw = nose.X < (ls.X + rs.X) * 0.5f ? mag : -mag;
            }
        }
        double sinYaw = System.Math.Sin(yaw);

        // ---- 3. 関節ごとのカメラからの距離 ----
        var z = new double[Enum.GetValues<Joint>().Length];
        Array.Fill(z, depth);

        // 左右の肩・腰: 体が回っている分だけ前後にずらす（左側は sin(yaw) > 0 のときカメラに近づく）
        double halfHip = body.HipWidth * h * 0.5;
        double halfShoulder = body.ShoulderWidth * h * 0.5;
        z[(int)Joint.LeftHip] = depth - halfHip * sinYaw;
        z[(int)Joint.RightHip] = depth + halfHip * sinYaw;
        z[(int)Joint.LeftShoulder] = depth - halfShoulder * sinYaw;
        z[(int)Joint.RightShoulder] = depth + halfShoulder * sinYaw;

        // 脚: 太ももが縮んで見える分だけ膝が前に出ているとみなし、足首・かかとは膝と同じ距離にする
        LiftLeg(Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle, Joint.LeftHeel, Joint.LeftBigToe, Joint.LeftSmallToe);
        LiftLeg(Joint.RightHip, Joint.RightKnee, Joint.RightAnkle, Joint.RightHeel, Joint.RightBigToe, Joint.RightSmallToe);

        void LiftLeg(Joint hip, Joint knee, Joint ankle, Joint heel, Joint bigToe, Joint smallToe)
        {
            double kneeZ = z[(int)hip];
            if (Get(hip, out var ph) && Get(knee, out var pk))
            {
                kneeZ -= Foreshortening(camera, Vector2.Distance(ph, pk), body.Thigh * h, z[(int)hip]);
            }
            z[(int)knee] = kneeZ;
            z[(int)ankle] = kneeZ;
            z[(int)heel] = kneeZ;
            double toeZ = kneeZ;
            if (Get(heel, out var pHeel) && (GetMid(bigToe, smallToe, out var pToe) || Get(bigToe, out pToe)))
            {
                // つま先は、足の甲が縮んで見える分だけカメラ側にある
                toeZ -= Foreshortening(camera, Vector2.Distance(pHeel, pToe), body.Foot * h, kneeZ);
            }
            z[(int)bigToe] = toeZ;
            z[(int)smallToe] = toeZ;
        }

        // ---- 4. 3D に戻してトラッキング座標へ ----
        var pose = new Pose3D { BodyDepth = depth, BodyYawDeg = yaw * 180.0 / System.Math.PI };
        for (int i = 0; i < layout.Count; i++)
        {
            if (!points[i].Valid) continue;
            var joint = layout.Joints[i];
            var cam = camera.Unproject(points[i].X, points[i].Y, z[(int)joint]);
            pose.Set(joint, toTracking.Transform(cam));
        }
        return pose;
    }

    /// <summary>
    /// 骨が前後に傾いている量 [m] を、画像上の縮み具合から求める。
    /// 縮みがしきい値以内なら 0（横向き）。しきい値の境目で値が飛ばないよう、その分を差し引く。
    /// </summary>
    private static double Foreshortening(CameraIntrinsics camera, double pixels, double lengthMeters, double depth)
    {
        double ratio = camera.PixelsToMeters(pixels, depth) / lengthMeters;
        if (ratio >= ForeshortenThreshold) return 0;
        double baseline = System.Math.Sqrt(1 - ForeshortenThreshold * ForeshortenThreshold);
        return lengthMeters * (System.Math.Sqrt(1 - ratio * ratio) - baseline);
    }

    private delegate bool Getter(Joint j, out Vector2 p);

    private static double MaxBoneLength(Getter get, Joint a1, Joint b1, Joint a2, Joint b2)
    {
        double len = 0;
        if (get(a1, out var p1) && get(b1, out var q1)) len = Vector2.Distance(p1, q1);
        if (get(a2, out var p2) && get(b2, out var q2)) len = System.Math.Max(len, Vector2.Distance(p2, q2));
        return len;
    }

    public void Reset() => _depthFilter?.Reset();
}
