using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using WebcamFbt.Mac;
using WebcamFbt.Mac.Platform;
using WebcamFbt.Mac.Rendering;
using WebcamFbt.Vision.Camera;
using WebcamFbt.Vision.Inference;
using WebcamFbt.Vision.Models;
using WebcamFbt.Vision.Pipeline;

// 名前空間は Windows 版と同じにする。トラッキング・校正・手順ガイド・設定の保存の partial ファイルは
// Windows 版（src/WebcamFbt.App/ViewModels）のものをそのままコンパイルして共有するため（WebcamFbt.Mac.csproj を参照）。
namespace WebcamFbt.App.ViewModels;

/// <summary>選択肢（表示名と値の組）。</summary>
public sealed record Choice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// Mac 版のメイン画面の ViewModel（カメラ・推論・プレビュー部分）。
/// Windows 版（WebcamFbt.App の MainViewModel.cs）を Avalonia 向けに写したもの。項目名・文言は Windows 版とそろえる。
/// Windows 版との違い: プレビューの画像（Avalonia の Bitmap）、取得方式（AVFoundation のみ）、推論（CoreML）、
/// 実行中のスリープ防止、エラーのログファイル。
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ModelStore _modelStore = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly MatBitmap _previewBitmap = new();
    private readonly SleepBlocker _sleepBlocker = new();
    private IPoseEstimator2D? _estimator;
    private InferenceDevice _estimatorDevice;
    private SingleCameraPipeline? _pipeline;
    private CancellationTokenSource? _startCts;

    // ---- 選択肢 ----
    public ObservableCollection<Choice<int>> CameraChoices { get; } =
        new(Enumerable.Range(0, 6).Select(i => new Choice<int>($"カメラ {i}", i)));

    public ObservableCollection<Choice<(int W, int H)>> ResolutionChoices { get; } =
    [
        new("640 × 480", (640, 480)),
        new("1280 × 720", (1280, 720)),
        new("1920 × 1080", (1920, 1080)),
    ];

    public ObservableCollection<Choice<int>> FpsChoices { get; } = [new("30 fps", 30), new("60 fps", 60)];

    // Mac の取得方式は AVFoundation だけ（Windows 版と画面をそろえるため選択肢として見せる。値は使わない）
    public ObservableCollection<Choice<CameraBackend>> BackendChoices { get; } =
    [
        new("AVFoundation", CameraBackend.MediaFoundation),
    ];

    public ObservableCollection<Choice<InferenceDevice>> DeviceChoices { get; } =
    [
        new("自動（GPU 優先）", InferenceDevice.Auto),
        new("GPU（CoreML）", InferenceDevice.CoreML),
        new("CPU", InferenceDevice.Cpu),
    ];

    // ---- 設定 ----
    [ObservableProperty] private Choice<int> _selectedCamera;
    [ObservableProperty] private Choice<(int W, int H)> _selectedResolution;
    [ObservableProperty] private Choice<int> _selectedFps;
    [ObservableProperty] private Choice<CameraBackend> _selectedBackend;
    [ObservableProperty] private Choice<InferenceDevice> _selectedDevice;
    [ObservableProperty] private double _scoreThreshold = 0.3;
    [ObservableProperty] private bool _mirrorPreview = true;
    [ObservableProperty] private bool _previewEnabled = true;

    // ---- 状態 ----
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "停止中";
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private double _progress;

    public string StartStopLabel => IsRunning ? "停止" : "開始";
    public bool CanEditSettings => !IsRunning && !IsBusy;

    public MainViewModel()
    {
        _selectedCamera = CameraChoices[0];
        _selectedCamera2 = CameraChoices.Count > 1 ? CameraChoices[1] : CameraChoices[0];
        _selectedResolution = ResolutionChoices[1];
        // Mac の内蔵カメラ（FaceTime HD）は 30fps までのものが多い
        _selectedFps = FpsChoices[0];
        _selectedBackend = BackendChoices[0];
        _selectedDevice = DeviceChoices[0];

        // プレビューは画面の更新に合わせて取り出す（最新の 1 枚だけ表示）
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _previewTimer.Tick += (_, _) => UpdatePreview();
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        InitializeSettings();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StartStopLabel));
        OnPropertyChanged(nameof(CanEditSettings));
        UpdateGuide();

        // 実行中は Mac がスリープしたり App Nap で減速したりしないようにする（Quest で遊ぶ間は Mac を触らないため）
        if (value) _sleepBlocker.Begin("WebcamFbt: カメラでトラッキングして VRChat に送信中");
        else _sleepBlocker.End();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditSettings));

    partial void OnScoreThresholdChanged(double value)
    {
        if (_pipeline != null) _pipeline.DrawScoreThreshold = (float)value;
        if (_pipeline2 != null) _pipeline2.DrawScoreThreshold = (float)value;
    }

    partial void OnPreviewEnabledChanged(bool value)
    {
        ApplyPreviewEnabled();
        if (!value) { PreviewImage = null; PreviewImage2 = null; }
    }

    /// <summary>ウィンドウを閉じて裏で動いている間など、プレビューを一時的に止めているか（設定は変えない）。</summary>
    private bool _previewSuspended;

    /// <summary>実際にプレビューを作るか（設定がオンで、一時的に止めていないとき）。</summary>
    private bool EffectivePreview => PreviewEnabled && !_previewSuspended;

    /// <summary>
    /// プレビューを一時的に止める・再開する（ウィンドウが見えなくなった・見えるようになったとき）。
    /// 見えない映像の描画とコピーを省いて負荷を減らす。トラッキングと OSC の送信は続く。
    /// </summary>
    public void SuspendPreview(bool suspended)
    {
        _previewSuspended = suspended;
        ApplyPreviewEnabled();
    }

    private void ApplyPreviewEnabled()
    {
        if (_pipeline != null) _pipeline.PreviewEnabled = EffectivePreview;
        if (_pipeline2 != null) _pipeline2.PreviewEnabled = EffectivePreview;
    }

    // 準備中にもう一度押して中止できるよう、同時実行を許可する
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StartStopAsync()
    {
        if (IsBusy)
        {
            // 準備中に押されたら中止
            _startCts?.Cancel();
            return;
        }
        if (IsRunning)
        {
            StopPipeline();
            return;
        }
        await StartPipelineAsync();
    }

    private async Task StartPipelineAsync()
    {
        IsBusy = true;
        _startCts = new CancellationTokenSource();
        var progress = new Progress<(double Ratio, string Message)>(p =>
        {
            Progress = p.Ratio;
            Status = p.Message;
        });

        try
        {
            // 推定器はデバイスが変わったときだけ作り直す（モデル読み込みは数秒かかる。CoreML は初回の変換がさらに遅い）
            var device = SelectedDevice.Value;
            if (_estimator == null || _estimatorDevice != device)
            {
                _estimator?.Dispose();
                _estimator = null;
                _estimator = await PoseEstimatorFactory.CreateDefaultAsync(_modelStore, device, progress, _startCts.Token);
                _estimatorDevice = device;
            }

            Status = "カメラを開いています…";
            var settings = new CameraSettings(
                SelectedCamera.Value,
                SelectedResolution.Value.W,
                SelectedResolution.Value.H,
                SelectedFps.Value,
                SelectedBackend.Value);
            var camera = new OpenCvCameraSource(settings);
            camera.Error += msg => PostStatus(msg);

            var pipeline = new SingleCameraPipeline(camera, _estimator)
            {
                DrawScoreThreshold = (float)ScoreThreshold,
                PreviewEnabled = EffectivePreview,
            };
            pipeline.Error += msg => PostStatus(msg);
            StartTracking(pipeline);

            try
            {
                // カメラを開く処理は環境によって数秒かかる（初回はカメラの許可待ちもある）ので UI スレッド外で行う
                await Task.Run(pipeline.Start, _startCts.Token);
            }
            catch
            {
                pipeline.Dispose();
                StopTracking();
                throw;
            }

            _pipeline = pipeline;
            IsRunning = true;
            var fmt = camera.ActualFormat;
            Status = $"実行中（カメラ {settings.DeviceIndex}: {fmt.Width}×{fmt.Height} @ {fmt.Fps:F0}fps）";

            if (TwoCameraMode)
            {
                try
                {
                    await StartSecondCameraAsync(device, progress, _startCts.Token);
                    Status = $"実行中（2 台: カメラ {settings.DeviceIndex} + {SelectedCamera2.Value}）";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    CrashLog.Write($"カメラ 2 の開始に失敗: {ex}");
                    StopPipeline();
                    Status = $"カメラ 2 を開始できませんでした: {ex.Message}";
                    return;
                }
            }

            LoadCameraCalibration(settings.DeviceIndex, fmt.Width, fmt.Height);
            _previewTimer.Start();
            _statsTimer.Start();
        }
        catch (OperationCanceledException)
        {
            Status = "中止しました";
        }
        catch (Exception ex)
        {
            CrashLog.Write($"開始に失敗: {ex}");
            Status = $"開始できませんでした: {ex.Message}" +
                     (ex.Message.Contains("カメラ") ? "\nシステム設定 → プライバシーとセキュリティ → カメラ で WebcamFbt が許可されているか確認してください。" : "");
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _startCts.Dispose();
            _startCts = null;
        }
    }

    private void StopPipeline()
    {
        _previewTimer.Stop();
        _statsTimer.Stop();
        ResetCameraCalibrationView();
        _pipeline?.Dispose();
        _pipeline = null;
        StopSecondCamera();
        StopTracking();
        IsRunning = false;
        Status = "停止中";
    }

    private void PostStatus(string message)
    {
        Dispatcher.UIThread.Post(() => { Status = message; });
    }

    private void UpdatePreview()
    {
        UpdatePreview2();

        // 上から見た図は画面の更新に合わせて描き直す（新しいトラッカーの結果が来たときだけ）
        var trackerFrame = _lastTrackerFrame;
        if (!ReferenceEquals(trackerFrame, TopDownFrame)) TopDownFrame = trackerFrame;

        var preview = _pipeline?.Preview.TryTake();
        if (preview == null) return;
        using (preview)
        {
            var mat = preview.Image;
            if (MirrorPreview)
            {
                // 鏡のように左右反転して表示する（推定結果の左右は変わらない）
                Cv2.Flip(mat, mat, FlipMode.Y);
            }
            PreviewImage = _previewBitmap.Update(mat);
        }
    }

    private void UpdateStats()
    {
        if (_pipeline == null) return;
        var s = _pipeline.GetStats();
        StatsText =
            $"カメラ  {s.CameraFps,5:F0} fps   ({s.FrameWidth}×{s.FrameHeight})\n" +
            $"処理    {s.ProcessFps,5:F0} fps\n" +
            $"検出    {s.DetectionMs,5:F1} ms   (実行したとき)\n" +
            $"姿勢    {s.PoseMs,5:F1} ms\n" +
            $"合計    {s.TotalProcessMs,5:F1} ms\n" +
            $"遅延    {s.LatencyMs,5:F1} ms   (取得→推論完了)\n" +
            $"実行    {s.Provider}\n" +
            $"人物    {(s.PersonFound ? "検出中" : "見つかりません")}";
        UpdateTrackingText();
        UpdateStats2();
        UpdateCameraCalibration();
        UpdateGuide();
    }

    public void Dispose()
    {
        SaveSettingsNow();
        StopPipeline();
        _estimator?.Dispose();
        _estimator = null;
        _estimator2?.Dispose();
        _estimator2 = null;
        _osc.Dispose();
        _previewBitmap.Dispose();
        _previewBitmap2.Dispose();
    }
}
