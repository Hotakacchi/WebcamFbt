using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WebcamFbt.Vision.Camera;
using WebcamFbt.Vision.Inference;
using WebcamFbt.Vision.Pipeline;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、2 台目のカメラに関わる部分（フェーズ 4a）。
/// 2 台を同時にプレビューして各カメラの 2D 推定を表示する。ステレオ校正があれば、
/// カメラ 2 の推定結果も三角測量に使う（MainViewModel.Tracking.cs、フェーズ 4c）。
/// </summary>
public sealed partial class MainViewModel
{
    private IPoseEstimator2D? _estimator2;
    private InferenceDevice _estimator2Device;
    private SingleCameraPipeline? _pipeline2;

    /// <summary>カメラを 2 台使う（ステレオ）。実行中は変更不可。</summary>
    [ObservableProperty] private bool _twoCameraMode;

    /// <summary>2 台目のカメラのデバイス。解像度・fps・取得方式はカメラ 1 と同じにする。</summary>
    [ObservableProperty] private Choice<int> _selectedCamera2;

    [ObservableProperty] private WriteableBitmap? _previewImage2;
    [ObservableProperty] private string _statsText2 = "";

    /// <summary>2 台目のカメラ・推定器を用意して開始する（<see cref="TwoCameraMode"/> のときに呼ぶ）。</summary>
    private async Task StartSecondCameraAsync(
        InferenceDevice device,
        IProgress<(double Ratio, string Message)> progress,
        CancellationToken ct)
    {
        if (SelectedCamera2.Value == SelectedCamera.Value)
        {
            throw new InvalidOperationException("カメラ 1 と 2 には別のデバイスを選んでください。");
        }

        // 推定器はカメラごとに別インスタンスが必要（1 セッションを複数スレッドから呼べないため）
        if (_estimator2 == null || _estimator2Device != device)
        {
            _estimator2?.Dispose();
            _estimator2 = null;
            progress.Report((0, "カメラ 2 用の推論エンジンを初期化中…"));
            _estimator2 = await PoseEstimatorFactory.CreateDefaultAsync(_modelStore, device, progress, ct);
            _estimator2Device = device;
        }

        Status = "カメラ 2 を開いています…";
        var settings = new CameraSettings(
            SelectedCamera2.Value,
            SelectedResolution.Value.W,
            SelectedResolution.Value.H,
            SelectedFps.Value,
            SelectedBackend.Value);
        var camera = new OpenCvCameraSource(settings, cameraIndex: 1);
        camera.Error += msg => PostStatus(msg);

        var pipeline = new SingleCameraPipeline(camera, _estimator2)
        {
            DrawScoreThreshold = (float)ScoreThreshold,
            PreviewEnabled = PreviewEnabled,
        };
        pipeline.Error += msg => PostStatus(msg);
        pipeline.PoseEstimated += OnPoseEstimated2;

        try
        {
            await Task.Run(pipeline.Start, ct);
        }
        catch
        {
            pipeline.Dispose();
            throw;
        }
        _pipeline2 = pipeline;
    }

    private void StopSecondCamera()
    {
        _pipeline2?.Dispose();
        _pipeline2 = null;
        PreviewImage2 = null;
        StatsText2 = "";
    }

    private void UpdatePreview2()
    {
        var preview = _pipeline2?.Preview.TryTake();
        if (preview == null) return;
        using (preview)
        {
            var mat = preview.Image;
            if (MirrorPreview)
            {
                Cv2.Flip(mat, mat, FlipMode.Y);
            }

            if (PreviewImage2 == null || PreviewImage2.PixelWidth != mat.Width || PreviewImage2.PixelHeight != mat.Height)
            {
                PreviewImage2 = mat.ToWriteableBitmap();
            }
            else
            {
                WriteableBitmapConverter.ToWriteableBitmap(mat, PreviewImage2);
            }
        }
    }

    private void UpdateStats2()
    {
        if (_pipeline2 == null) return;
        var s = _pipeline2.GetStats();
        StatsText2 =
            $"カメラ2  {s.CameraFps,5:F0} fps   ({s.FrameWidth}×{s.FrameHeight})\n" +
            $"処理    {s.ProcessFps,5:F0} fps\n" +
            $"姿勢    {s.PoseMs,5:F1} ms\n" +
            $"人物    {(s.PersonFound ? "検出中" : "見つかりません")}";
    }
}
