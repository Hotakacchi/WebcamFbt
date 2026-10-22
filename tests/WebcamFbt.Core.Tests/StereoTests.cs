using System.Diagnostics;
using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

/// <summary>
/// 三角測量を合成データで検算する。カメラ 1 の右 0.6m に、20 度内側を向いたカメラ 2 を置き、
/// 3m 先でカメラの方を向いて立つ人の関節を両カメラに写した画像座標（歪みあり）から 3D に戻す。
/// </summary>
public class StereoTriangulatorTests
{
    internal static readonly CameraCalibration Cam = new(1280, 720, 900, 900, 640, 360, [-0.15, 0.05, 0, 0, 0], 0, 0);

    private static readonly Vector3 C2 = new(0.6f, 0, 0);
    private static readonly double A = 20 * Math.PI / 180;
    private static readonly double[] R = [Math.Cos(A), 0, Math.Sin(A), 0, 1, 0, -Math.Sin(A), 0, Math.Cos(A)];

    // T = -R·C2
    internal static readonly StereoCalibration Stereo = new(0, 1, Cam, Cam, R,
        [-(R[0] * C2.X), -(R[3] * C2.X), -(R[6] * C2.X)], 0, 0);

    internal static readonly KeypointLayout Layout = KeypointLayout.Halpe26;

    internal static Vector3 ToCamera2(Vector3 x1)
    {
        var d = x1 - C2;
        return new Vector3(
            (float)(R[0] * d.X + R[1] * d.Y + R[2] * d.Z),
            (float)(R[3] * d.X + R[4] * d.Y + R[5] * d.Z),
            (float)(R[6] * d.X + R[7] * d.Y + R[8] * d.Z));
    }

    internal static Vector2 Project(Vector3 p) => Cam.Distort(new Vector2(
        (float)(Cam.Fx * p.X / p.Z + Cam.Cx), (float)(Cam.Fy * p.Y / p.Z + Cam.Cy)));

    /// <summary>
    /// カメラ 1 座標（+X 右、+Y 下、+Z 奥）の骨格。人はカメラの方を向いているので、体の左側がカメラから見て右（+X）。
    /// </summary>
    internal static Dictionary<Joint, Vector3> Skeleton(bool raiseLeftLeg = false)
    {
        const float z = 3.0f;
        var s = new Dictionary<Joint, Vector3>
        {
            [Joint.HeadTop] = new(0, -0.90f, z), [Joint.Nose] = new(0, -0.75f, z - 0.1f),
            [Joint.LeftEye] = new(0.03f, -0.78f, z - 0.08f), [Joint.RightEye] = new(-0.03f, -0.78f, z - 0.08f),
            [Joint.LeftEar] = new(0.07f, -0.76f, z), [Joint.RightEar] = new(-0.07f, -0.76f, z),
            [Joint.Neck] = new(0, -0.55f, z),
            [Joint.LeftShoulder] = new(0.20f, -0.50f, z), [Joint.RightShoulder] = new(-0.20f, -0.50f, z),
            [Joint.LeftElbow] = new(0.25f, -0.20f, z), [Joint.RightElbow] = new(-0.25f, -0.20f, z),
            [Joint.LeftWrist] = new(0.27f, 0.05f, z - 0.05f), [Joint.RightWrist] = new(-0.27f, 0.05f, z - 0.05f),
            [Joint.Pelvis] = new(0, 0, z),
            [Joint.LeftHip] = new(0.10f, 0, z), [Joint.RightHip] = new(-0.10f, 0, z),
            [Joint.LeftKnee] = new(0.10f, 0.45f, z - 0.02f), [Joint.RightKnee] = new(-0.10f, 0.45f, z - 0.02f),
            [Joint.LeftAnkle] = new(0.10f, 0.88f, z + 0.02f), [Joint.RightAnkle] = new(-0.10f, 0.88f, z + 0.02f),
            [Joint.LeftHeel] = new(0.10f, 0.93f, z + 0.07f), [Joint.RightHeel] = new(-0.10f, 0.93f, z + 0.07f),
            [Joint.LeftBigToe] = new(0.12f, 0.93f, z - 0.15f), [Joint.RightBigToe] = new(-0.12f, 0.93f, z - 0.15f),
            [Joint.LeftSmallToe] = new(0.16f, 0.93f, z - 0.12f), [Joint.RightSmallToe] = new(-0.16f, 0.93f, z - 0.12f),
        };
        if (raiseLeftLeg)
        {
            // 左膝を前に上げた姿勢（左右で高さが違うので、左右の取り違えが再投影誤差に出る）
            s[Joint.LeftKnee] = new(0.10f, 0.20f, z - 0.35f);
            s[Joint.LeftAnkle] = new(0.10f, 0.55f, z - 0.30f);
            s[Joint.LeftHeel] = new(0.10f, 0.60f, z - 0.25f);
            s[Joint.LeftBigToe] = new(0.12f, 0.60f, z - 0.45f);
            s[Joint.LeftSmallToe] = new(0.16f, 0.60f, z - 0.42f);
        }
        return s;
    }

    internal static KeypointGate.GatedPoint[] View(Dictionary<Joint, Vector3> skeleton, bool camera2)
    {
        var points = new KeypointGate.GatedPoint[Layout.Count];
        for (int i = 0; i < Layout.Count; i++)
        {
            if (!skeleton.TryGetValue(Layout.Joints[i], out var p)) continue;
            var uv = Project(camera2 ? ToCamera2(p) : p);
            points[i] = new KeypointGate.GatedPoint(uv.X, uv.Y, true, false);
        }
        return points;
    }

    [Fact]
    public void 一点の三角測量で元の位置に戻る()
    {
        foreach (var p in new[] { new Vector3(0, 0, 3), new Vector3(0.4f, -0.6f, 2.5f), new Vector3(-0.5f, 0.8f, 3.5f) })
        {
            var r = StereoTriangulator.TriangulatePoint(Project(p), Project(ToCamera2(p)), Stereo);
            Assert.NotNull(r);
            Assert.True(Vector3.Distance(p, r.Value.Camera1Point) < 1e-3, $"{p} → {r.Value.Camera1Point}");
            Assert.True(r.Value.ReprojErrorPx < 0.05);
        }
    }

    [Fact]
    public void 二台で食い違う点は再投影誤差が大きい()
    {
        var p = new Vector3(0, 0, 3);
        // カメラ 2 の点を縦に 40px ずらす（2 台の視線が交わらなくなる）
        var r = StereoTriangulator.TriangulatePoint(Project(p), Project(ToCamera2(p)) + new Vector2(0, 40), Stereo);
        Assert.NotNull(r);
        Assert.True(r.Value.ReprojErrorPx > 15, $"再投影誤差 {r.Value.ReprojErrorPx}");
    }

    [Fact]
    public void 姿勢全体をトラッキング座標の3Dにする()
    {
        var skeleton = Skeleton();
        var toTracking = new CameraToTracking();
        var pose = StereoTriangulator.Triangulate(View(skeleton, false), View(skeleton, true), Layout, Stereo, toTracking, 15);

        Assert.NotNull(pose);
        foreach (var (joint, expected) in skeleton)
        {
            Assert.True(pose!.TryGet(joint, out var actual), $"{joint} が無効");
            Assert.True(Vector3.Distance(toTracking.Transform(expected), actual) < 1e-3, $"{joint}");
        }
        Assert.Equal(3.0, pose!.BodyDepth, 3);
        Assert.Equal(0, pose.BodyYawDeg, 1); // カメラの方を向いている
    }

    [Fact]
    public void 食い違う関節だけ捨てる()
    {
        var skeleton = Skeleton();
        var view2 = View(skeleton, true);
        int knee = Layout.IndexOf(Joint.LeftKnee);
        view2[knee] = view2[knee] with { Y = view2[knee].Y + 60 };

        var pose = StereoTriangulator.Triangulate(View(skeleton, false), view2, Layout, Stereo, new CameraToTracking(), 15);

        Assert.NotNull(pose);
        Assert.False(pose!.TryGet(Joint.LeftKnee, out _));
        Assert.True(pose.TryGet(Joint.RightKnee, out _));
    }

    [Fact]
    public void カメラ2の左右の取り違えを直す()
    {
        var skeleton = Skeleton(raiseLeftLeg: true);
        var view2 = View(skeleton, true);
        // カメラ 2 の推定だけ左右が逆になったとする
        var swapped = new KeypointGate.GatedPoint[view2.Length];
        for (int i = 0; i < Layout.Count; i++) swapped[i] = view2[Layout.IndexOf(MirrorOf(Layout.Joints[i]))];

        var toTracking = new CameraToTracking();
        var pose = StereoTriangulator.Triangulate(View(skeleton, false), swapped, Layout, Stereo, toTracking, 15);

        Assert.NotNull(pose);
        Assert.True(pose!.TryGet(Joint.LeftKnee, out var leftKnee));
        Assert.True(Vector3.Distance(toTracking.Transform(skeleton[Joint.LeftKnee]), leftKnee) < 1e-3);
    }

    private static Joint MirrorOf(Joint j)
    {
        var name = j.ToString();
        if (name.StartsWith("Left")) return Enum.Parse<Joint>("Right" + name[4..]);
        if (name.StartsWith("Right")) return Enum.Parse<Joint>("Left" + name[5..]);
        return j;
    }
}

public class StereoFrameMatcherTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static StereoView View(double seconds) => new(null, Ticks(100 + seconds), 1280, 720);

    [Fact]
    public void 時刻が近ければ組にする()
    {
        var m = new StereoFrameMatcher();
        Assert.False(m.TrySubmit(0, View(0.000), out _, out _));
        Assert.True(m.TrySubmit(1, View(0.010), out var v1, out var v2));
        Assert.Equal(Ticks(100.000), v1.CapturedTimestamp);
        Assert.Equal(Ticks(100.010), v2.CapturedTimestamp);
    }

    [Fact]
    public void 時刻が離れていれば組にせず_新しい方同士で組む()
    {
        var m = new StereoFrameMatcher();
        m.TrySubmit(0, View(0.000), out _, out _);
        Assert.False(m.TrySubmit(1, View(0.050), out _, out _)); // 50ms 離れている
        Assert.True(m.TrySubmit(0, View(0.055), out var v1, out var v2));
        Assert.Equal(Ticks(100.055), v1.CapturedTimestamp);
        Assert.Equal(Ticks(100.050), v2.CapturedTimestamp);
    }

    [Fact]
    public void 組にした結果は二度使わない()
    {
        var m = new StereoFrameMatcher();
        m.TrySubmit(0, View(0.000), out _, out _);
        Assert.True(m.TrySubmit(1, View(0.005), out _, out _));
        Assert.False(m.TrySubmit(1, View(0.010), out _, out _));
    }
}

public class TrackingProcessorStereoTests
{
    private static Pose2D Pose(Dictionary<Joint, Vector3> skeleton, bool camera2)
    {
        var layout = StereoTriangulatorTests.Layout;
        var kps = new Keypoint2D[layout.Count];
        for (int i = 0; i < layout.Count; i++)
        {
            if (!skeleton.TryGetValue(layout.Joints[i], out var p)) continue;
            var uv = StereoTriangulatorTests.Project(camera2 ? StereoTriangulatorTests.ToCamera2(p) : p);
            kps[i] = new Keypoint2D(uv.X, uv.Y, 1f);
        }
        return new Pose2D(layout, kps, new BoundingBox(0, 0, 1280, 720));
    }

    [Fact]
    public void ステレオの組から腰のトラッカーを求める()
    {
        var skeleton = StereoTriangulatorTests.Skeleton();
        var tp = new TrackingProcessor(new TrackingSettings()) { Stereo = StereoTriangulatorTests.Stereo };
        long ts = 1000 * Stopwatch.Frequency;

        var frame = tp.ProcessStereo(
            new StereoView(Pose(skeleton, false), ts, 1280, 720),
            new StereoView(Pose(skeleton, true), ts, 1280, 720),
            StereoTriangulatorTests.Layout);

        Assert.NotNull(frame);
        Assert.Null(tp.StereoProblem);
        var hip = frame!.Trackers.Single(t => t.Role == TrackerRole.Hip);
        Assert.True(hip.Valid);
        var toTracking = new CameraToTracking();
        var expected = (toTracking.Transform(skeleton[Joint.LeftHip]) + toTracking.Transform(skeleton[Joint.RightHip])) * 0.5f;
        Assert.True(Vector3.Distance(expected, hip.Position) < 1e-3, $"{expected} / {hip.Position}");
        Assert.True(frame.Trackers.All(t => t.Valid));
    }

    [Fact]
    public void 解像度が校正時と違えば三角測量しない()
    {
        var skeleton = StereoTriangulatorTests.Skeleton();
        var tp = new TrackingProcessor(new TrackingSettings()) { Stereo = StereoTriangulatorTests.Stereo };
        long ts = 1000 * Stopwatch.Frequency;

        var frame = tp.ProcessStereo(
            new StereoView(Pose(skeleton, false), ts, 640, 480),
            new StereoView(Pose(skeleton, true), ts, 1280, 720),
            StereoTriangulatorTests.Layout);

        Assert.Null(frame);
        Assert.NotNull(tp.StereoProblem);
    }
}
