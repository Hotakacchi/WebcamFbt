using System.Diagnostics;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Pose;
using WebcamFbt.Core.Threading;
using WebcamFbt.Vision.Camera;
using WebcamFbt.Vision.Inference;
using WebcamFbt.Vision.Rendering;

namespace WebcamFbt.Vision.Pipeline;

/// <summary>姿勢推定 1 回分の結果。</summary>
public readonly record struct PoseEstimatedEventArgs(
    Pose2D? Pose,
    KeypointLayout Layout,
    long CapturedTimestamp,
    int ImageWidth,
    int ImageHeight);

/// <summary>
/// カメラ 1 台の処理の流れ（フェーズ 1）。
///   [カメラスレッド] → 最新フレーム → [推論スレッド] 姿勢推定・描画 → 最新プレビュー → [UI]
/// 推論が追いつかない場合は古いフレームを捨てるので、遅延がたまらない。
/// </summary>
public sealed class SingleCameraPipeline : IDisposable
{
    private readonly OpenCvCameraSource _camera;
    private readonly IPoseEstimator2D _estimator;
    private Thread? _thread;
    private volatile bool _running;

    private readonly RateCounter _processRate = new();
    private readonly MovingAverage _detMs = new();
    private readonly MovingAverage _poseMs = new();
    private readonly MovingAverage _totalMs = new();
    private readonly MovingAverage _latencyMs = new();
    private volatile bool _personFound;
    private int _frameW, _frameH;

    /// <summary>骨格を描き込んだ最新のプレビュー。UI が取り出して Dispose する。</summary>
    public LatestSlot<PreviewFrame> Preview { get; } = new();

    /// <summary>描画に使う信頼度のしきい値（UI から変更可）。</summary>
    public float DrawScoreThreshold { get; set; } = 0.3f;

    /// <summary>プレビュー画像を作るかどうか（オフにすると描画とコピーの分だけ軽くなる）。</summary>
    public bool PreviewEnabled { get; set; } = true;

    /// <summary>
    /// 設定されている間は、姿勢推定の代わりにこれを各フレームに対して呼ぶ（推論スレッド。画像に描き込んでよい）。
    /// チェッカーボードの校正に使う。校正中は位置合わせのためプレビューを常に作る。
    /// </summary>
    public Action<Frame>? FrameProcessor
    {
        get => _frameProcessor;
        set => _frameProcessor = value;
    }
    private volatile Action<Frame>? _frameProcessor;

    /// <summary>
    /// 姿勢を推定するたびに推論スレッドから呼ばれる（人がいなければ Pose は null）。
    /// トラッカー計算はここで行う。重い処理を書くと推論が遅れるので注意。
    /// </summary>
    public event Action<PoseEstimatedEventArgs>? PoseEstimated;

    /// <summary>推論スレッドで起きたエラー。</summary>
    public event Action<string>? Error;

    public SingleCameraPipeline(OpenCvCameraSource camera, IPoseEstimator2D estimator)
    {
        _camera = camera;
        _estimator = estimator;
    }

    public void Start()
    {
        if (_running) return;
        _estimator.Reset();
        _camera.Start();
        _running = true;
        _thread = new Thread(ProcessLoop)
        {
            IsBackground = true,
            Name = "PoseInference",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void ProcessLoop()
    {
        while (_running)
        {
            var frame = _camera.Frames.Take(TimeSpan.FromMilliseconds(100));
            if (frame is null) continue;

            bool handedOff = false; // 画像の所有権をプレビューに渡したか
            try
            {
                var processor = _frameProcessor;
                if (processor != null)
                {
                    processor(frame);
                    _personFound = false;
                    _frameW = frame.Image.Cols;
                    _frameH = frame.Image.Rows;
                    _processRate.Tick();
                    Preview.Put(new PreviewFrame(frame.Image, null));
                    handedOff = true;
                    continue;
                }

                long t0 = Stopwatch.GetTimestamp();
                var result = _estimator.Estimate(frame.Image);
                _totalMs.Add(TimeUtil.ElapsedMsSince(t0));
                if (result.DetectorRan) _detMs.Add(result.DetectionMs);
                if (result.Pose != null) _poseMs.Add(result.PoseMs);
                _personFound = result.Pose != null;
                _frameW = frame.Image.Cols;
                _frameH = frame.Image.Rows;

                PoseEstimated?.Invoke(new PoseEstimatedEventArgs(
                    result.Pose, _estimator.Layout, frame.CapturedTimestamp, frame.Image.Cols, frame.Image.Rows));

                // 遅延 = カメラでフレームを受け取ってから推論が終わるまで
                // （カメラ内部・USB 転送の遅延は含まない）
                _latencyMs.Add(TimeUtil.ElapsedMsSince(frame.CapturedTimestamp));
                _processRate.Tick();

                if (PreviewEnabled)
                {
                    // フレームは使い終わるので、画像をそのままプレビューに流用する（コピーしない）
                    if (result.Pose != null)
                    {
                        PoseRenderer.Draw(frame.Image, result.Pose, DrawScoreThreshold);
                    }
                    Preview.Put(new PreviewFrame(frame.Image, result.Pose));
                    handedOff = true;
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke($"推論エラー: {ex.Message}");
                Thread.Sleep(200);
            }
            finally
            {
                if (!handedOff) frame.Dispose();
            }
        }
    }

    /// <summary>現在の統計情報。</summary>
    public PipelineStats GetStats() => new(
        _camera.CaptureRate.Rate,
        _processRate.Rate,
        _detMs.Value,
        _poseMs.Value,
        _totalMs.Value,
        _latencyMs.Value,
        _personFound,
        _estimator.Provider,
        _frameW,
        _frameH);

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _camera.Stop();
        Preview.Clear();
        _processRate.Reset();
    }

    /// <summary>
    /// 停止してカメラを解放する。姿勢推定器はモデルの読み込みに時間がかかるため、
    /// 使い回せるよう呼び出し側が管理する（ここでは破棄しない）。
    /// </summary>
    public void Dispose()
    {
        Stop();
        Preview.Dispose();
        _camera.Dispose();
    }
}
