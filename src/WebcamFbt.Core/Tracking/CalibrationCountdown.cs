namespace WebcamFbt.Core.Tracking;

/// <summary>体のキャリブレーション開始までの待ち時間。呼び出し側が単調増加する時刻を渡す。</summary>
public sealed class CalibrationCountdown
{
    private double? _startAt;
    public bool IsWaiting => _startAt.HasValue;
    public void Start(double nowSeconds) => _startAt = nowSeconds + 5;
    public void Cancel() => _startAt = null;

    /// <summary>残り秒数。準備時間中のみ1以上で、時間到達時に一度だけ0を返す。</summary>
    public int? Poll(double nowSeconds)
    {
        if (_startAt is not { } target) return null;
        double remaining = target - nowSeconds;
        if (remaining > 0) return (int)Math.Ceiling(remaining);
        _startAt = null;
        return 0;
    }
}
