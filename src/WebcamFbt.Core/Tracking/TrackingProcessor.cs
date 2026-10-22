using System.Diagnostics;
using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 2D の推定結果からトラッカーを求めるまでの処理をまとめたもの（カメラ 1 台用）。
///   2D キーポイント → 信頼度ゲート → 3D 化 → トラッカー計算・平滑化
/// 推論スレッドから呼ぶ（スレッドセーフではない）。
/// </summary>
public sealed class TrackingProcessor
{
    private readonly KeypointGate _gate = new();
    private readonly MonocularLifter _lifter = new();
    private readonly TrackerSolver _solver;
    private readonly CameraToTracking _toTracking = new();

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
        double time = capturedTimestamp / (double)Stopwatch.Frequency;
        var s = Settings;

        var intrinsics = _intrinsics;
        bool calibrated = intrinsics is not null && intrinsics.Matches(imageWidth, imageHeight);
        if (calibrated && pose is not null) pose = Undistort(pose, intrinsics!);

        var points = _gate.Apply(pose, layout.Count, time, s.KeypointScoreThreshold, s.KeypointHoldSeconds);

        var camera = calibrated
            ? intrinsics!.ToIntrinsics()
            : CameraIntrinsics.FromHorizontalFov(imageWidth, imageHeight, s.HorizontalFovDeg);
        _toTracking.PitchDownDeg = s.CameraPitchDownDeg;

        var pose3D = _lifter.Lift(points, layout, camera, _toTracking, s, Body, time);
        if (pose3D is null) return null;

        // キャリブレーション中なら、この姿勢を取り込み、目標フレーム数に達したら確定する
        var cal = _calibrator;
        if (cal is not null)
        {
            cal.Add(pose3D);
            if (cal.FrameCount >= _calibrationTarget)
            {
                var result = cal.Build(s.HeightMeters);
                _calibrator = null;
                _calibrationActive = false;
                if (result is not null)
                {
                    Calibration = result;
                    Body = result.Body;
                    _calibrationSucceeded = true;
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
        _gate.Reset();
        _lifter.Reset();
        _solver.Reset();
        CancelCalibration();
    }
}
