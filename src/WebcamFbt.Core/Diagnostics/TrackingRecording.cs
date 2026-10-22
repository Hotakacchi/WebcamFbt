using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Persistence;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Diagnostics;

/// <summary>
/// 1 フレーム分の、トラッキングの入力（2D の関節）と計算結果。<see cref="TrackingProcessor.Sampler"/> に渡される。
/// </summary>
/// <param name="Pose">カメラ 1 の 2D の推定（歪み補正の前。人がいなければ null）。</param>
/// <param name="Pose2">ステレオのときのカメラ 2 の 2D の推定。</param>
/// <param name="Pose3D">3D 化した姿勢（できなければ null）。</param>
/// <param name="Frame">補正後のトラッカー（できなければ null）。</param>
public sealed record TrackingSample(
    bool Stereo,
    long CapturedTimestamp, int ImageWidth, int ImageHeight, Pose2D? Pose,
    long CapturedTimestamp2, int ImageWidth2, int ImageHeight2, Pose2D? Pose2,
    Pose3D? Pose3D,
    TrackerFrame? Frame);

/// <summary>
/// 診断用の記録の 1 行目（その時の設定とキャリブレーション）。これがあれば、記録した 2D から同じ計算を再現できる。
/// </summary>
public sealed class RecordingHeader
{
    public string Type { get; set; } = "header";
    public int Format { get; set; } = 1;
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>何をしたか（利用者のメモ）。</summary>
    public string Note { get; set; } = "";

    /// <summary>記録した PC の Stopwatch.Frequency（時刻の換算用）。</summary>
    public long TimestampFrequency { get; set; }

    public string Layout { get; set; } = "";
    public string[] Joints { get; set; } = [];
    public RecordedSettings Settings { get; set; } = new();
    public BodyCalibrationSettings? BodyCalibration { get; set; }
    public CameraCalibration? Intrinsics { get; set; }
    public StereoCalibration? Stereo { get; set; }

    public static RecordingHeader Create(TrackingProcessor processor, KeypointLayout layout, string appVersion, string note) => new()
    {
        AppVersion = appVersion,
        CreatedUtc = DateTime.UtcNow,
        Note = note,
        TimestampFrequency = Stopwatch.Frequency,
        Layout = layout.Name,
        Joints = layout.Joints.Select(j => j.ToString()).ToArray(),
        Settings = RecordedSettings.From(processor.Settings),
        BodyCalibration = processor.Calibration.IsCalibrated ? BodyCalibrationSettings.From(processor.Calibration) : null,
        Intrinsics = processor.Intrinsics,
        Stereo = processor.Stereo,
    };
}

/// <summary>記録したときの <see cref="TrackingSettings"/>。</summary>
public sealed class RecordedSettings
{
    public double HeightMeters { get; set; }
    public double HorizontalFovDeg { get; set; }
    public double CameraPitchDownDeg { get; set; }
    public float KeypointScoreThreshold { get; set; }
    public double KeypointHoldSeconds { get; set; }
    public double StereoMaxReprojErrorPx { get; set; }

    /// <summary>各フィルタの (min cutoff, beta, d cutoff)。</summary>
    public double[] PositionFilter { get; set; } = [];
    public double[] RotationFilter { get; set; } = [];
    public double[] DepthFilter { get; set; } = [];

    public static RecordedSettings From(TrackingSettings s) => new()
    {
        HeightMeters = s.HeightMeters,
        HorizontalFovDeg = s.HorizontalFovDeg,
        CameraPitchDownDeg = s.CameraPitchDownDeg,
        KeypointScoreThreshold = s.KeypointScoreThreshold,
        KeypointHoldSeconds = s.KeypointHoldSeconds,
        StereoMaxReprojErrorPx = s.StereoMaxReprojErrorPx,
        PositionFilter = [s.PositionFilter.MinCutoff, s.PositionFilter.Beta, s.PositionFilter.DCutoff],
        RotationFilter = [s.RotationFilter.MinCutoff, s.RotationFilter.Beta, s.RotationFilter.DCutoff],
        DepthFilter = [s.DepthFilter.MinCutoff, s.DepthFilter.Beta, s.DepthFilter.DCutoff],
    };

    public void ApplyTo(TrackingSettings s)
    {
        s.HeightMeters = HeightMeters;
        s.HorizontalFovDeg = HorizontalFovDeg;
        s.CameraPitchDownDeg = CameraPitchDownDeg;
        s.KeypointScoreThreshold = KeypointScoreThreshold;
        s.KeypointHoldSeconds = KeypointHoldSeconds;
        s.StereoMaxReprojErrorPx = StereoMaxReprojErrorPx;
        Set(s.PositionFilter, PositionFilter);
        Set(s.RotationFilter, RotationFilter);
        Set(s.DepthFilter, DepthFilter);

        static void Set(OneEuroParams p, double[] v)
        {
            if (v.Length < 3) return;
            p.MinCutoff = v[0];
            p.Beta = v[1];
            p.DCutoff = v[2];
        }
    }
}

/// <summary>診断用の記録の 2 行目以降（1 フレーム 1 行）。</summary>
public sealed class RecordingFrame
{
    public bool Stereo { get; set; }

    /// <summary>カメラ 1 の撮影時刻（Stopwatch のタイムスタンプのまま）。</summary>
    public long Ticks { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>カメラ 1 の 2D の関節（[x, y, 信頼度] の並び。人がいなければ null）。</summary>
    public float[][]? Kp { get; set; }

    /// <summary>人物の枠 [x1, y1, x2, y2, score]。</summary>
    public float[]? Box { get; set; }

    public long Ticks2 { get; set; }
    public int W2 { get; set; }
    public int H2 { get; set; }
    public float[][]? Kp2 { get; set; }
    public float[]? Box2 { get; set; }

    /// <summary>3D の関節（トラッキング座標 [m]）。</summary>
    public Dictionary<string, float[]>? Pose3D { get; set; }

    public double Depth { get; set; }
    public double Yaw { get; set; }
    public RecordedTracker[]? Trackers { get; set; }
    public float[]? Head { get; set; }
    public bool HeadValid { get; set; }

    public static RecordingFrame From(TrackingSample s)
    {
        var f = new RecordingFrame
        {
            Stereo = s.Stereo,
            Ticks = s.CapturedTimestamp,
            W = s.ImageWidth,
            H = s.ImageHeight,
            Kp = Keypoints(s.Pose),
            Box = BoxOf(s.Pose),
        };
        if (s.Stereo)
        {
            f.Ticks2 = s.CapturedTimestamp2;
            f.W2 = s.ImageWidth2;
            f.H2 = s.ImageHeight2;
            f.Kp2 = Keypoints(s.Pose2);
            f.Box2 = BoxOf(s.Pose2);
        }
        if (s.Pose3D is { } pose)
        {
            f.Depth = pose.BodyDepth;
            f.Yaw = pose.BodyYawDeg;
            f.Pose3D = new Dictionary<string, float[]>();
            foreach (var j in Enum.GetValues<Joint>())
            {
                if (pose.TryGet(j, out var p)) f.Pose3D[j.ToString()] = [p.X, p.Y, p.Z];
            }
        }
        if (s.Frame is { } frame)
        {
            f.Trackers = frame.Trackers.Select(RecordedTracker.From).ToArray();
            f.Head = [frame.HeadPosition.X, frame.HeadPosition.Y, frame.HeadPosition.Z];
            f.HeadValid = frame.HeadValid;
        }
        return f;
    }

    private static float[][]? Keypoints(Pose2D? pose)
        => pose?.Keypoints.Select(k => new[] { k.X, k.Y, k.Score }).ToArray();

    private static float[]? BoxOf(Pose2D? pose)
        => pose is null ? null : [pose.Box.X1, pose.Box.Y1, pose.Box.X2, pose.Box.Y2, pose.Box.Score];

    /// <summary>記録した 2D の関節を推定結果の形に戻す。</summary>
    public static Pose2D? ToPose(float[][]? kp, float[]? box, KeypointLayout layout)
    {
        if (kp is null) return null;
        var keypoints = kp.Select(k => new Keypoint2D(k[0], k[1], k[2])).ToArray();
        var b = box is { Length: >= 5 } ? new BoundingBox(box[0], box[1], box[2], box[3], box[4]) : new BoundingBox(0, 0, 1, 1);
        return new Pose2D(layout, keypoints, b);
    }
}

/// <summary>トラッカー 1 つの記録。</summary>
public sealed class RecordedTracker
{
    public string Role { get; set; } = "";
    public int Index { get; set; }
    public bool Valid { get; set; }
    public float[] P { get; set; } = [];

    /// <summary>回転のクォータニオン (x, y, z, w)。</summary>
    public float[] Q { get; set; } = [];

    /// <summary>VRChat に送るオイラー角 [度]。</summary>
    public float[] E { get; set; } = [];

    public static RecordedTracker From(TrackerPose t) => new()
    {
        Role = t.Role.ToString(),
        Index = t.OscIndex,
        Valid = t.Valid,
        P = [t.Position.X, t.Position.Y, t.Position.Z],
        Q = [t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W],
        E = [t.EulerDeg.X, t.EulerDeg.Y, t.EulerDeg.Z],
    };
}

/// <summary>
/// 診断用の記録をファイル（JSON Lines: 1 行目がヘッダ、2 行目以降が 1 フレームずつ）に書き出す。
/// 映像は記録しない。推論スレッドから <see cref="Write"/>、UI スレッドから <see cref="Dispose"/> するのでロックで守る。
/// </summary>
public sealed class TrackingRecorder : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private StreamWriter? _writer;

    public string FilePath { get; }

    public int FrameCount { get; private set; }

    public TrackingRecorder(string filePath, RecordingHeader header)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
        _writer.WriteLine(JsonSerializer.Serialize(header, JsonOptions));
    }

    public void Write(TrackingSample sample)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            _writer.WriteLine(JsonSerializer.Serialize(RecordingFrame.From(sample), JsonOptions));
            FrameCount++;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

/// <summary>
/// 診断用の記録を読み込み、同じ計算を再現する（原因の調査とテスト用）。
/// </summary>
public sealed class TrackingRecording
{
    public required RecordingHeader Header { get; init; }
    public required List<RecordingFrame> Frames { get; init; }

    public static TrackingRecording Load(string filePath)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        var header = JsonSerializer.Deserialize<RecordingHeader>(reader.ReadLine() ?? "", TrackingRecorder.JsonOptions)
            ?? throw new InvalidDataException("ヘッダがありません。");
        var frames = new List<RecordingFrame>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            frames.Add(JsonSerializer.Deserialize<RecordingFrame>(line, TrackingRecorder.JsonOptions)
                ?? throw new InvalidDataException("フレームを読めません。"));
        }
        return new TrackingRecording { Header = header, Frames = frames };
    }

    /// <summary>
    /// 記録したときの設定・キャリブレーション・カメラの校正で <see cref="TrackingProcessor"/> を作り直し、
    /// 記録した 2D を同じ時刻で流し込んで計算し直す。<paramref name="configure"/> で設定を変えて比べることもできる。
    /// </summary>
    public List<TrackerFrame?> Replay(Action<TrackingProcessor>? configure = null)
    {
        var layout = Header.Layout == KeypointLayout.Halpe26.Name
            ? KeypointLayout.Halpe26
            : throw new NotSupportedException($"関節の並び {Header.Layout} には対応していません。");

        var settings = new TrackingSettings();
        Header.Settings.ApplyTo(settings);
        var processor = new TrackingProcessor(settings);
        if (Header.BodyCalibration?.ToCalibrationData() is { } calibration) processor.RestoreCalibration(calibration);
        processor.Intrinsics = Header.Intrinsics;
        processor.Stereo = Header.Stereo;
        configure?.Invoke(processor);

        // 記録した PC と時計の刻みが違えば換算する（フィルタは時刻の差で計算するため）
        double scale = Header.TimestampFrequency > 0 ? (double)Stopwatch.Frequency / Header.TimestampFrequency : 1.0;
        long Ticks(long t) => (long)(t * scale);

        var results = new List<TrackerFrame?>(Frames.Count);
        foreach (var f in Frames)
        {
            var pose = RecordingFrame.ToPose(f.Kp, f.Box, layout);
            results.Add(f.Stereo
                ? processor.ProcessStereo(
                    new StereoView(pose, Ticks(f.Ticks), f.W, f.H),
                    new StereoView(RecordingFrame.ToPose(f.Kp2, f.Box2, layout), Ticks(f.Ticks2), f.W2, f.H2),
                    layout)
                : processor.Process(pose, layout, Ticks(f.Ticks), f.W, f.H));
        }
        return results;
    }
}
