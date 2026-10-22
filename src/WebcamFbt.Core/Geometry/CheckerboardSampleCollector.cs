using System.Numerics;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// チェッカーボードの検出結果から、校正に使うサンプルを選んで貯める。
///
/// 採用条件:
///  - ボードが止まっている（直前の検出からコーナーがほとんど動いていない）。ブレた画像を避けるため
///  - これまでに採ったどのサンプルとも位置・向きが十分違う（似た画像ばかりだと校正が不安定になる）
///  - 前回の採用から一定時間たっている
/// ステレオでは、2 台がほぼ同じ時刻に止まったボードを捉えたときに「組」として採る。
/// あわせて、各カメラ単体のサンプル（内部パラメータ用）も同じ条件で貯める。
///
/// 推論スレッド（カメラごとに 1 本）から <see cref="Submit"/> し、UI スレッドから読むのでロックで守る。
/// </summary>
public sealed class CheckerboardSampleCollector
{
    private sealed record Detection(double Time, Vector2[] Corners);

    private sealed class CameraState
    {
        public Detection? Last;      // 直前の検出（静止判定用）
        public Detection? LastStill; // 直近の「止まっている」検出（ステレオの組み合わせ用）
        public readonly List<Vector2[]> Samples = new();
        public double LastAcceptTime = double.NegativeInfinity;
        public int Width, Height;
    }

    private readonly object _lock = new();
    private readonly CameraState[] _cameras = [new(), new()];
    private readonly List<(Vector2[] Camera1, Vector2[] Camera2)> _pairs = new();
    private double _lastPairTime = double.NegativeInfinity;

    /// <summary>集めるサンプル数（ステレオは組の数）。</summary>
    public int TargetCount { get; }

    public bool IsStereo { get; }

    /// <summary>静止とみなすコーナーの平均移動量（画像の幅に対する比率。1280px で約 5px）。</summary>
    public double StillRatio { get; init; } = 0.004;

    /// <summary>新しいサンプルとみなす、既存サンプルとのコーナーの平均距離（画像の幅に対する比率。1280px で約 64px）。</summary>
    public double MinChangeRatio { get; init; } = 0.05;

    /// <summary>採用の最小間隔 [秒]。</summary>
    public double CooldownSeconds { get; init; } = 0.6;

    /// <summary>ステレオで「同時」とみなす 2 台の時刻差 [秒]。</summary>
    public double MaxPairDtSeconds { get; init; } = 0.08;

    public CheckerboardSampleCollector(bool stereo, int targetCount)
    {
        IsStereo = stereo;
        TargetCount = targetCount;
    }

    /// <summary>
    /// 1 フレーム分の検出結果を渡す。
    /// </summary>
    /// <param name="camera">0 = カメラ 1、1 = カメラ 2。</param>
    /// <param name="time">フレームの取得時刻 [秒]。</param>
    /// <param name="corners">検出したコーナー（見つからなければ null）。</param>
    public void Submit(int camera, double time, Vector2[]? corners, int width, int height)
    {
        lock (_lock)
        {
            var c = _cameras[camera];
            c.Width = width;
            c.Height = height;
            if (corners is null)
            {
                c.Last = null;
                c.LastStill = null;
                return;
            }

            var detection = new Detection(time, corners);
            bool still = c.Last is not null
                && c.Last.Corners.Length == corners.Length
                && MeanDistance(c.Last.Corners, corners) < StillRatio * width;
            c.Last = detection;
            if (!still)
            {
                c.LastStill = null;
                return;
            }
            c.LastStill = detection;

            // 各カメラ単体のサンプル（内部パラメータ用）
            if (time - c.LastAcceptTime >= CooldownSeconds && IsNew(c.Samples, corners, width))
            {
                c.Samples.Add(corners);
                c.LastAcceptTime = time;
            }

            // ステレオの組（相手のカメラも同じころに止まったボードを捉えていれば）
            if (IsStereo)
            {
                var other = _cameras[1 - camera].LastStill;
                if (other is not null
                    && System.Math.Abs(other.Time - time) <= MaxPairDtSeconds
                    && time - _lastPairTime >= CooldownSeconds)
                {
                    var c1 = camera == 0 ? corners : other.Corners;
                    var c2 = camera == 0 ? other.Corners : corners;
                    if (IsNew(_pairs.Select(p => p.Camera1), c1, _cameras[0].Width))
                    {
                        _pairs.Add((c1, c2));
                        _lastPairTime = time;
                    }
                }
            }
        }
    }

    /// <summary>カメラ単体のサンプル数。</summary>
    public int SampleCount(int camera)
    {
        lock (_lock) return _cameras[camera].Samples.Count;
    }

    /// <summary>ステレオの組の数。</summary>
    public int PairCount
    {
        get { lock (_lock) return _pairs.Count; }
    }

    /// <summary>目標の数が集まったか。ステレオは組に加えて、両カメラ単体のサンプルも目標数そろったら完了。</summary>
    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                if (!IsStereo) return _cameras[0].Samples.Count >= TargetCount;
                return _pairs.Count >= TargetCount
                    && _cameras[0].Samples.Count >= TargetCount
                    && _cameras[1].Samples.Count >= TargetCount;
            }
        }
    }

    /// <summary>最後に見た画像の大きさ。</summary>
    public (int Width, int Height) ImageSize(int camera)
    {
        lock (_lock) return (_cameras[camera].Width, _cameras[camera].Height);
    }

    public List<Vector2[]> GetSamples(int camera)
    {
        lock (_lock) return [.. _cameras[camera].Samples];
    }

    public List<(Vector2[] Camera1, Vector2[] Camera2)> GetPairs()
    {
        lock (_lock) return [.. _pairs];
    }

    private bool IsNew(IEnumerable<Vector2[]> accepted, Vector2[] corners, int width)
    {
        double min = MinChangeRatio * width;
        foreach (var a in accepted)
        {
            if (a.Length == corners.Length && MeanDistance(a, corners) < min) return false;
        }
        return true;
    }

    private static double MeanDistance(Vector2[] a, Vector2[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Vector2.Distance(a[i], b[i]);
        return sum / a.Length;
    }
}
