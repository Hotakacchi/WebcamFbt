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

    // 床の面を使うとき（カメラの高さと傾きが分かっているとき）の状態
    private OneEuroFilter? _planeFilter; // カメラから体の面までの水平距離
    private OneEuroFilter? _yawFilter;   // 体の向き [rad]
    private int _yawSign = 1;            // 体の向きの左右（鼻がはっきり逆にずれるまで保つ）

    /// <summary>この比率（画像上の長さ ÷ 実際の長さ）より長く見えていれば、骨は横向き（前後に傾いていない）とみなす。</summary>
    private const double ForeshortenThreshold = 0.92;

    /// <summary>肩幅の見え方から出した向きのうち、これより小さい分は無視する（正面に近いところは肩幅がほとんど変わらず、揺れで大きく振れるため）。</summary>
    private const double YawDeadZoneRad = 20 * System.Math.PI / 180;

    /// <summary>鼻が肩の中心から肩幅のこの割合以上ずれたときだけ、向きの左右を切り替える。</summary>
    private const double YawSignSwitch = 0.12;

    /// <summary>両足の床の上の位置がこれ [m] より近ければ、両足とも床に着いているとみなす。</summary>
    private const double BothFeetPlantedTolerance = 0.15;

    /// <param name="floor">
    /// カメラの置き方（体のキャリブレーションで求めたもの）。あれば床の面を使って 3D 化する（座標は床の上の、カメラの真下が原点）。
    /// 無ければ従来の方法（骨の長さから距離を出す。座標はカメラが原点）。
    /// </param>
    /// <param name="shoulderRatioRef">キャリブレーションのときの、正面での肩幅の見え方（向きを出す基準）。</param>
    /// <param name="forceFrontal">向きを 0（カメラの方を向いている）とみなす（キャリブレーションで測り直すとき）。</param>
    public Pose3D? Lift(
        KeypointGate.GatedPoint[] points,
        KeypointLayout layout,
        CameraIntrinsics camera,
        CameraToTracking toTracking,
        TrackingSettings settings,
        BodyProportions body,
        double time,
        CameraPose? floor = null,
        double shoulderRatioRef = 1.0,
        bool forceFrontal = false)
    {
        if (floor is not null)
        {
            return LiftOnFloor(points, layout, camera, toTracking, settings, body, time, floor, shoulderRatioRef, forceFrontal);
        }

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
        => ForwardOffset(camera.PixelsToMeters(pixels, depth), lengthMeters);

    /// <summary>
    /// 骨が前後に傾いている量 [m] を、見えている長さ（体の面の上での長さ）と本当の長さから求める。
    /// 縮みがしきい値以内なら 0（横向き）。しきい値の境目で値が飛ばないよう、その分を差し引く。
    /// </summary>
    private static double ForwardOffset(double observedMeters, double lengthMeters)
    {
        if (lengthMeters <= 0) return 0;
        double ratio = observedMeters / lengthMeters;
        if (ratio >= ForeshortenThreshold) return 0;
        double baseline = System.Math.Sqrt(1 - ForeshortenThreshold * ForeshortenThreshold);
        return lengthMeters * (System.Math.Sqrt(1 - ratio * ratio) - baseline);
    }

    /// <summary>
    /// 床の面を使った 3D 化（カメラの高さと傾きが分かっているとき）。座標は床の上の、カメラの真下が原点（Y=0 が床）。
    ///
    /// 1. 床に着いている足: 足首を、カメラからの視線と「床から足首の高さの面」の交点として求める。
    ///    両足の交点のうちカメラに近い方が着いている足（上げた足は高いので、視線の先の床が遠くなる）。
    /// 2. 体の面: 着いている足の奥行きにある縦の面。ほかの関節は、視線とこの面（向きや膝の出ぶんだけ前後にずらす）の交点。
    ///    見下ろしの遠近がそのまま反映されるので、脚が縮んで見えても体が小さくならない。
    /// 3. 体の向き: 体の面の上での肩幅を、キャリブレーションのときの正面の肩幅と比べる。正面に近い小さな揺れは無視し、
    ///    左右は鼻がはっきり逆にずれるまで保ち、平滑化する。
    /// 足首が見えないときは、従来どおり骨の長さから距離を出して体の面を決める。
    /// </summary>
    private Pose3D? LiftOnFloor(
        KeypointGate.GatedPoint[] points,
        KeypointLayout layout,
        CameraIntrinsics camera,
        CameraToTracking toTracking,
        TrackingSettings settings,
        BodyProportions body,
        double time,
        CameraPose floor,
        double shoulderRatioRef,
        bool forceFrontal)
    {
        _planeFilter ??= new OneEuroFilter(settings.DepthFilter);
        _yawFilter ??= new OneEuroFilter(settings.RotationFilter);
        double h = settings.HeightMeters;
        var origin = new Vector3(0, (float)floor.HeightMeters, 0); // カメラの位置

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

        // 画面上の点に向かう視線の向き（トラッキング座標）
        Vector3 Ray(Vector2 p) => Vector3.Normalize(toTracking.Transform(camera.Unproject(p.X, p.Y, 1.0)));

        // 視線と「床から高さ y の面」の交点（下向きの視線でなければ null）
        Vector3? AtHeight(Vector2 p, double y)
        {
            var d = Ray(p);
            if (d.Y > -0.02f) return null;
            float t = (float)((y - origin.Y) / d.Y);
            return origin + t * d;
        }

        // 視線と「奥行き z の縦の面」の交点（カメラは -Z の方を向いている）
        Vector3 AtDepth(Vector2 p, double z)
        {
            var d = Ray(p);
            float t = d.Z < -1e-4f ? (float)(z / d.Z) : 0f;
            return origin + t * d;
        }

        // ---- 1. 床に着いている足と、体の面の奥行き ----
        Vector3? leftFloor = Get(Joint.LeftAnkle, out var la) ? AtHeight(la, CameraPoseEstimator.AnkleHeightRatio * h) : null;
        Vector3? rightFloor = Get(Joint.RightAnkle, out var ra) ? AtHeight(ra, CameraPoseEstimator.AnkleHeightRatio * h) : null;
        bool leftPlanted = false, rightPlanted = false;
        double zRaw;
        if (leftFloor is { } lf && rightFloor is { } rf)
        {
            if (System.Math.Abs(lf.Z - rf.Z) < BothFeetPlantedTolerance)
            {
                leftPlanted = rightPlanted = true;
                zRaw = (lf.Z + rf.Z) * 0.5;
            }
            else if (lf.Z > rf.Z) { leftPlanted = true; zRaw = lf.Z; } // Z が大きい方がカメラに近い
            else { rightPlanted = true; zRaw = rf.Z; }
        }
        else if (leftFloor is { } onlyLeft) { leftPlanted = true; zRaw = onlyLeft.Z; }
        else if (rightFloor is { } onlyRight) { rightPlanted = true; zRaw = onlyRight.Z; }
        else
        {
            // 足首が見えない: 骨の長さから距離を出し、骨盤（無ければ首）の奥行きを体の面にする
            double? depth = null;
            void Candidate(double lengthMeters, double pixels)
            {
                if (pixels < 5) return;
                double estimate = camera.Fx * lengthMeters / pixels;
                if (depth is null || estimate < depth) depth = estimate;
            }
            bool hasNeck = Get(Joint.Neck, out var nk) || GetMid(Joint.LeftShoulder, Joint.RightShoulder, out nk);
            bool hasPelvis = Get(Joint.Pelvis, out var pv) || GetMid(Joint.LeftHip, Joint.RightHip, out pv);
            if (hasNeck && hasPelvis) Candidate(body.Torso * h, Vector2.Distance(nk, pv));
            Candidate(body.Thigh * h, MaxBoneLength(Get, Joint.LeftHip, Joint.LeftKnee, Joint.RightHip, Joint.RightKnee));
            if (depth is null || !(hasPelvis || hasNeck)) return null;
            var refPoint = hasPelvis ? pv : nk;
            zRaw = (origin + toTracking.Transform(camera.Unproject(refPoint.X, refPoint.Y, depth.Value))).Z;
        }

        double distance = _planeFilter.Filter(System.Math.Clamp(-zRaw, 0.5, 10.0), time);
        double zPlane = -distance;

        // ---- 2. 体の向き ----
        double yaw = 0;
        if (!forceFrontal && Get(Joint.LeftShoulder, out var ls) && Get(Joint.RightShoulder, out var rs))
        {
            double widthM = System.Math.Abs(AtDepth(ls, zPlane).X - AtDepth(rs, zPlane).X);
            double ratio = widthM / (body.ShoulderWidth * h) / System.Math.Max(0.3, shoulderRatioRef);
            double mag = System.Math.Acos(System.Math.Clamp(ratio, 0, 1));
            mag = mag <= YawDeadZoneRad ? 0 : (mag - YawDeadZoneRad) * (System.Math.PI / 2) / (System.Math.PI / 2 - YawDeadZoneRad);
            if (Get(Joint.Nose, out var nose))
            {
                // 鼻が肩の中心より画像の左にあれば、人は自分の右（トラッキング座標の +X）を向いている
                double shift = (nose.X - (ls.X + rs.X) * 0.5) / System.Math.Max(1.0, System.Math.Abs(ls.X - rs.X));
                if (shift < -YawSignSwitch) _yawSign = 1;
                else if (shift > YawSignSwitch) _yawSign = -1;
            }
            yaw = _yawSign * mag;
        }
        yaw = _yawFilter.Filter(yaw, time);
        double sinYaw = System.Math.Sin(yaw);

        // ---- 3. 関節ごとの奥行き（体の面から、向きや膝の出ぶんだけ前後にずらす。Z が大きいほどカメラに近い） ----
        var z = new double[Enum.GetValues<Joint>().Length];
        Array.Fill(z, zPlane);
        double halfHip = body.HipWidth * h * 0.5;
        double halfShoulder = body.ShoulderWidth * h * 0.5;
        z[(int)Joint.LeftHip] = zPlane + halfHip * sinYaw;
        z[(int)Joint.RightHip] = zPlane - halfHip * sinYaw;
        z[(int)Joint.LeftShoulder] = zPlane + halfShoulder * sinYaw;
        z[(int)Joint.RightShoulder] = zPlane - halfShoulder * sinYaw;

        var onFloor = new Dictionary<Joint, Vector3>();
        void Leg(Joint hip, Joint knee, Joint ankle, Joint heel, Joint bigToe, Joint smallToe, bool planted, Vector3? ankleOnFloor)
        {
            if (planted && ankleOnFloor is { } a)
            {
                // 床に着いている足: 足首・かかと・つま先は床の近くの面との交点。膝は股関節と足首の間の奥行き
                onFloor[ankle] = a;
                z[(int)knee] = (z[(int)hip] + a.Z) * 0.5;
                foreach (var (j, ratio) in new[]
                {
                    (heel, CameraPoseEstimator.HeelHeightRatio),
                    (bigToe, CameraPoseEstimator.ToeHeightRatio),
                    (smallToe, CameraPoseEstimator.ToeHeightRatio),
                })
                {
                    if (Get(j, out var pj) && AtHeight(pj, ratio * h) is { } q) onFloor[j] = q;
                    else z[(int)j] = a.Z;
                }
                return;
            }

            // 上げている足: 太ももが縮んで見える分だけ膝が前に出ているとみなし、足首・かかとは膝と同じ奥行きにする
            double kneeZ = z[(int)hip];
            if (Get(hip, out var ph) && Get(knee, out var pk))
            {
                double observed = Vector3.Distance(AtDepth(ph, z[(int)hip]), AtDepth(pk, z[(int)hip]));
                kneeZ += ForwardOffset(observed, body.Thigh * h);
            }
            z[(int)knee] = kneeZ;
            z[(int)ankle] = kneeZ;
            z[(int)heel] = kneeZ;
            double toeZ = kneeZ;
            if (Get(heel, out var pHeel) && (GetMid(bigToe, smallToe, out var pToe) || Get(bigToe, out pToe)))
            {
                // つま先は、足の甲が縮んで見える分だけカメラ側にある
                double observed = Vector3.Distance(AtDepth(pHeel, kneeZ), AtDepth(pToe, kneeZ));
                toeZ += ForwardOffset(observed, body.Foot * h);
            }
            z[(int)bigToe] = toeZ;
            z[(int)smallToe] = toeZ;
        }
        Leg(Joint.LeftHip, Joint.LeftKnee, Joint.LeftAnkle, Joint.LeftHeel, Joint.LeftBigToe, Joint.LeftSmallToe, leftPlanted, leftFloor);
        Leg(Joint.RightHip, Joint.RightKnee, Joint.RightAnkle, Joint.RightHeel, Joint.RightBigToe, Joint.RightSmallToe, rightPlanted, rightFloor);

        // ---- 4. 3D の姿勢 ----
        var pose = new Pose3D { BodyDepth = distance, BodyYawDeg = yaw * 180.0 / System.Math.PI };
        for (int i = 0; i < layout.Count; i++)
        {
            if (!points[i].Valid) continue;
            var joint = layout.Joints[i];
            pose.Set(joint, onFloor.TryGetValue(joint, out var q)
                ? q
                : AtDepth(new Vector2(points[i].X, points[i].Y), z[(int)joint]));
        }
        return pose;
    }

    private delegate bool Getter(Joint j, out Vector2 p);

    private static double MaxBoneLength(Getter get, Joint a1, Joint b1, Joint a2, Joint b2)
    {
        double len = 0;
        if (get(a1, out var p1) && get(b1, out var q1)) len = Vector2.Distance(p1, q1);
        if (get(a2, out var p2) && get(b2, out var q2)) len = System.Math.Max(len, Vector2.Distance(p2, q2));
        return len;
    }

    public void Reset()
    {
        _depthFilter?.Reset();
        _planeFilter?.Reset();
        _yawFilter?.Reset();
        _yawSign = 1;
    }
}
