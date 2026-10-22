using System.Diagnostics;
using OpenCvSharp;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Threading;

namespace WebcamFbt.Vision.Camera;

/// <summary>
/// OpenCV の VideoCapture でカメラ映像を取得する。
/// 専用スレッドで読み続け、最新のフレームだけを <see cref="Frames"/> に置く（古いフレームは捨てる）。
/// </summary>
public sealed class OpenCvCameraSource : IDisposable
{
    private readonly CameraSettings _settings;
    private readonly int _cameraIndex;
    private VideoCapture? _capture;
    private Thread? _thread;
    private volatile bool _running;
    private long _frameId;

    /// <summary>最新フレームの受け渡し口。取り出した側が Dispose する。</summary>
    public LatestSlot<Frame> Frames { get; } = new();

    /// <summary>実際に取得できているフレームレート。</summary>
    public RateCounter CaptureRate { get; } = new();

    /// <summary>カメラが実際に採用した解像度（開いた後に確定）。</summary>
    public (int Width, int Height, double Fps) ActualFormat { get; private set; }

    /// <summary>取得スレッドで起きたエラー（UI 表示用）。</summary>
    public event Action<string>? Error;

    public OpenCvCameraSource(CameraSettings settings, int cameraIndex = 0)
    {
        _settings = settings;
        _cameraIndex = cameraIndex;
    }

    /// <summary>カメラを開いて取得スレッドを開始する。開けなければ例外。</summary>
    public void Start()
    {
        if (_running) return;

        var api = _settings.Backend == CameraBackend.DirectShow ? VideoCaptureAPIs.DSHOW : VideoCaptureAPIs.MSMF;
        _capture = new VideoCapture(_settings.DeviceIndex, api);
        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            _capture = null;
            throw new InvalidOperationException($"カメラ {_settings.DeviceIndex} を開けませんでした（{_settings.Backend}）。");
        }

        // MJPG を指定すると、USB 帯域の制約で 720p 以上でも高 fps が出やすい
        if (_settings.UseMjpg)
        {
            _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
        }
        _capture.Set(VideoCaptureProperties.FrameWidth, _settings.Width);
        _capture.Set(VideoCaptureProperties.FrameHeight, _settings.Height);
        _capture.Set(VideoCaptureProperties.Fps, _settings.Fps);
        // 内部バッファを最小にして遅延を減らす（対応していないバックエンドでは無視される）
        _capture.Set(VideoCaptureProperties.BufferSize, 1);

        ActualFormat = (
            (int)_capture.Get(VideoCaptureProperties.FrameWidth),
            (int)_capture.Get(VideoCaptureProperties.FrameHeight),
            _capture.Get(VideoCaptureProperties.Fps));

        _running = true;
        _thread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = $"Camera{_cameraIndex}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        int consecutiveFailures = 0;
        while (_running)
        {
            var mat = new Mat();
            bool ok;
            try
            {
                // Read はフレームが届くまでブロックする
                ok = _capture!.Read(mat) && !mat.Empty();
            }
            catch (Exception ex)
            {
                mat.Dispose();
                Error?.Invoke($"カメラ読み取りエラー: {ex.Message}");
                Thread.Sleep(100);
                continue;
            }

            if (!ok)
            {
                mat.Dispose();
                if (++consecutiveFailures == 50)
                {
                    Error?.Invoke("カメラからフレームを取得できません。他のアプリが使用中でないか確認してください。");
                }
                Thread.Sleep(10);
                continue;
            }

            consecutiveFailures = 0;
            long ts = Stopwatch.GetTimestamp();
            CaptureRate.Tick();
            Frames.Put(new Frame(mat, _cameraIndex, Interlocked.Increment(ref _frameId), ts));
        }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        Frames.Clear();
        CaptureRate.Reset();
    }

    public void Dispose()
    {
        Stop();
        Frames.Dispose();
    }
}
