using System.Diagnostics;
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

    public TrackingProcessor(TrackingSettings settings)
    {
        Settings = settings;
        _solver = new TrackerSolver(settings);
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

        var points = _gate.Apply(pose, layout.Count, time, s.KeypointScoreThreshold, s.KeypointHoldSeconds);

        var camera = CameraIntrinsics.FromHorizontalFov(imageWidth, imageHeight, s.HorizontalFovDeg);
        _toTracking.PitchDownDeg = s.CameraPitchDownDeg;

        var pose3D = _lifter.Lift(points, layout, camera, _toTracking, s, Body, time);
        return pose3D is null ? null : _solver.Solve(pose3D, time, capturedTimestamp);
    }

    /// <summary>平滑化などの内部状態を捨てる（開始し直すとき）。</summary>
    public void Reset()
    {
        _gate.Reset();
        _lifter.Reset();
        _solver.Reset();
    }
}
