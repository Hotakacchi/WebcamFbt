using System.Diagnostics;
using System.Numerics;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 2D の推定結果からトラッカーを求めるまでの処理をまとめたもの。
///   単眼（<see cref="Process"/>）:       2D キーポイント → 信頼度ゲート → 単眼の 3D 化 → トラッカー計算・平滑化
///   ステレオ（<see cref="ProcessStereo"/>）: 2 台の 2D キーポイント → 信頼度ゲート → 三角測量 → トラッカー計算・平滑化
/// 推論スレッドから呼ぶ（スレッドセーフではない。2 台のときは呼び出し側で直列化する）。
/// </summary>
public sealed class TrackingProcessor
{
    private readonly KeypointGate _gate = new();
    private readonly KeypointGate _gate2 = new();
    private readonly MonocularLifter _lifter = new();
    private readonly TrackerSolver _solver;
    private readonly CameraToTracking _toTracking = new();
    private volatile bool _resetRequested;

    public TrackingSettings Settings { get; }
    public BodyProportions Body { get; set; } = BodyProportions.Default;

    /// <summary>適用中のキャリブレーション結果（未実施なら <see cref="CalibrationData.None"/>）。</summary>
    public CalibrationData Calibration { get; private set; } = CalibrationData.None;

    /// <summary>
    /// チェッカーボードで校正したカメラの内部パラメータ（無ければ null）。
    /// 解像度が合うときだけ、画角による近似の代わりに使い、キーポイントの歪みも補正する。
    /// UI スレッドから差し替えるので volatile。
    /// </summary>
    public CameraCalibration? Intrinsics
    {
        get => _intrinsics;
        set => _intrinsics = value;
    }
    private volatile CameraCalibration? _intrinsics;

    /// <summary>ステレオの校正結果（無ければ null）。<see cref="ProcessStereo"/> で使う。UI スレッドから差し替える。</summary>
    public StereoCalibration? Stereo
    {
        get => _stereo;
        set => _stereo = value;
    }
    private volatile StereoCalibration? _stereo;

    /// <summary>直近の <see cref="ProcessStereo"/> で三角測量できなかった理由（表示用。問題なければ null）。</summary>
    public string? StereoProblem
    {
        get => _stereoProblem;
        private set => _stereoProblem = value;
    }
    private volatile string? _stereoProblem;

    /// <summary>
    /// 設定されている間は、1 フレームごとに入力の 2D と計算結果を渡す（診断用の記録）。
    /// 推論スレッドから呼ばれる。UI スレッドから差し替えるので volatile。
    /// </summary>
    public Action<TrackingSample>? Sampler
    {
        get => _sampler;
        set => _sampler = value;
    }
    private volatile Action<TrackingSample>? _sampler;

    /// <summary>直近の <see cref="ProcessStereo"/> の品質の目安（精度確認の表示用。三角測量しなかったときは null）。</summary>
    public StereoStats? LastStereoStats
    {
        get => _lastStereoStats;
        private set => _lastStereoStats = value;
    }
    private volatile StereoStats? _lastStereoStats;

    /// <summary>
    /// 単眼のときの、写り方についての注意（足が画面の下端に近い・体が端に寄っている など）。問題なければ空。
    /// 一瞬だけの状態で表示がちらつかないよう、しばらく続いたものだけを出す。
    /// </summary>
    public IReadOnlyList<string> SetupWarnings => _setupWarnings;
    private volatile string[] _setupWarnings = [];
    private readonly SetupMonitor _setupMonitor = new();

    // キャリブレーション中の状態。UI スレッドと推論スレッドから触るので volatile。
    private volatile Calibrator? _calibrator;
    private volatile int _calibrationTarget;
    private volatile bool _calibrationActive;
    private volatile bool _calibrationSucceeded;
    private volatile bool _calibrationFailed;

    /// <summary>キャリブレーション中か。</summary>
    public bool IsCalibrating => _calibrationActive;

    /// <summary>これまでに取り込んだフレーム数。</summary>
    public int CalibrationFrameCount => _calibrator?.FrameCount ?? 0;

    /// <summary>確定までに必要なフレーム数。</summary>
    public int CalibrationTarget => _calibrationTarget;

    public TrackingProcessor(TrackingSettings settings)
    {
        Settings = settings;
        _solver = new TrackerSolver(settings);
    }

    /// <summary>
    /// キャリブレーションを開始する（UI スレッドから）。targetFrames フレーム貯めたら自動で確定する。
    /// </summary>
    public void BeginCalibration(int targetFrames)
    {
        _calibrationTarget = System.Math.Max(1, targetFrames);
        _calibrationSucceeded = false;
        _calibrationFailed = false;
        _calibrator = new Calibrator();
        _calibrationActive = true;
    }

    /// <summary>キャリブレーションを中断する。</summary>
    public void CancelCalibration()
    {
        _calibrator = null;
        _calibrationActive = false;
    }

    /// <summary>保存しておいたキャリブレーション結果を適用する（開始前に UI スレッドから）。</summary>
    public void RestoreCalibration(CalibrationData data)
    {
        Calibration = data;
        Body = data.Body;
    }

    /// <summary>キャリブレーション結果を破棄し、既定の比率に戻す。</summary>
    public void ClearCalibration()
    {
        CancelCalibration();
        Calibration = CalibrationData.None;
        Body = BodyProportions.Default;
    }

    /// <summary>直近のキャリブレーション完了を一度だけ受け取る（UI スレッドから。成功/失敗を返す）。</summary>
    public bool TryTakeCalibrationResult(out bool success)
    {
        if (_calibrationSucceeded) { _calibrationSucceeded = false; success = true; return true; }
        if (_calibrationFailed) { _calibrationFailed = false; success = false; return true; }
        success = false;
        return false;
    }

    /// <summary>
    /// 1 フレーム分を処理する。人が見つからない・関節が足りない場合は null。
    /// </summary>
    /// <param name="pose">2D の推定結果（人がいなければ null）。</param>
    /// <param name="layout">キーポイントの並び。</param>
    /// <param name="capturedTimestamp">フレームの取得時刻（Stopwatch のタイムスタンプ）。</param>
    public TrackerFrame? Process(Pose2D? pose, KeypointLayout layout, long capturedTimestamp, int imageWidth, int imageHeight)
    {
        ApplyPendingReset();
        var input = pose; // 診断用の記録には、歪み補正の前の推定を残す
        double time = capturedTimestamp / (double)Stopwatch.Frequency;
        var s = Settings;

        var intrinsics = _intrinsics;
        bool calibrated = intrinsics is not null && intrinsics.Matches(imageWidth, imageHeight);
        if (calibrated && pose is not null) pose = Undistort(pose, intrinsics!);

        var points = _gate.Apply(pose, layout.Count, time, s.KeypointScoreThreshold, s.KeypointHoldSeconds);

        var camera = calibrated
            ? intrinsics!.ToIntrinsics()
            : CameraIntrinsics.FromHorizontalFov(imageWidth, imageHeight, s.HorizontalFovDeg);

        // 体のキャリブレーションでカメラの置き方が分かっていれば、その傾きで床の面を使う（傾きの設定は使わない）
        var cal = Calibration;
        _toTracking.PitchDownDeg = cal.Camera?.PitchDownDeg ?? s.CameraPitchDownDeg;
        _setupWarnings = _setupMonitor.Update(points, layout, imageWidth, imageHeight);

        var pose3D = _lifter.Lift(points, layout, camera, _toTracking, s, Body, time,
            floor: cal.Camera, shoulderRatioRef: cal.ShoulderRatioRef ?? 1.0);
        var frame = pose3D is null ? null : Finish(pose3D, time, capturedTimestamp, stereo: false, mono: (points, layout, camera));
        _sampler?.Invoke(new TrackingSample(false, capturedTimestamp, imageWidth, imageHeight, input, 0, 0, 0, null, pose3D, frame));
        return frame;
    }

    /// <summary>
    /// 2 台のカメラの組を処理する（三角測量）。校正が無い・解像度が合わない・使える関節がない場合は null。
    /// </summary>
    /// <param name="view1">カメラ 1 の推定結果。</param>
    /// <param name="view2">カメラ 2 の推定結果（撮影時刻が近いもの）。</param>
    public TrackerFrame? ProcessStereo(StereoView view1, StereoView view2, KeypointLayout layout)
    {
        var frame = ProcessStereoCore(view1, view2, layout, out var pose3D);
        _sampler?.Invoke(new TrackingSample(true,
            view1.CapturedTimestamp, view1.ImageWidth, view1.ImageHeight, view1.Pose,
            view2.CapturedTimestamp, view2.ImageWidth, view2.ImageHeight, view2.Pose,
            pose3D, frame));
        return frame;
    }

    private TrackerFrame? ProcessStereoCore(StereoView view1, StereoView view2, KeypointLayout layout, out Pose3D? pose3D)
    {
        pose3D = null;
        ApplyPendingReset();
        var s = Settings;
        var stereo = _stereo;
        if (stereo is null)
        {
            StereoProblem = "ステレオ校正がありません";
            LastStereoStats = null;
            return null;
        }
        if (!stereo.Camera1.Matches(view1.ImageWidth, view1.ImageHeight) || !stereo.Camera2.Matches(view2.ImageWidth, view2.ImageHeight))
        {
            StereoProblem = "解像度が校正したときと違います";
            LastStereoStats = null;
            return null;
        }

        // 2 台のうち後に撮った方の時刻を、この組の時刻とする
        long timestamp = System.Math.Max(view1.CapturedTimestamp, view2.CapturedTimestamp);
        double time = timestamp / (double)Stopwatch.Frequency;
        var points1 = _gate.Apply(view1.Pose, layout.Count, time, s.KeypointScoreThreshold, s.KeypointHoldSeconds);
        var points2 = _gate2.Apply(view2.Pose, layout.Count, time, s.KeypointScoreThreshold, s.KeypointHoldSeconds);
        _toTracking.PitchDownDeg = s.CameraPitchDownDeg;

        pose3D = StereoTriangulator.Triangulate(points1, points2, layout, stereo, _toTracking, s.StereoMaxReprojErrorPx, out var stats);
        LastStereoStats = stats;
        if (pose3D is null)
        {
            StereoProblem = "2 台に共通して写っている関節がありません";
            return null;
        }
        StereoProblem = null;
        return Finish(pose3D, time, timestamp, stereo: true);
    }

    /// <summary>
    /// 3D の姿勢からトラッカーを求める（単眼・ステレオ共通の後段）。
    /// 体のキャリブレーション中ならこの姿勢を取り込み、床の高さ・向き・傾きの補正をかけて返す。
    /// </summary>
    private TrackerFrame Finish(
        Pose3D pose3D, double time, long capturedTimestamp, bool stereo,
        (KeypointGate.GatedPoint[] Points, KeypointLayout Layout, CameraIntrinsics Camera)? mono = null)
    {
        var s = Settings;

        // キャリブレーション中なら、この姿勢を取り込み、目標フレーム数に達したら確定する
        var cal = _calibrator;
        if (cal is not null)
        {
            cal.Add(pose3D, stereo, mono);
            if (cal.FrameCount >= _calibrationTarget)
            {
                var result = BuildCalibration(cal, s);
                _calibrator = null;
                _calibrationActive = false;
                if (result is not null)
                {
                    Calibration = result;
                    Body = result.Body;
                    _calibrationSucceeded = true;
                    // 座標の取り方が変わる（床の面を使う）ので、次のフレームで平滑化を捨てて前の位置を引きずらないようにする
                    _resetRequested = true;
                }
                else
                {
                    _calibrationFailed = true;
                }
            }
        }

        var frame = _solver.Solve(pose3D, time, capturedTimestamp);
        return Calibration.Apply(frame);
    }

    /// <summary>
    /// 貯めたフレームからキャリブレーション結果を作る。全フレームが単眼なら、立ち姿からカメラの置き方を求めて
    /// 床の面を使って測り直す（<see cref="MonoCalibration"/>）。求まらなければ従来どおり 3D 姿勢から測る。
    /// </summary>
    private static CalibrationData? BuildCalibration(Calibrator cal, TrackingSettings s)
    {
        bool allMono = cal.Layout2D is not null && cal.Points2D.Count == cal.FrameCount;
        if (!allMono) return cal.Build(s.HeightMeters);

        var mono = MonoCalibration.Build(cal.Points2D, cal.Layout2D!, cal.Camera2D, s);
        if (mono is not null) return mono;

        var fallback = cal.Build(s.HeightMeters);
        if (fallback is null) return null;
        var warnings = new List<string>(fallback.Warnings ?? Array.Empty<string>())
        {
            "カメラの高さと傾きを求められませんでした（頭から足首まで全身が写るように、まっすぐ立ってやり直してください）",
        };
        return fallback with { Warnings = warnings };
    }

    /// <summary>キーポイントのレンズ歪みを補正した姿勢を作る。</summary>
    private static Pose2D Undistort(Pose2D pose, CameraCalibration calibration)
    {
        var src = pose.Keypoints;
        var dst = new Keypoint2D[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var p = calibration.Undistort(new Vector2(src[i].X, src[i].Y));
            dst[i] = src[i] with { X = p.X, Y = p.Y };
        }
        return new Pose2D(pose.Layout, dst, pose.Box);
    }

    /// <summary>平滑化などの内部状態を捨てる（開始し直すとき）。キャリブレーション結果は保持する。</summary>
    public void Reset()
    {
        ResetFilters();
        CancelCalibration();
    }

    /// <summary>
    /// 次のフレームの処理の前に、平滑化などの内部状態を捨てるよう頼む（UI スレッドから）。
    /// 単眼とステレオを切り替えたとき、推定方法の違いで位置が飛ぶのをフィルタが引きずらないようにする。
    /// </summary>
    public void RequestReset() => _resetRequested = true;

    private void ApplyPendingReset()
    {
        if (!_resetRequested) return;
        _resetRequested = false;
        ResetFilters();
    }

    private void ResetFilters()
    {
        _gate.Reset();
        _gate2.Reset();
        _lifter.Reset();
        _solver.Reset();
        _setupMonitor.Reset();
        _setupWarnings = [];
    }

    /// <summary>
    /// 写り方の注意を、フレームごとの判定をならして決める（推論スレッドだけで使う）。
    /// 足首が画面の下端に近い・頭が上端に近い（全身が入っていない）、体が左右の端に寄っている（レンズの歪みが大きい）を見る。
    /// </summary>
    private sealed class SetupMonitor
    {
        private const double Smoothing = 0.05; // 1 フレームの重み（約 20 フレームでならす）
        private const double On = 0.6, Off = 0.3; // 出す・消すしきい値（行ったり来たりしないよう差をつける）

        private static readonly string[] Messages =
        [
            "足が画面の下端に近いです。カメラを少し離すか、上に向けて足元に余白を作ってください",
            "頭が画面の上端に近いです。カメラを少し離すか、下に向けて頭の上に余白を作ってください",
            "体が画面の端に寄っています。画面の中央に立つと、歪みが少なく安定します",
        ];

        private readonly double[] _level = new double[Messages.Length];
        private readonly bool[] _shown = new bool[Messages.Length];

        public string[] Update(KeypointGate.GatedPoint[] points, KeypointLayout layout, int width, int height)
        {
            float? MaxY(params Joint[] joints)
            {
                float? v = null;
                foreach (var j in joints)
                {
                    int i = layout.IndexOf(j);
                    if (i >= 0 && points[i].Valid && !points[i].Held) v = v is null ? points[i].Y : System.Math.Max(v.Value, points[i].Y);
                }
                return v;
            }
            float? MinY(params Joint[] joints)
            {
                float? v = null;
                foreach (var j in joints)
                {
                    int i = layout.IndexOf(j);
                    if (i >= 0 && points[i].Valid && !points[i].Held) v = v is null ? points[i].Y : System.Math.Min(v.Value, points[i].Y);
                }
                return v;
            }
            float? CenterX()
            {
                int l = layout.IndexOf(Joint.LeftHip), r = layout.IndexOf(Joint.RightHip);
                if (l >= 0 && r >= 0 && points[l].Valid && points[r].Valid) return (points[l].X + points[r].X) * 0.5f;
                return null;
            }

            // 人が見えていない・その部分が見えていないときは、判定を変えない
            var feet = MaxY(Joint.LeftAnkle, Joint.RightAnkle, Joint.LeftHeel, Joint.RightHeel);
            var head = MinY(Joint.HeadTop, Joint.LeftEar, Joint.RightEar, Joint.Nose);
            var cx = CenterX();
            bool?[] now =
            [
                feet is { } f ? f > 0.96 * height : null,
                head is { } hd ? hd < 0.03 * height : null,
                cx is { } x ? x < 0.2 * width || x > 0.8 * width : null,
            ];

            var result = new List<string>();
            for (int i = 0; i < now.Length; i++)
            {
                if (now[i] is { } bad) _level[i] += Smoothing * ((bad ? 1 : 0) - _level[i]);
                if (_level[i] > On) _shown[i] = true;
                else if (_level[i] < Off) _shown[i] = false;
                if (_shown[i]) result.Add(Messages[i]);
            }
            return [.. result];
        }

        public void Reset()
        {
            Array.Clear(_level);
            Array.Clear(_shown);
        }
    }
}
