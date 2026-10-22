using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Tracking;
using WebcamFbt.Osc;
using WebcamFbt.Vision.Pipeline;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、トラッカー計算と OSC 送信に関わる部分（フェーズ 2、ステレオはフェーズ 4c）。
/// 1 台のときはカメラ 1 の推論スレッドで単眼の 3D 化をする。2 台でステレオ校正があるときは、
/// 両カメラの結果を撮影時刻で組にして三角測量する（組ができたスレッドで処理する）。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly TrackingSettings _trackingSettings = new();
    private TrackingProcessor? _tracking;
    private readonly VrcTrackerSender _osc = new();
    private volatile TrackerFrame? _lastTrackerFrame;
    private volatile bool _oscEnabledFlag = true;

    // ---- ステレオ ----
    private readonly StereoFrameMatcher _stereoMatcher = new();
    // 2 台のときは 2 本の推論スレッドから TrackingProcessor を呼ぶので直列化する（切替の瞬間に単眼とステレオが重なる場合も）
    private readonly object _trackingLock = new();
    private volatile bool _stereoActive;

    /// <summary>2 台のとき三角測量で 3D 化する（オフにするとカメラ 1 の単眼で動く）。実行中に切り替えられる。</summary>
    [ObservableProperty] private bool _useStereoTracking = true;

    // ---- 体とカメラ ----
    [ObservableProperty] private double _heightMeters = 1.65;
    [ObservableProperty] private double _horizontalFov = 70;
    [ObservableProperty] private double _cameraPitch;
    [ObservableProperty] private double _trackingThreshold = 0.4;

    // ---- 平滑化（One Euro Filter） ----
    [ObservableProperty] private double _positionMinCutoff = 1.5;
    [ObservableProperty] private double _positionBeta = 1.0;
    [ObservableProperty] private double _rotationMinCutoff = 1.0;
    [ObservableProperty] private double _rotationBeta = 0.3;

    // ---- OSC ----
    [ObservableProperty] private bool _oscEnabled = true;
    [ObservableProperty] private string _oscHost = "127.0.0.1";
    [ObservableProperty] private int _oscPort = 9000;
    [ObservableProperty] private bool _sendHeadPosition = true;
    [ObservableProperty] private string _trackingText = "";

    // ---- キャリブレーション ----
    [ObservableProperty] private bool _isCalibrating;
    [ObservableProperty] private string _calibrationText = "キャリブレーション: 未実施（既定値で動作）";

    /// <summary>確定までに貯めるフレーム数（おおよそ 30fps で 3 秒前後）。</summary>
    private const int CalibrationFrames = 90;

    partial void OnHeightMetersChanged(double value) => _trackingSettings.HeightMeters = value;
    partial void OnHorizontalFovChanged(double value) => _trackingSettings.HorizontalFovDeg = value;
    partial void OnCameraPitchChanged(double value) => _trackingSettings.CameraPitchDownDeg = value;
    partial void OnTrackingThresholdChanged(double value) => _trackingSettings.KeypointScoreThreshold = (float)value;
    partial void OnPositionMinCutoffChanged(double value) => _trackingSettings.PositionFilter.MinCutoff = value;
    partial void OnPositionBetaChanged(double value) => _trackingSettings.PositionFilter.Beta = value;
    partial void OnRotationMinCutoffChanged(double value) => _trackingSettings.RotationFilter.MinCutoff = value;
    partial void OnRotationBetaChanged(double value) => _trackingSettings.RotationFilter.Beta = value;
    partial void OnOscEnabledChanged(bool value) => _oscEnabledFlag = value;
    partial void OnSendHeadPositionChanged(bool value) => _osc.SendHeadPosition = value;

    /// <summary>送信先（IP とポート）を反映する。</summary>
    [RelayCommand]
    private void ApplyOscEndpoint()
    {
        try
        {
            _osc.SetEndpoint(OscHost.Trim(), OscPort);
            Status = $"OSC の送信先を {_osc.Endpoint} にしました";
        }
        catch (Exception ex)
        {
            Status = $"送信先を設定できません: {ex.Message}";
        }
    }

    /// <summary>
    /// 向きを合わせる。カメラの方を向いて押すと、VRChat がトラッカー全体の向き（ヨー）を合わせる。
    /// </summary>
    [RelayCommand]
    private void AlignYaw()
    {
        _osc.RequestYawAlign();
        Status = "向きを合わせました（カメラの方を向いた状態を正面としました）";
    }

    /// <summary>
    /// キャリブレーションを開始する。カメラの方を向いて T ポーズで立った状態で押してもらう。
    /// 数秒ぶん観測を貯めて、体の比率・床の高さ・正面方向を確定する。
    /// </summary>
    [RelayCommand]
    private void Calibrate()
    {
        var tracking = _tracking;
        if (tracking == null || !IsRunning)
        {
            Status = "先に「開始」を押してください";
            return;
        }
        tracking.BeginCalibration(CalibrationFrames);
        IsCalibrating = true;
        Status = "キャリブレーション中… カメラの方を向いて T ポーズで立ってください";
    }

    /// <summary>キャリブレーション結果を破棄して既定値に戻す。</summary>
    [RelayCommand]
    private void ClearCalibration()
    {
        _tracking?.ClearCalibration();
        IsCalibrating = false;
        Status = "キャリブレーションを解除しました";
    }

    /// <summary>フィルタの値を初期値に戻す。</summary>
    [RelayCommand]
    private void ResetFilterParams()
    {
        PositionMinCutoff = 1.5;
        PositionBeta = 1.0;
        RotationMinCutoff = 1.0;
        RotationBeta = 0.3;
    }

    /// <summary>パイプライン開始時に呼ぶ。</summary>
    private void StartTracking(SingleCameraPipeline pipeline)
    {
        _tracking ??= new TrackingProcessor(_trackingSettings);
        _tracking.Reset();
        _lastTrackerFrame = null;
        pipeline.PoseEstimated += OnPoseEstimated;
        _osc.SendHeadPosition = SendHeadPosition;
        _osc.Start();
    }

    /// <summary>パイプライン停止時に呼ぶ。</summary>
    private void StopTracking()
    {
        _osc.Stop();
        _stereoActive = false;
        _stereoMatcher.Reset();
        _lastTrackerFrame = null;
        TrackingText = "";
    }

    partial void OnUseStereoTrackingChanged(bool value) => UpdateTrackingMode();

    /// <summary>
    /// 単眼とステレオのどちらで動かすかを決め直す（UI スレッド）。
    /// 2 台で動いていて、ステレオ校正があり、三角測量がオンのときだけステレオにする。
    /// </summary>
    private void UpdateTrackingMode()
    {
        var tracking = _tracking;
        if (tracking != null) tracking.Stereo = _stereoCalibration;
        bool active = _pipeline2 != null && UseStereoTracking && _stereoCalibration != null;
        if (active == _stereoActive) return;
        _stereoActive = active;
        _stereoMatcher.Reset();
        tracking?.RequestReset();
    }

    /// <summary>カメラ 1 の推論スレッドから呼ばれる。トラッカーを計算して OSC スレッドに渡す。</summary>
    private void OnPoseEstimated(PoseEstimatedEventArgs e)
    {
        if (_stereoActive)
        {
            SubmitStereo(0, e);
            return;
        }
        var tracking = _tracking;
        if (tracking == null) return;
        TrackerFrame? frame;
        lock (_trackingLock) frame = tracking.Process(e.Pose, e.Layout, e.CapturedTimestamp, e.ImageWidth, e.ImageHeight);
        Publish(frame);
    }

    /// <summary>カメラ 2 の推論スレッドから呼ばれる（ステレオのときだけ使う）。</summary>
    private void OnPoseEstimated2(PoseEstimatedEventArgs e)
    {
        if (_stereoActive) SubmitStereo(1, e);
    }

    /// <summary>相手のカメラの結果と組になったら三角測量する。</summary>
    private void SubmitStereo(int camera, PoseEstimatedEventArgs e)
    {
        var tracking = _tracking;
        if (tracking == null) return;
        var view = new StereoView(e.Pose, e.CapturedTimestamp, e.ImageWidth, e.ImageHeight);
        if (!_stereoMatcher.TrySubmit(camera, view, out var view1, out var view2)) return;
        TrackerFrame? frame;
        lock (_trackingLock) frame = tracking.ProcessStereo(view1, view2, e.Layout);
        Publish(frame);
    }

    private void Publish(TrackerFrame? frame)
    {
        _lastTrackerFrame = frame;
        if (frame != null && _oscEnabledFlag) _osc.Submit(frame);
    }

    private void UpdateTrackingText()
    {
        UpdateCalibration();

        var frame = _lastTrackerFrame;
        var sb = new StringBuilder();
        sb.AppendLine($"OSC     {(OscEnabled ? $"{_osc.SendRate.Rate,5:F0} fps → {_osc.Endpoint}" : "送信オフ")}");
        if (_osc.LastError != null) sb.AppendLine($"OSC エラー: {_osc.LastError}");
        sb.AppendLine($"方式    {(_stereoActive ? "ステレオ（三角測量）" : "単眼")}");
        if (_stereoActive && _tracking?.LastStereoStats is { } st)
        {
            // 精度の目安: 三角測量できた関節の数、再投影誤差、左右の脚の長さの差（大きければ測定がずれている）
            var leg = st.LegLengthDiffMeters is { } d ? $"  脚の長さ差 {d * 100,3:F0}cm" : "";
            sb.AppendLine($"精度    {st.Triangulated,2}/{st.Candidates,2}点  誤差 {st.MeanReprojErrorPx,4:F1}px{leg}{(st.Swapped ? "  左右入替" : "")}");
        }
        if (frame == null)
        {
            var problem = _stereoActive ? _tracking?.StereoProblem : null;
            sb.Append(problem != null ? $"トラッカー: {problem}" : "トラッカー: 体が見えていません");
        }
        else
        {
            sb.AppendLine($"距離    {frame.BodyDepth,5:F2} m   向き {frame.BodyYawDeg,5:F0}°");
            foreach (var t in frame.Trackers)
            {
                string name = t.Role switch
                {
                    TrackerRole.Hip => "腰  ",
                    TrackerRole.LeftFoot => "左足",
                    TrackerRole.RightFoot => "右足",
                    _ => t.Role.ToString(),
                };
                sb.AppendLine(t.Valid
                    ? $"{name}[{t.OscIndex}] ({t.Position.X,5:F2},{t.Position.Y,5:F2},{t.Position.Z,5:F2}) " +
                      $"rot({t.EulerDeg.X,4:F0},{t.EulerDeg.Y,4:F0},{t.EulerDeg.Z,4:F0})"
                    : $"{name}[{t.OscIndex}] 見えていません");
            }
            var h = frame.HeadPosition;
            sb.Append(frame.HeadValid ? $"頭       ({h.X,5:F2},{h.Y,5:F2},{h.Z,5:F2})" : "頭       見えていません");
        }
        TrackingText = sb.ToString();
    }

    /// <summary>キャリブレーションの進捗・完了・結果の表示を更新する（UI スレッド）。</summary>
    private void UpdateCalibration()
    {
        var tracking = _tracking;
        if (tracking == null) return;

        if (tracking.TryTakeCalibrationResult(out bool success))
        {
            IsCalibrating = false;
            Status = success
                ? "キャリブレーション完了"
                : "キャリブレーション失敗（体全体が映っていませんでした）";
        }
        if (IsCalibrating && !tracking.IsCalibrating) IsCalibrating = false;

        if (tracking.IsCalibrating)
        {
            CalibrationText = $"キャリブレーション中… {tracking.CalibrationFrameCount}/{tracking.CalibrationTarget}";
            return;
        }

        var cal = tracking.Calibration;
        if (!cal.IsCalibrated)
        {
            CalibrationText = "キャリブレーション: 未実施（既定値で動作）";
            return;
        }

        var b = cal.Body;
        var sb = new StringBuilder();
        sb.AppendLine("キャリブレーション済み");
        if (cal.HasFloor) sb.AppendLine($"床の高さ {cal.FloorY,5:F2} m   正面補正 {cal.YawOffsetDeg,4:F0}°");
        if (cal.Tilt is not null) sb.AppendLine($"カメラの傾き補正 {cal.TiltDeg,4:F1}°（ステレオで測定。この後カメラの傾きの設定は変えないでください）");
        sb.Append($"比率 肩{b.ShoulderWidth:F2} 腰{b.HipWidth:F2} 胴{b.Torso:F2} 腿{b.Thigh:F2} すね{b.Shin:F2} 足{b.Foot:F2}");
        CalibrationText = sb.ToString();
    }
}
