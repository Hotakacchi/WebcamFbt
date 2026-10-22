using System.Diagnostics;

namespace WebcamFbt.Core.Diagnostics;

/// <summary>
/// 直近 1 秒あたりの回数（fps など）を数える。スレッドセーフ。
/// </summary>
public sealed class RateCounter
{
    private readonly Queue<long> _ticks = new();
    private readonly object _lock = new();
    private static readonly long Window = Stopwatch.Frequency; // 1 秒

    /// <summary>1 回分を記録する。</summary>
    public void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _ticks.Enqueue(now);
            Trim(now);
        }
    }

    /// <summary>直近 1 秒間の回数。</summary>
    public double Rate
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            lock (_lock)
            {
                Trim(now);
                return _ticks.Count;
            }
        }
    }

    public void Reset()
    {
        lock (_lock) _ticks.Clear();
    }

    private void Trim(long now)
    {
        while (_ticks.Count > 0 && now - _ticks.Peek() > Window)
        {
            _ticks.Dequeue();
        }
    }
}

/// <summary>
/// 値の指数移動平均（推論時間などの表示用）。スレッドセーフ。
/// </summary>
public sealed class MovingAverage(double alpha = 0.1)
{
    private double _value = double.NaN;
    private readonly object _lock = new();

    public void Add(double sample)
    {
        lock (_lock)
        {
            _value = double.IsNaN(_value) ? sample : _value + alpha * (sample - _value);
        }
    }

    public double Value
    {
        get { lock (_lock) return double.IsNaN(_value) ? 0 : _value; }
    }

    public void Reset()
    {
        lock (_lock) _value = double.NaN;
    }
}

/// <summary>Stopwatch のタイムスタンプ同士の差をミリ秒に変換する補助。</summary>
public static class TimeUtil
{
    public static double ElapsedMs(long fromTimestamp, long toTimestamp)
        => (toTimestamp - fromTimestamp) * 1000.0 / Stopwatch.Frequency;

    public static double ElapsedMsSince(long fromTimestamp)
        => ElapsedMs(fromTimestamp, Stopwatch.GetTimestamp());
}
