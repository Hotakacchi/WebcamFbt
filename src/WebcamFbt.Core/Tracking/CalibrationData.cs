using System.Numerics;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// キャリブレーションの結果。測定した体の比率、床の高さ、正面方向のヨー補正、（ステレオでは）カメラの傾きの補正を持つ。
/// 未キャリブレーション時は <see cref="None"/>（既定の比率を使い、床・向き・傾きの補正はしない）。
/// </summary>
/// <param name="Body">実測で置き換えた体の比率。</param>
/// <param name="FloorY">床面のトラッキング Y 座標 [m]（傾きを補正した後の座標）。この値を差し引いて「床 = Y0」にする。未測定なら NaN。</param>
/// <param name="YawOffsetDeg">キャリブレーション時に向いていた方向 [度]。これを差し引いて正面（+Z）にそろえる。</param>
/// <param name="Tilt">
/// カメラの傾き（ピッチ・ロール）の補正。まっすぐ立った体の縦の向きを真上に重ねる回転で、空間全体にかける。
/// ステレオでキャリブレーションしたときだけ求める（単眼では null）。
/// </param>
/// <param name="Camera">
/// カメラの置き方（床からの高さと下向きの傾き）。単眼でキャリブレーションしたときに立ち姿から求める。
/// あれば単眼の 3D 化は床の面を使い、座標は最初から床が Y0 になる（<paramref name="FloorY"/> は使わない）。
/// </param>
/// <param name="ShoulderRatioRef">キャリブレーションのときの正面での肩幅の見え方（単眼で体の向きを出す基準。1 = 比率どおり）。</param>
/// <param name="Warnings">キャリブレーションのときに気づいた問題（測り直した比率・カメラの置き方など）。表示用。</param>
public sealed record CalibrationData(
    BodyProportions Body,
    float FloorY,
    double YawOffsetDeg,
    Quaternion? Tilt = null,
    CameraPose? Camera = null,
    double? ShoulderRatioRef = null,
    IReadOnlyList<string>? Warnings = null)
{
    /// <summary>未キャリブレーション（既定の比率を使い、床・向き・傾きの補正はしない）。</summary>
    public static CalibrationData None { get; } = new(BodyProportions.Default, float.NaN, 0);

    /// <summary>床の高さが測定済みか。</summary>
    public bool HasFloor => !float.IsNaN(FloorY);

    /// <summary>既定値のまま（キャリブレーションしていない）か。</summary>
    public bool IsCalibrated =>
        !ReferenceEquals(Body, BodyProportions.Default) || HasFloor || YawOffsetDeg != 0 || Tilt is not null || Camera is not null;

    /// <summary>傾きの補正の大きさ [度]（補正なしなら 0）。</summary>
    public double TiltDeg => Tilt is { } q ? 2 * System.Math.Acos(System.Math.Min(1, System.Math.Abs(q.W))) * 180.0 / System.Math.PI : 0;

    /// <summary>
    /// トラッカーの結果に補正をかける。順番は
    ///   1. 傾き: 空間全体を回して、体の縦の向きを真上にそろえる（原点＝カメラ 1 のまわり）
    ///   2. 向き: キャリブレーション時に向いていた方向を正面（+Z）にする（腰を軸にその場で回す）
    ///   3. 床: 床を Y0 にそろえる
    /// 位置・回転の両方に反映する。
    /// </summary>
    public TrackerFrame Apply(TrackerFrame frame)
    {
        bool doTilt = Tilt is not null;
        bool doYaw = System.Math.Abs(YawOffsetDeg) > 1e-6;
        bool doFloor = HasFloor;
        if (!doTilt && !doYaw && !doFloor) return frame;

        var tilt = Tilt ?? Quaternion.Identity;
        var yaw = doYaw
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(-YawOffsetDeg * System.Math.PI / 180.0))
            : Quaternion.Identity;
        float floor = doFloor ? FloorY : 0f;

        // ヨー回転の軸（傾きを補正した後の腰の位置。見えなければ原点）。その場で体を回す
        var pivot = Vector3.Zero;
        foreach (var t in frame.Trackers)
        {
            if (t is { Role: TrackerRole.Hip, Valid: true }) { pivot = Vector3.Transform(t.Position, tilt); break; }
        }

        Vector3 Fix(Vector3 p)
        {
            if (doTilt) p = Vector3.Transform(p, tilt);
            if (doYaw) p = pivot + Vector3.Transform(p - pivot, yaw);
            return new Vector3(p.X, p.Y - floor, p.Z);
        }

        // 回転は「傾き → ヨー」の順にかける（System.Numerics の a * b は「b を適用してから a」）
        var correction = Quaternion.Normalize(yaw * tilt);
        bool rotate = doTilt || doYaw;

        var trackers = new TrackerPose[frame.Trackers.Count];
        for (int i = 0; i < trackers.Length; i++)
        {
            var t = frame.Trackers[i];
            if (!t.Valid) { trackers[i] = t; continue; }
            var rot = rotate ? Quaternion.Normalize(correction * t.Rotation) : t.Rotation;
            trackers[i] = t with
            {
                Position = Fix(t.Position),
                Rotation = rot,
                EulerDeg = Geometry.Rotation.ToUnityEuler(rot),
            };
        }

        return new TrackerFrame
        {
            Trackers = trackers,
            HeadPosition = frame.HeadValid ? Fix(frame.HeadPosition) : frame.HeadPosition,
            HeadValid = frame.HeadValid,
            CapturedTimestamp = frame.CapturedTimestamp,
            BodyDepth = frame.BodyDepth,
            BodyYawDeg = frame.BodyYawDeg,
        };
    }
}
