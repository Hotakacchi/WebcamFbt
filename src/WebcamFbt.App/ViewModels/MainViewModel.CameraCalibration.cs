using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Vision.Calibration;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、チェッカーボードによるカメラの校正に関わる部分（フェーズ 4b）。
/// 1 台モードではカメラ 1 の内部パラメータ、2 台モードでは両カメラの内部パラメータと相対姿勢（ステレオ）を求めて保存する。
/// カメラ 1 の内部パラメータは単眼トラッキングにも使う（画角による近似の代わり＋歪み補正）。
/// </summary>
public sealed partial class MainViewModel
{
    private const string CameraCalibrationIdleText = "「開始」後にカメラを校正できます";

    /// <summary>集めるサンプル数（ステレオは組の数）。</summary>
    private const int CalibrationSampleTarget = 15;

    private readonly CameraCalibrationStore _calibrationStore = new();
    private CheckerboardCaptureSession? _captureSession;
    private bool _solvingCameraCalibration;
    private CameraCalibration? _camera1Calibration;
    private StereoCalibration? _stereoCalibration;

    // ---- チェッカーボード ----
    [ObservableProperty] private int _boardInnerCols = 9;
    [ObservableProperty] private int _boardInnerRows = 6;
    [ObservableProperty] private double _boardSquareMm = 25;

    [ObservableProperty] private bool _isCameraCalibrating;
    [ObservableProperty] private string _cameraCalibrationText = CameraCalibrationIdleText;

    public bool CanEditBoard => !IsCameraCalibrating;

    partial void OnIsCameraCalibratingChanged(bool value) => OnPropertyChanged(nameof(CanEditBoard));

    /// <summary>撮影を始める。集まったら自動で計算して保存する。</summary>
    [RelayCommand]
    private void StartCameraCalibration()
    {
        if (!IsRunning || _pipeline == null)
        {
            Status = "先に「開始」を押してください";
            return;
        }
        if (_solvingCameraCalibration || IsCameraCalibrating) return;
        if (BoardInnerCols < 3 || BoardInnerRows < 3 || BoardSquareMm <= 0)
        {
            Status = "チェッカーボードの設定を確認してください（交点は 3 以上、マスの大きさは正の値）";
            return;
        }

        bool stereo = _pipeline2 != null;
        var spec = new CheckerboardSpec(BoardInnerCols, BoardInnerRows, BoardSquareMm / 1000.0);
        var session = new CheckerboardCaptureSession(spec, stereo, CalibrationSampleTarget);
        _captureSession = session;
        _pipeline.FrameProcessor = session.Process;
        if (_pipeline2 != null) _pipeline2.FrameProcessor = session.Process;
        IsCameraCalibrating = true;
        Status = stereo
            ? "校正中… 2 台の両方に写る位置で、ボードの位置と傾きを変えながら毎回少し止めてください"
            : "校正中… ボードの位置と傾きを変えながら毎回少し止めてください（画面の端の方にも写すと精度が上がります）";
    }

    [RelayCommand]
    private void CancelCameraCalibration()
    {
        if (!IsCameraCalibrating) return;
        EndCameraCapture();
        Status = "カメラの校正を中止しました";
    }

    /// <summary>撮影をやめて、パイプラインを姿勢推定に戻す。</summary>
    private void EndCameraCapture()
    {
        if (_pipeline != null) _pipeline.FrameProcessor = null;
        if (_pipeline2 != null) _pipeline2.FrameProcessor = null;
        _captureSession = null;
        IsCameraCalibrating = false;
    }

    /// <summary>開始時に、保存してある校正結果を読み込んでトラッキングに反映する。</summary>
    private void LoadCameraCalibration(int device1, int width, int height)
    {
        _camera1Calibration = _calibrationStore.GetCamera(device1, width, height);
        _stereoCalibration = _pipeline2 != null ? _calibrationStore.GetStereo(device1, SelectedCamera2.Value) : null;
        if (_tracking != null) _tracking.Intrinsics = _camera1Calibration;
        UpdateTrackingMode();
        CameraCalibrationText = DescribeCameraCalibration();
    }

    /// <summary>停止時に呼ぶ。</summary>
    private void ResetCameraCalibrationView()
    {
        EndCameraCapture();
        CameraCalibrationText = CameraCalibrationIdleText;
    }

    /// <summary>UI スレッドのタイマーから呼ぶ。撮影の進捗を表示し、集まったら計算を始める。</summary>
    private void UpdateCameraCalibration()
    {
        var session = _captureSession;
        if (session == null) return;

        var c = session.Collector;
        CameraCalibrationText = c.IsStereo
            ? $"撮影中  組 {c.PairCount}/{c.TargetCount}  カメラ1 {c.SampleCount(0)}/{c.TargetCount}  カメラ2 {c.SampleCount(1)}/{c.TargetCount}"
            : $"撮影中  {c.SampleCount(0)}/{c.TargetCount} 枚";
        if (c.IsComplete) _ = SolveCameraCalibrationAsync(session);
    }

    private async Task SolveCameraCalibrationAsync(CheckerboardCaptureSession session)
    {
        EndCameraCapture();
        _solvingCameraCalibration = true;
        Status = "校正を計算しています…";
        CameraCalibrationText = "計算中…";

        var c = session.Collector;
        var spec = session.Spec;
        int device1 = SelectedCamera.Value;
        int device2 = SelectedCamera2.Value;
        try
        {
            if (c.IsStereo)
            {
                var stereo = await Task.Run(() =>
                {
                    var (w1, h1) = c.ImageSize(0);
                    var (w2, h2) = c.ImageSize(1);
                    var cal1 = CameraCalibrationSolver.CalibrateMono(c.GetSamples(0), spec, w1, h1);
                    var cal2 = CameraCalibrationSolver.CalibrateMono(c.GetSamples(1), spec, w2, h2);
                    return CameraCalibrationSolver.CalibrateStereo(c.GetPairs(), cal1, cal2, spec, device1, device2);
                });
                _calibrationStore.SaveStereo(stereo);
                _stereoCalibration = stereo;
                ApplyCamera1Calibration(stereo.Camera1);
                UpdateTrackingMode();
                Status = $"ステレオ校正が完了しました（基線 {stereo.BaselineMeters:F2} m、誤差 {stereo.ReprojErrorPx:F2} px）";
            }
            else
            {
                var (w, h) = c.ImageSize(0);
                var cal = await Task.Run(() => CameraCalibrationSolver.CalibrateMono(c.GetSamples(0), spec, w, h));
                _calibrationStore.SaveCamera(device1, cal);
                ApplyCamera1Calibration(cal);
                Status = $"カメラの校正が完了しました（誤差 {cal.ReprojErrorPx:F2} px）";
            }
        }
        catch (Exception ex)
        {
            Status = $"校正に失敗しました: {ex.Message}";
        }
        finally
        {
            _solvingCameraCalibration = false;
            CameraCalibrationText = IsRunning ? DescribeCameraCalibration() : CameraCalibrationIdleText;
        }
    }

    private void ApplyCamera1Calibration(CameraCalibration calibration)
    {
        _camera1Calibration = calibration;
        if (_tracking != null) _tracking.Intrinsics = calibration;
    }

    private string DescribeCameraCalibration()
    {
        var sb = new StringBuilder();
        var cal = _camera1Calibration;
        sb.Append(cal != null
            ? $"カメラ1: 校正済み  f={cal.Fx:F0}px  誤差 {cal.ReprojErrorPx:F2}px\n（画角の設定は使いません）"
            : "カメラ1: 未校正（画角の設定で近似）");
        if (_pipeline2 != null)
        {
            var s = _stereoCalibration;
            sb.Append(s != null
                ? $"\nステレオ: 校正済み  基線 {s.BaselineMeters:F2} m  誤差 {s.ReprojErrorPx:F2}px"
                : "\nステレオ: 未校正");
        }
        return sb.ToString();
    }
}
