using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 3D の姿勢から、腰・両足のトラッカーの位置と回転、頭の位置を求めて平滑化する。
///
/// - 腰: 位置 = 左右の股関節の中点。回転 = 「左股関節 → 右股関節」を右、「骨盤 → 首」を上とする向き
/// - 足: 位置 = 足首。回転 = 「かかと → つま先」を前、「足首 → 膝」を上とする向き
/// - 頭: 位置 = 両耳の中点（見えなければ両目の中点、鼻の順）
///
/// VRChat 側でキャリブレーション（T ポーズ）したときに、トラッカーとアバターの骨のずれが記録されるので、
/// 回転の絶対的な向きより「動きに合わせて一貫して回ること」の方が重要。
/// </summary>
public sealed class TrackerSolver
{
    private sealed class Channel(OneEuroParams posParams, OneEuroParams rotParams)
    {
        public OneEuroFilterVector3 Position { get; } = new(posParams);
        public OneEuroFilterRotation Rotation { get; } = new(rotParams);
        public Quaternion LastRotation { get; set; } = Quaternion.Identity;
        public void Reset()
        {
            Position.Reset();
            Rotation.Reset();
            LastRotation = Quaternion.Identity;
        }
    }

    private readonly Channel _hip;
    private readonly Channel _leftFoot;
    private readonly Channel _rightFoot;
    private readonly OneEuroFilterVector3 _head;

    public TrackerSolver(TrackingSettings settings)
    {
        _hip = new Channel(settings.PositionFilter, settings.RotationFilter);
        _leftFoot = new Channel(settings.PositionFilter, settings.RotationFilter);
        _rightFoot = new Channel(settings.PositionFilter, settings.RotationFilter);
        _head = new OneEuroFilterVector3(settings.PositionFilter);
    }

    public TrackerFrame Solve(Pose3D pose, double time, long capturedTimestamp)
    {
        // ---- 腰 ----
        var up = Vector3.UnitY;
        var hipForward = Vector3.UnitZ;
        TrackerPose hip;
        if (pose.TryGet(Joint.LeftHip, out var lh) && pose.TryGet(Joint.RightHip, out var rh))
        {
            var hipCenter = (lh + rh) * 0.5f;
            if (pose.TryGet(Joint.Neck, out var neck) || pose.TryGetMid(Joint.LeftShoulder, Joint.RightShoulder, out neck))
            {
                up = neck - hipCenter;
            }
            var rot = Rotation.FromRightUp(rh - lh, up);
            hip = Output(_hip, TrackerRole.Hip, 1, hipCenter, rot, time);
            hipForward = Vector3.Transform(Vector3.UnitZ, hip.Rotation);
        }
        else
        {
            hip = Invalid(TrackerRole.Hip, 1);
        }

        // ---- 足 ----
        var leftFoot = SolveFoot(pose, _leftFoot, TrackerRole.LeftFoot, 2,
            Joint.LeftAnkle, Joint.LeftKnee, Joint.LeftHeel, Joint.LeftBigToe, Joint.LeftSmallToe, hipForward, time);
        var rightFoot = SolveFoot(pose, _rightFoot, TrackerRole.RightFoot, 3,
            Joint.RightAnkle, Joint.RightKnee, Joint.RightHeel, Joint.RightBigToe, Joint.RightSmallToe, hipForward, time);

        // ---- 頭 ----
        bool headValid = pose.TryGetMid(Joint.LeftEar, Joint.RightEar, out var head)
            || pose.TryGetMid(Joint.LeftEye, Joint.RightEye, out head)
            || pose.TryGet(Joint.Nose, out head);
        if (headValid) head = _head.Filter(head, time);

        return new TrackerFrame
        {
            Trackers = [hip, leftFoot, rightFoot],
            HeadPosition = head,
            HeadValid = headValid,
            CapturedTimestamp = capturedTimestamp,
            BodyDepth = pose.BodyDepth,
            BodyYawDeg = pose.BodyYawDeg,
        };
    }

    private static TrackerPose SolveFoot(Pose3D pose, Channel channel, TrackerRole role, int oscIndex,
        Joint ankle, Joint knee, Joint heel, Joint bigToe, Joint smallToe, Vector3 fallbackForward, double time)
    {
        if (!pose.TryGet(ankle, out var pAnkle)) return Invalid(role, oscIndex);

        // 上方向: すねの向き（膝が見えなければ真上）
        var upHint = pose.TryGet(knee, out var pKnee) ? pKnee - pAnkle : Vector3.UnitY;

        // 前方向: かかと → つま先（見えなければ腰の向き）
        var forward = fallbackForward;
        if (pose.TryGet(heel, out var pHeel)
            && (pose.TryGetMid(bigToe, smallToe, out var pToe) || pose.TryGet(bigToe, out pToe)))
        {
            forward = pToe - pHeel;
        }

        var rot = Rotation.FromForwardUp(forward, upHint)
            ?? Rotation.FromForwardUp(fallbackForward, upHint); // つま先が真上・真下を向いているときなど
        return Output(channel, role, oscIndex, pAnkle, rot, time);
    }

    private static TrackerPose Output(Channel ch, TrackerRole role, int oscIndex, Vector3 position, Quaternion? rotation, double time)
    {
        var p = ch.Position.Filter(position, time);
        // 回転が求められなかったフレームは、直前の回転を使う
        var q = rotation.HasValue ? ch.Rotation.Filter(rotation.Value, time) : ch.LastRotation;
        ch.LastRotation = q;
        return new TrackerPose(role, oscIndex, p, q, Rotation.ToUnityEuler(q), true);
    }

    private static TrackerPose Invalid(TrackerRole role, int oscIndex)
        => new(role, oscIndex, Vector3.Zero, Quaternion.Identity, Vector3.Zero, false);

    public void Reset()
    {
        _hip.Reset();
        _leftFoot.Reset();
        _rightFoot.Reset();
        _head.Reset();
    }
}
