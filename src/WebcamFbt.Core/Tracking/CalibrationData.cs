using System.Numerics;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// キャリブレーションの結果。測定した体の比率、床の高さ、正面方向のヨー補正を持つ。
/// 未キャリブレーション時は <see cref="None"/>（既定の比率を使い、床・向きの補正はしない）。
/// </summary>
/// <param name="Body">実測で置き換えた体の比率。</param>
/// <param name="FloorY">床面のトラッキング Y 座標 [m]。この値を差し引いて「床 = Y0」にする。未測定なら NaN。</param>
/// <param name="YawOffsetDeg">キャリブレーション時に向いていた方向 [度]。これを差し引いて正面（+Z）にそろえる。</param>
public sealed record CalibrationData(
    BodyProportions Body,
    float FloorY,
    double YawOffsetDeg)
{
    /// <summary>未キャリブレーション（既定の比率を使い、床・向きの補正はしない）。</summary>
    public static CalibrationData None { get; } = new(BodyProportions.Default, float.NaN, 0);

    /// <summary>床の高さが測定済みか。</summary>
    public bool HasFloor => !float.IsNaN(FloorY);

    /// <summary>既定値のまま（キャリブレーションしていない）か。</summary>
    public bool IsCalibrated => !ReferenceEquals(Body, BodyProportions.Default) || HasFloor || YawOffsetDeg != 0;

    /// <summary>
    /// トラッカーの結果に床の高さとヨー補正を適用する。
    /// 床を Y0 にそろえ、キャリブレーション時に向いていた方向を正面（+Z）にする。
    /// ヨー補正は腰の位置を軸にその場で回し、位置・回転の両方に反映する。
    /// </summary>
    public TrackerFrame Apply(TrackerFrame frame)
    {
        bool doYaw = System.Math.Abs(YawOffsetDeg) > 1e-6;
        bool doFloor = HasFloor;
        if (!doYaw && !doFloor) return frame;

        // ヨー回転の軸（腰の位置。見えなければ原点）。その場で体を回す
        var pivot = Vector3.Zero;
        foreach (var t in frame.Trackers)
        {
            if (t is { Role: TrackerRole.Hip, Valid: true }) { pivot = t.Position; break; }
        }

        var yaw = doYaw
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(-YawOffsetDeg * System.Math.PI / 180.0))
            : Quaternion.Identity;
        float floor = doFloor ? FloorY : 0f;

        Vector3 Fix(Vector3 p)
        {
            if (doYaw) p = pivot + Vector3.Transform(p - pivot, yaw);
            return new Vector3(p.X, p.Y - floor, p.Z);
        }

        var trackers = new TrackerPose[frame.Trackers.Count];
        for (int i = 0; i < trackers.Length; i++)
        {
            var t = frame.Trackers[i];
            if (!t.Valid) { trackers[i] = t; continue; }
            var rot = doYaw ? Quaternion.Normalize(yaw * t.Rotation) : t.Rotation;
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
