using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

public class CalibratorTests
{
    // 身長 1.7m の直立姿勢（トラッキング座標、+Y 上）。既知の骨の長さから比率を検算する。
    internal static Pose3D StandingPose(double yawDeg = 0)
    {
        var pose = new Pose3D { BodyYawDeg = yawDeg };
        pose.Set(Joint.LeftShoulder, new Vector3(-0.20f, 1.40f, 0));
        pose.Set(Joint.RightShoulder, new Vector3(0.20f, 1.40f, 0));
        pose.Set(Joint.Neck, new Vector3(0, 1.40f, 0));
        pose.Set(Joint.Pelvis, new Vector3(0, 0.90f, 0));
        pose.Set(Joint.LeftHip, new Vector3(-0.10f, 0.90f, 0));
        pose.Set(Joint.RightHip, new Vector3(0.10f, 0.90f, 0));
        pose.Set(Joint.LeftKnee, new Vector3(-0.10f, 0.45f, 0));
        pose.Set(Joint.RightKnee, new Vector3(0.10f, 0.45f, 0));
        pose.Set(Joint.LeftAnkle, new Vector3(-0.10f, 0.05f, 0));
        pose.Set(Joint.RightAnkle, new Vector3(0.10f, 0.05f, 0));
        pose.Set(Joint.LeftHeel, new Vector3(-0.10f, 0.0f, -0.05f));
        pose.Set(Joint.RightHeel, new Vector3(0.10f, 0.0f, -0.05f));
        pose.Set(Joint.LeftBigToe, new Vector3(-0.10f, 0.0f, 0.15f));
        pose.Set(Joint.RightBigToe, new Vector3(0.10f, 0.0f, 0.15f));
        pose.Set(Joint.LeftEar, new Vector3(-0.05f, 1.60f, 0));
        pose.Set(Joint.RightEar, new Vector3(0.05f, 1.60f, 0));
        return pose;
    }

    [Fact]
    public void 既知の姿勢から比率を測る()
    {
        var c = new Calibrator();
        for (int i = 0; i < 3; i++) c.Add(StandingPose());
        var cal = c.Build(1.7);

        Assert.NotNull(cal);
        var b = cal!.Body;
        Assert.Equal(0.40 / 1.7, b.ShoulderWidth, 3); // 肩幅 0.40m
        Assert.Equal(0.20 / 1.7, b.HipWidth, 3);      // 腰幅 0.20m
        Assert.Equal(0.50 / 1.7, b.Torso, 3);         // 首〜骨盤 0.50m
        Assert.Equal(0.45 / 1.7, b.Thigh, 3);         // 股関節〜膝 0.45m
        Assert.Equal(0.40 / 1.7, b.Shin, 3);          // 膝〜足首 0.40m
        Assert.Equal(0.20 / 1.7, b.Foot, 3);          // かかと〜つま先 0.20m
    }

    [Fact]
    public void 床の高さは足の最低点()
    {
        var c = new Calibrator();
        for (int i = 0; i < 5; i++) c.Add(StandingPose());
        var cal = c.Build(1.7);

        Assert.NotNull(cal);
        Assert.True(cal!.HasFloor);
        Assert.Equal(0.0f, cal.FloorY, 1e-4f); // かかと・つま先の Y
    }

    [Fact]
    public void 正面方向のヨーは中央値()
    {
        var c = new Calibrator();
        c.Add(StandingPose(8));
        c.Add(StandingPose(10));
        c.Add(StandingPose(12));
        var cal = c.Build(1.7);

        Assert.NotNull(cal);
        Assert.Equal(10, cal!.YawOffsetDeg, 3);
    }

    [Fact]
    public void 体が見えなければ結果は出ない()
    {
        var c = new Calibrator();
        c.Add(new Pose3D()); // 関節なし
        Assert.Null(c.Build(1.7));
    }
}

public class CalibrationApplyTests
{
    private static TrackerFrame MakeFrame(Vector3 hip, Vector3 leftFoot)
    {
        return new TrackerFrame
        {
            Trackers =
            [
                new TrackerPose(TrackerRole.Hip, 1, hip, Quaternion.Identity, Vector3.Zero, true),
                new TrackerPose(TrackerRole.LeftFoot, 2, leftFoot, Quaternion.Identity, Vector3.Zero, true),
            ],
            HeadPosition = new Vector3(0, 1.6f, 0),
            HeadValid = true,
            CapturedTimestamp = 0,
        };
    }

    [Fact]
    public void 床オフセットで足が床に着く()
    {
        // 床が Y=0.9 の位置にあると測定された → 全体を 0.9 下げる
        var cal = new CalibrationData(BodyProportions.Default, 0.9f, 0);
        var frame = cal.Apply(MakeFrame(new Vector3(0, 0.9f, 0), new Vector3(0.1f, 0.9f, 0)));

        Assert.Equal(0.0f, frame.Trackers[0].Position.Y, 1e-4f);
        Assert.Equal(0.0f, frame.Trackers[1].Position.Y, 1e-4f);
        Assert.Equal(0.7f, frame.HeadPosition.Y, 1e-4f); // 1.6 - 0.9
    }

    [Fact]
    public void ヨー補正は腰を軸に回す()
    {
        // 正面方向が 90°（右向き）と測定された → 全体を -90° 回して +Z 正面にそろえる
        var cal = new CalibrationData(BodyProportions.Default, float.NaN, 90);
        var frame = cal.Apply(MakeFrame(Vector3.Zero, new Vector3(1, 0, 0)));

        // 腰は軸なので動かない
        Assert.Equal(0, frame.Trackers[0].Position.X, 1e-4f);
        // 腰から見て +X にあった左足は +Z へ回る
        var lf = frame.Trackers[1].Position;
        Assert.Equal(0, lf.X, 1e-4f);
        Assert.Equal(1, lf.Z, 1e-4f);
    }
}
