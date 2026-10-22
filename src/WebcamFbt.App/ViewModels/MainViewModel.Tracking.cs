using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Tracking;
using WebcamFbt.Osc;
using WebcamFbt.Vision.Pipeline;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、トラッカー計算と OSC 送信に関わる部分（フェーズ 2）。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly TrackingSettings _trackingSettings = new();
    private TrackingProcessor? _tracking;
    private readonly VrcTrackerSender _osc = new();
    private volatile TrackerFrame? _lastTrackerFrame;
    private volatile bool _oscEnabledFlag = true;

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
        _lastTrackerFrame = null;
        TrackingText = "";
    }

    /// <summary>推論スレッドから呼ばれる。トラッカーを計算して OSC スレッドに渡す。</summary>
    private void OnPoseEstimated(PoseEstimatedEventArgs e)
    {
        var tracking = _tracking;
        if (tracking == null) return;
        var frame = tracking.Process(e.Pose, e.Layout, e.CapturedTimestamp, e.ImageWidth, e.ImageHeight);
        _lastTrackerFrame = frame;
        if (frame != null && _oscEnabledFlag) _osc.Submit(frame);
    }

    private void UpdateTrackingText()
    {
        var frame = _lastTrackerFrame;
        var sb = new StringBuilder();
        sb.AppendLine($"OSC     {(OscEnabled ? $"{_osc.SendRate.Rate,5:F0} fps → {_osc.Endpoint}" : "送信オフ")}");
        if (_osc.LastError != null) sb.AppendLine($"OSC エラー: {_osc.LastError}");
        if (frame == null)
        {
            sb.Append("トラッカー: 体が見えていません");
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
}
