// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.Diagnostics;
using System.IO;
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

    /// <summary>このフレーム数ボードが見えなければ「見つからない」と案内する（ボードを探す間は処理が遅いので数秒）。</summary>
    private const int BoardNotFoundFrames = 45;

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

    /// <summary>校正中の案内（ボードが見えているか、見つからないときの確かめ方）。</summary>
    [ObservableProperty] private string _cameraCalibrationHint = "";

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
        CameraCalibrationText = stereo ? "撮影中  組 0/15" : "撮影中  0/15 枚";
        CameraCalibrationHint = "ボードを探しています…";
        Status = stereo
            ? "カメラの校正中（トラッキングは止まっています）… 2 台の両方に写る位置で、ボードの位置と傾きを変えながら毎回少し止めてください"
            : "カメラの校正中（トラッキングは止まっています）… ボードの位置と傾きを変えながら毎回少し止めてください（画面の端の方にも写すと精度が上がります）";
    }

    /// <summary>
    /// 印刷用のチェッカーボードの画像を、今の設定（内側の交点の数）で保存して開く。
    /// 印刷の拡大・縮小で大きさが変わるので、印刷したものの 1 マスを定規で測って入力してもらう。
    /// </summary>
    [RelayCommand]
    private async Task SaveCheckerboardImage()
    {
        int cols = BoardInnerCols, rows = BoardInnerRows;
        if (cols < 3 || rows < 3)
        {
            Status = "チェッカーボードの交点の数を確認してください（3 以上）";
            return;
        }
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var path = Path.Combine(folder, $"WebcamFbt-checkerboard-{cols}x{rows}.png");
        try
        {
            await Task.Run(() => CheckerboardImage.Save(path, cols, rows));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status = $"印刷用のボードを保存しました（{path}）。印刷したら、1 マスの大きさを定規で測って入力してください";
        }
        catch (Exception ex)
        {
            Status = $"ボードの画像を作れませんでした: {ex.Message}";
        }
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
        CameraCalibrationHint = "";
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
        CameraCalibrationHint = DescribeBoardSearch(c, session.Spec);
        if (c.IsComplete) _ = SolveCameraCalibrationAsync(session);
    }

    /// <summary>ボードが見えているか、見つからないときは何を確かめればよいかの案内。</summary>
    private static string DescribeBoardSearch(CheckerboardSampleCollector c, CheckerboardSpec spec)
    {
        int cameras = c.IsStereo ? 2 : 1;
        bool allVisible = true, anyNotFound = false;
        for (int i = 0; i < cameras; i++)
        {
            allVisible &= c.IsBoardVisible(i);
            anyNotFound |= !c.IsBoardVisible(i) && c.FramesWithoutBoard(i) >= BoardNotFoundFrames;
        }

        if (allVisible) return "ボードが見えています。位置や傾きを変えながら、毎回少し止めてください";
        if (!anyNotFound) return "ボードを探しています…";

        var where = c.IsStereo
            ? (c.IsBoardVisible(0) ? "カメラ2 に" : c.IsBoardVisible(1) ? "カメラ1 に" : "") + "ボードが見つかりません。"
            : "ボードが見つかりません。";
        return where + $"全体が映っているか、印刷したボードの内側の交点が 横{spec.InnerCols}×縦{spec.InnerRows} か確かめてください。"
            + "ボードが無ければ「校正」タブの「印刷用のボードを保存」で作れます";
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
