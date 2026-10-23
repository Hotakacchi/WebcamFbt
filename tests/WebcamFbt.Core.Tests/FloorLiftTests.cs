using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

/// <summary>
/// 床の面を使った単眼の 3D 化の検算。見下ろしたカメラ（高さ 2.1m・下向き 28°）で、
/// 床の上の座標（カメラの真下が原点、+Y 上、カメラは -Z を向く）に置いた体を写して、元に戻せるかを見る。
/// </summary>
public class FloorLiftTests
{
    private const double Height = 1.7;
    private static readonly CameraIntrinsics Camera = CameraIntrinsics.FromHorizontalFov(1280, 720, 70);
    private static readonly CameraPose TruePose = new(2.1, 28, 0);
    private static readonly KeypointLayout Layout = KeypointLayout.Halpe26;

    /// <summary>床の上の点を画面に写す（<see cref="CameraToTracking.Transform"/> の逆）。</summary>
    private static Vector2 Project(Vector3 world, CameraPose pose)
    {
        double tx = world.X, ty = world.Y - pose.HeightMeters, tz = world.Z;
        double t = pose.PitchDownDeg * System.Math.PI / 180.0;
        double c = System.Math.Cos(t), s = System.Math.Sin(t);
        double x = -tx, y1 = ty, z1 = -tz;
        double y = y1 * c + z1 * s;
        double z = -y1 * s + z1 * c;
        double camY = -y;
        return new Vector2((float)(Camera.Cx + Camera.Fx * x / z), (float)(Camera.Cy + Camera.Fy * camY / z));
    }

    /// <summary>
    /// カメラから水平に distance 離れて立つ体（各部の高さは <see cref="CameraPoseEstimator"/> の比どおり）。
    /// yawDeg だけ自分の右（+X）を向く。rightFoot を指定すると右の足首・かかと・つま先・膝をそこへ動かす。
    /// </summary>
    private static Dictionary<Joint, Vector3> Body(double distance, double yawDeg = 0, Vector3? rightAnkle = null, Vector3? rightKnee = null)
    {
        float h = (float)Height;
        float zc = (float)-distance;
        double yaw = yawDeg * System.Math.PI / 180.0;
        // 体の正面基準の (右, 上, 前) をトラッキング座標へ（前 = +Z を yaw だけ +X 側へ回す）
        Vector3 At(float right, float up, float forward)
        {
            float c = (float)System.Math.Cos(yaw), s = (float)System.Math.Sin(yaw);
            return new Vector3(right * c + forward * s, up, zc - right * s + forward * c);
        }
        float sh = 0.11f * h, hp = 0.07f * h;
        var j = new Dictionary<Joint, Vector3>
        {
            [Joint.Nose] = At(0, 0.92f * h, 0.10f),
            [Joint.LeftEar] = At(-0.07f, (float)CameraPoseEstimator.EarHeightRatio * h, 0),
            [Joint.RightEar] = At(0.07f, (float)CameraPoseEstimator.EarHeightRatio * h, 0),
            [Joint.LeftShoulder] = At(-sh, (float)CameraPoseEstimator.ShoulderHeightRatio * h, 0),
            [Joint.RightShoulder] = At(sh, (float)CameraPoseEstimator.ShoulderHeightRatio * h, 0),
            [Joint.Neck] = At(0, (float)CameraPoseEstimator.ShoulderHeightRatio * h, 0),
            [Joint.LeftHip] = At(-hp, (float)CameraPoseEstimator.HipHeightRatio * h, 0),
            [Joint.RightHip] = At(hp, (float)CameraPoseEstimator.HipHeightRatio * h, 0),
            [Joint.Pelvis] = At(0, (float)CameraPoseEstimator.HipHeightRatio * h, 0),
            [Joint.LeftKnee] = At(-hp, (float)CameraPoseEstimator.KneeHeightRatio * h, 0),
            [Joint.RightKnee] = At(hp, (float)CameraPoseEstimator.KneeHeightRatio * h, 0),
            [Joint.LeftAnkle] = At(-hp, (float)CameraPoseEstimator.AnkleHeightRatio * h, 0),
            [Joint.RightAnkle] = At(hp, (float)CameraPoseEstimator.AnkleHeightRatio * h, 0),
            [Joint.LeftHeel] = At(-hp, (float)CameraPoseEstimator.HeelHeightRatio * h, -0.05f),
            [Joint.RightHeel] = At(hp, (float)CameraPoseEstimator.HeelHeightRatio * h, -0.05f),
            [Joint.LeftBigToe] = At(-hp, (float)CameraPoseEstimator.ToeHeightRatio * h, 0.18f),
            [Joint.RightBigToe] = At(hp, (float)CameraPoseEstimator.ToeHeightRatio * h, 0.18f),
        };
        if (rightAnkle is { } a)
        {
            j[Joint.RightAnkle] = a;
            j[Joint.RightHeel] = a + new Vector3(0, -0.03f, -0.05f);
            j[Joint.RightBigToe] = a + new Vector3(0, -0.03f, 0.18f);
        }
        if (rightKnee is { } k) j[Joint.RightKnee] = k;
        return j;
    }

    private static KeypointGate.GatedPoint[] Points(Dictionary<Joint, Vector3> body, CameraPose pose)
    {
        var points = new KeypointGate.GatedPoint[Layout.Count];
        foreach (var (joint, world) in body)
        {
            int i = Layout.IndexOf(joint);
            if (i < 0) continue;
            var p = Project(world, pose);
            points[i] = new KeypointGate.GatedPoint(p.X, p.Y, true, false);
        }
        return points;
    }

    private static TrackingSettings Settings() => new() { HeightMeters = Height };

    private static Pose3D Lift(MonocularLifter lifter, KeypointGate.GatedPoint[] points, CameraPose pose, int frame, double shoulderRef = 1.0)
    {
        var toTracking = new CameraToTracking { PitchDownDeg = pose.PitchDownDeg };
        var body = BodyProportions.Default with { ShoulderWidth = 0.22 };
        var result = lifter.Lift(points, Layout, Camera, toTracking, Settings(), body, frame / 30.0, floor: pose, shoulderRatioRef: shoulderRef);
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public void 立ち姿からカメラの高さと傾きを求める()
    {
        var frames = Enumerable.Range(0, 10).Select(_ => Points(Body(2.0), TruePose)).ToList();
        var pose = CameraPoseEstimator.Estimate(frames, Layout, Camera, Height);

        Assert.NotNull(pose);
        Assert.Equal(2.1, pose!.HeightMeters, 0.05);
        Assert.Equal(28, pose.PitchDownDeg, 1.0);
        Assert.True(pose.FitErrorPx < 1);
    }

    [Fact]
    public void 足首が写っていなければカメラの置き方は求めない()
    {
        var body = Body(2.0);
        body.Remove(Joint.LeftAnkle);
        body.Remove(Joint.RightAnkle);
        var frames = Enumerable.Range(0, 5).Select(_ => Points(body, TruePose)).ToList();
        Assert.Null(CameraPoseEstimator.Estimate(frames, Layout, Camera, Height));
    }

    [Fact]
    public void 見下ろしても立ち姿の大きさが正しい()
    {
        var lifter = new MonocularLifter();
        var points = Points(Body(2.0), TruePose);
        Pose3D p = null!;
        for (int i = 0; i < 10; i++) p = Lift(lifter, points, TruePose, i);

        Assert.True(p.TryGet(Joint.LeftAnkle, out var ankle));
        Assert.True(p.TryGet(Joint.LeftEar, out var ear));
        Assert.True(p.TryGet(Joint.Pelvis, out var pelvis));
        Assert.Equal(CameraPoseEstimator.AnkleHeightRatio * Height, ankle.Y, 0.01);
        Assert.Equal(-2.0, ankle.Z, 0.02);
        // 耳〜足首の縦 1.51m、骨盤〜足首 0.82m（遠近で縮まない）
        Assert.Equal((CameraPoseEstimator.EarHeightRatio - CameraPoseEstimator.AnkleHeightRatio) * Height, ear.Y - ankle.Y, 0.02);
        Assert.Equal((CameraPoseEstimator.HipHeightRatio - CameraPoseEstimator.AnkleHeightRatio) * Height, pelvis.Y - ankle.Y, 0.02);
        Assert.Equal(0, p.BodyYawDeg, 1.0);
    }

    [Fact]
    public void 片足を上げても立っている足は床に残る()
    {
        var lifter = new MonocularLifter();
        var standing = Points(Body(2.0), TruePose);
        for (int i = 0; i < 10; i++) Lift(lifter, standing, TruePose, i);

        // 右の膝を前に上げ、足首を 0.4m 持ち上げる（単眼では前後の出が粗いので、上がる量は少なめに出る）
        float h = (float)Height;
        var raised = Points(Body(2.0,
            rightAnkle: new Vector3(0.07f * h, 0.40f, -1.85f),
            rightKnee: new Vector3(0.07f * h, 0.62f, -1.65f)), TruePose);
        Pose3D p = null!;
        for (int i = 10; i < 20; i++) p = Lift(lifter, raised, TruePose, i);

        Assert.True(p.TryGet(Joint.LeftAnkle, out var left));
        Assert.True(p.TryGet(Joint.RightAnkle, out var right));
        Assert.True(p.TryGet(Joint.Pelvis, out var pelvis));
        Assert.Equal(CameraPoseEstimator.AnkleHeightRatio * Height, left.Y, 0.01); // 床に着いたまま
        Assert.Equal(-2.0, left.Z, 0.03);
        Assert.True(right.Y - left.Y > 0.12, $"上げた足が上がっていない: {right.Y - left.Y:F2}");
        Assert.Equal(CameraPoseEstimator.HipHeightRatio * Height, pelvis.Y, 0.03); // 体は浮かない
    }

    [Fact]
    public void 正面付近の小さな揺れでは向きが振れない()
    {
        var lifter = new MonocularLifter();
        double maxYaw = 0;
        for (int i = 0; i < 60; i++)
        {
            // 肩幅が ±3% 揺れ、鼻が少し左右にぶれる
            var body = Body(2.0);
            float k = 1 + 0.03f * (i % 2 == 0 ? 1 : -1);
            body[Joint.LeftShoulder] *= new Vector3(k, 1, 1);
            body[Joint.RightShoulder] *= new Vector3(k, 1, 1);
            body[Joint.Nose] += new Vector3(i % 3 == 0 ? 0.02f : -0.02f, 0, 0);
            var p = Lift(lifter, Points(body, TruePose), TruePose, i);
            maxYaw = System.Math.Max(maxYaw, System.Math.Abs(p.BodyYawDeg));
        }
        Assert.True(maxYaw < 3, $"向きが振れた: {maxYaw:F1}°");
    }

    [Fact]
    public void 横を向くと向きが出て左右も合う()
    {
        foreach (int sign in new[] { 1, -1 })
        {
            var lifter = new MonocularLifter();
            var points = Points(Body(2.0, yawDeg: 60 * sign), TruePose);
            Pose3D p = null!;
            for (int i = 0; i < 60; i++) p = Lift(lifter, points, TruePose, i);
            Assert.Equal(60 * sign, p.BodyYawDeg, 15.0);
        }
    }

    [Fact]
    public void 立ち姿の2Dから比率とカメラの置き方をまとめて求める()
    {
        var frames = Enumerable.Range(0, 20).Select(_ => Points(Body(2.0), TruePose)).ToList();
        var cal = MonoCalibration.Build(frames, Layout, Camera, Settings());

        Assert.NotNull(cal);
        Assert.NotNull(cal!.Camera);
        Assert.Equal(2.1, cal.Camera!.HeightMeters, 0.05);
        Assert.False(cal.HasFloor);   // 床は最初から Y0
        Assert.Equal(0, cal.YawOffsetDeg);
        Assert.True(cal.IsCalibrated);
        // 太もも・すね・胴は各部の高さの差どおり（見下ろしで縮まない）
        Assert.Equal(CameraPoseEstimator.HipHeightRatio - CameraPoseEstimator.KneeHeightRatio, cal.Body.Thigh, 0.01);
        Assert.Equal(CameraPoseEstimator.KneeHeightRatio - CameraPoseEstimator.AnkleHeightRatio, cal.Body.Shin, 0.01);
        Assert.Equal(CameraPoseEstimator.ShoulderHeightRatio - CameraPoseEstimator.HipHeightRatio, cal.Body.Torso, 0.01);
        Assert.Equal(1.0, cal.ShoulderRatioRef!.Value, 0.02);
        // 高い・下向きのカメラなので置き方の注意が出る
        Assert.Contains(cal.Warnings!, w => w.Contains("カメラが高い"));
    }

    [Fact]
    public void ありえない比率は既定値に戻して注意を出す()
    {
        // 記録で見つかった壊れたキャリブレーション（すね 0.117・太もも 0.179）
        var broken = BodyProportions.Default with { Shin = 0.117, Thigh = 0.179 };
        var warnings = new List<string>();
        var fixedBody = Calibrator.CheckPlausible(broken, warnings);

        Assert.Equal(BodyProportions.Default.Shin, fixedBody.Shin);
        Assert.Equal(BodyProportions.Default.Thigh, fixedBody.Thigh);
        Assert.Equal(BodyProportions.Default.Torso, fixedBody.Torso);
        Assert.Single(warnings);
        Assert.Contains("すね", warnings[0]);

        var ok = new List<string>();
        Assert.Same(BodyProportions.Default, Calibrator.CheckPlausible(BodyProportions.Default, ok));
        Assert.Empty(ok);
    }
}
