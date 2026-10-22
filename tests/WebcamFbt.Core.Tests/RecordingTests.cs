using System.Diagnostics;
using System.Numerics;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Tests;

/// <summary>診断用の記録: 記録したものを読み込んで計算し直すと、記録したときと同じ結果になるか。</summary>
public class RecordingTests
{
    private static readonly KeypointLayout Layout = KeypointLayout.Halpe26;

    /// <summary>右足を少しずつ前に上げていく骨格（カメラ 1 座標）。</summary>
    private static Dictionary<Joint, Vector3> Frame(int i)
    {
        var s = StereoTriangulatorTests.Skeleton();
        float lift = Math.Min(i, 20) * 0.015f; // 最大 30cm
        foreach (var j in new[] { Joint.RightKnee, Joint.RightAnkle, Joint.RightHeel, Joint.RightBigToe, Joint.RightSmallToe })
        {
            var p = s[j];
            s[j] = new Vector3(p.X, p.Y - lift, p.Z - lift * 0.8f);
        }
        return s;
    }

    private static Pose2D Pose(Dictionary<Joint, Vector3> skeleton, bool camera2)
    {
        var kps = new Keypoint2D[Layout.Count];
        for (int i = 0; i < Layout.Count; i++)
        {
            if (!skeleton.TryGetValue(Layout.Joints[i], out var p)) continue;
            var uv = StereoTriangulatorTests.Project(camera2 ? StereoTriangulatorTests.ToCamera2(p) : p);
            kps[i] = new Keypoint2D(uv.X, uv.Y, 0.9f);
        }
        return new Pose2D(Layout, kps, new BoundingBox(100, 50, 900, 700, 0.95f));
    }

    private static TrackingProcessor NewProcessor(bool stereo)
    {
        var settings = new TrackingSettings { HeightMeters = 1.78, HorizontalFovDeg = 65, CameraPitchDownDeg = 4 };
        settings.PositionFilter.MinCutoff = 0.8;
        settings.DepthFilter.Beta = 0.1;
        var p = new TrackingProcessor(settings);
        p.RestoreCalibration(new CalibrationData(new BodyProportions(0.23, 0.12, 0.29, 0.26, 0.24, 0.14, 0.1), -1.1f, 3.0));
        if (stereo) p.Stereo = StereoTriangulatorTests.Stereo;
        return p;
    }

    private static string Record(bool stereo, out List<TrackerFrame?> recorded)
    {
        var path = Path.Combine(Path.GetTempPath(), $"webcamfbt-record-{Guid.NewGuid():N}.jsonl");
        var processor = NewProcessor(stereo);
        recorded = new List<TrackerFrame?>();
        using (var recorder = new TrackingRecorder(path, RecordingHeader.Create(processor, Layout, "0.1.2+test", "右足を上げる")))
        {
            processor.Sampler = recorder.Write;
            long t0 = 1000 * Stopwatch.Frequency;
            for (int i = 0; i < 40; i++)
            {
                long ts = t0 + i * Stopwatch.Frequency / 30;
                var skeleton = Frame(i);
                // 途中で 1 フレームだけ人を見失う（保持の動きも再現されるか）
                var pose1 = i == 25 ? null : Pose(skeleton, false);
                recorded.Add(stereo
                    ? processor.ProcessStereo(new StereoView(pose1, ts, 1280, 720), new StereoView(Pose(skeleton, true), ts + 50_000, 1280, 720), Layout)
                    : processor.Process(pose1, Layout, ts, 1280, 720));
            }
            Assert.Equal(40, recorder.FrameCount);
        }
        return path;
    }

    private static void AssertSame(List<TrackerFrame?> expected, List<TrackerFrame?> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i] is null, actual[i] is null);
            if (expected[i] is not { } e || actual[i] is not { } a) continue;
            for (int k = 0; k < e.Trackers.Count; k++)
            {
                Assert.Equal(e.Trackers[k].Valid, a.Trackers[k].Valid);
                Assert.True(Vector3.Distance(e.Trackers[k].Position, a.Trackers[k].Position) < 1e-5, $"フレーム {i} の {e.Trackers[k].Role}");
                Assert.True(Quaternion.Dot(e.Trackers[k].Rotation, a.Trackers[k].Rotation) > 1 - 1e-6, $"フレーム {i} の {e.Trackers[k].Role} の回転");
            }
            Assert.True(Vector3.Distance(e.HeadPosition, a.HeadPosition) < 1e-5);
        }
    }

    [Fact]
    public void 単眼の記録から同じ計算を再現できる()
    {
        var path = Record(stereo: false, out var recorded);
        try
        {
            var recording = TrackingRecording.Load(path);
            Assert.Equal(40, recording.Frames.Count);
            Assert.Equal("右足を上げる", recording.Header.Note);
            Assert.Equal(1.78, recording.Header.Settings.HeightMeters);
            Assert.Equal(0.8, recording.Header.Settings.PositionFilter[0]);
            Assert.NotNull(recording.Header.BodyCalibration);
            Assert.Null(recording.Frames[25].Kp);              // 見失ったフレーム
            Assert.NotNull(recording.Frames[0].Pose3D);         // 3D の関節も入っている

            AssertSame(recorded, recording.Replay());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ステレオの記録から同じ計算を再現できる()
    {
        var path = Record(stereo: true, out var recorded);
        try
        {
            var recording = TrackingRecording.Load(path);
            Assert.True(recording.Frames.All(f => f.Stereo));
            Assert.NotNull(recording.Header.Stereo);
            AssertSame(recorded, recording.Replay());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 記録した値とトラッカーの計算結果が一致する()
    {
        var path = Record(stereo: false, out var recorded);
        try
        {
            var frame = TrackingRecording.Load(path).Frames[10];
            var hip = frame.Trackers!.Single(t => t.Role == nameof(TrackerRole.Hip));
            var expected = recorded[10]!.Trackers.Single(t => t.Role == TrackerRole.Hip).Position;
            Assert.Equal(expected.X, hip.P[0], 1e-6f);
            Assert.Equal(expected.Y, hip.P[1], 1e-6f);
            Assert.Equal(expected.Z, hip.P[2], 1e-6f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
