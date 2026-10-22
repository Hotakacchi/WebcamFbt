using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

/// <summary>ステレオでの体のキャリブレーション（カメラの傾きの補正）と、三角測量の精度の目安（フェーズ 4d）。</summary>
public class StereoBodyCalibrationTests
{
    // カメラが下に 10 度、右に 5 度傾いている状況（トラッキング座標の姿勢全体がこの回転で傾いて見える）
    private static readonly Quaternion CameraTilt = Quaternion.Normalize(
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, 10f * MathF.PI / 180)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 5f * MathF.PI / 180));

    private static Pose3D Tilted(Pose3D pose, Quaternion q)
    {
        var result = new Pose3D { BodyDepth = pose.BodyDepth, BodyYawDeg = pose.BodyYawDeg };
        foreach (var j in Enum.GetValues<Joint>())
        {
            if (pose.TryGet(j, out var p)) result.Set(j, Vector3.Transform(p, q));
        }
        return result;
    }

    [Fact]
    public void ステレオでは体の縦の向きからカメラの傾きを補正する()
    {
        var c = new Calibrator();
        for (int i = 0; i < 5; i++) c.Add(Tilted(CalibratorTests.StandingPose(), CameraTilt), stereo: true);
        var cal = c.Build(1.7);

        Assert.NotNull(cal);
        Assert.NotNull(cal!.Tilt);
        // 傾いて見えていた体の縦の向き（足首の中点 → 首）が、補正で真上に戻る
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, CameraTilt));
        var corrected = Vector3.Transform(up, cal.Tilt!.Value);
        Assert.True(Vector3.Distance(Vector3.UnitY, corrected) < 1e-4, $"{corrected}");
        Assert.InRange(cal.TiltDeg, 10.5, 12.0); // 10 度と 5 度を合わせた傾き（約 11.2 度）

        // 床は傾きを補正した後の座標で測る（元の直立姿勢では床 Y=0）
        Assert.Equal(0f, cal.FloorY, 1e-3f);

        // 最小の回転で縦をそろえると、元の傾きの逆とは縦軸まわりに少しねじれが残る。
        // 正面方向（ヨー）の補正がそのねじれを打ち消し、傾き → ヨーの補正後に腰の左右が真横（+X）にそろう
        var pose = Tilted(CalibratorTests.StandingPose(), CameraTilt);
        pose.TryGet(Joint.LeftHip, out var lh);
        pose.TryGet(Joint.RightHip, out var rh);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(-cal.YawOffsetDeg * Math.PI / 180));
        var right = Vector3.Normalize(Vector3.Transform(Vector3.Transform(rh - lh, cal.Tilt.Value), yaw));
        Assert.True(Vector3.Distance(Vector3.UnitX, right) < 1e-3, $"{right}");
    }

    [Fact]
    public void 単眼や単眼の混ざったキャリブレーションでは傾きを求めない()
    {
        var mono = new Calibrator();
        var mixed = new Calibrator();
        for (int i = 0; i < 5; i++)
        {
            mono.Add(Tilted(CalibratorTests.StandingPose(), CameraTilt));
            mixed.Add(Tilted(CalibratorTests.StandingPose(), CameraTilt), stereo: i % 2 == 0);
        }
        Assert.Null(mono.Build(1.7)!.Tilt);
        Assert.Null(mixed.Build(1.7)!.Tilt);
    }

    [Fact]
    public void 傾きの補正は空間全体と回転にかかる()
    {
        // Z 軸まわり 90 度の補正: +X にあった点は +Y へ
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var cal = new CalibrationData(BodyProportions.Default, float.NaN, 0, q);
        var frame = cal.Apply(new TrackerFrame
        {
            Trackers = [new TrackerPose(TrackerRole.Hip, 1, new Vector3(1, 0, 0), Quaternion.Identity, Vector3.Zero, true)],
            HeadPosition = new Vector3(2, 0, 0),
            HeadValid = true,
            CapturedTimestamp = 0,
        });

        var hip = frame.Trackers[0];
        Assert.True(Vector3.Distance(new Vector3(0, 1, 0), hip.Position) < 1e-5);
        Assert.True(Vector3.Distance(new Vector3(0, 2, 0), frame.HeadPosition) < 1e-5);
        Assert.True(OneEuroFilterRotation.AngleBetween(q, hip.Rotation) < 1e-4);
        Assert.True(cal.IsCalibrated);
    }
}

public class StereoStatsTests
{
    [Fact]
    public void 全関節が三角測量できれば誤差は小さく_脚の長さはそろう()
    {
        var skeleton = StereoTriangulatorTests.Skeleton();
        StereoTriangulator.Triangulate(
            StereoTriangulatorTests.View(skeleton, false), StereoTriangulatorTests.View(skeleton, true),
            StereoTriangulatorTests.Layout, StereoTriangulatorTests.Stereo, new CameraToTracking(), 15, out var stats);

        Assert.Equal(26, stats.Candidates);
        Assert.Equal(26, stats.Triangulated);
        Assert.True(stats.MeanReprojErrorPx < 0.05);
        Assert.NotNull(stats.LegLengthDiffMeters);
        Assert.True(stats.LegLengthDiffMeters < 1e-3); // 合成した骨格は左右対称
        Assert.False(stats.Swapped);
    }

    [Fact]
    public void 捨てた関節と左右の入れ替えが統計に出る()
    {
        var layout = StereoTriangulatorTests.Layout;
        var skeleton = StereoTriangulatorTests.Skeleton(raiseLeftLeg: true);
        var view2 = StereoTriangulatorTests.View(skeleton, true);
        var swapped = new KeypointGate.GatedPoint[view2.Length];
        for (int i = 0; i < layout.Count; i++)
        {
            var name = layout.Joints[i].ToString();
            var mirror = name.StartsWith("Left") ? Enum.Parse<Joint>("Right" + name[4..])
                : name.StartsWith("Right") ? Enum.Parse<Joint>("Left" + name[5..])
                : layout.Joints[i];
            swapped[i] = view2[layout.IndexOf(mirror)];
        }
        // さらに鼻だけカメラ 2 で縦にずらす（捨てられるはず）
        int nose = layout.IndexOf(Joint.Nose);
        swapped[nose] = swapped[nose] with { Y = swapped[nose].Y + 60 };

        StereoTriangulator.Triangulate(
            StereoTriangulatorTests.View(skeleton, false), swapped,
            layout, StereoTriangulatorTests.Stereo, new CameraToTracking(), 15, out var stats);

        Assert.True(stats.Swapped);
        Assert.Equal(26, stats.Candidates);
        Assert.Equal(25, stats.Triangulated);
    }
}
