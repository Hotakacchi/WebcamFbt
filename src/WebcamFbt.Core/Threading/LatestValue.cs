namespace WebcamFbt.Core.Threading;

/// <summary>
/// 「常に最新の 1 個だけ」を受け渡す入れ物（破棄が不要な値用）。
/// 読み出し側は新しい値が置かれるまで待てる。
/// </summary>
public sealed class LatestValue<T> : IDisposable where T : class
{
    private T? _item;
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _disposed;

    public void Put(T item)
    {
        if (_disposed) return;
        Volatile.Write(ref _item, item);
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>新しい値があれば取り出す（取り出した後は空になる）。</summary>
    public T? TryTake() => Interlocked.Exchange(ref _item, null);

    /// <summary>新しい値が置かれるまで最大 timeout 待って取り出す。</summary>
    public T? Take(TimeSpan timeout)
    {
        var item = TryTake();
        if (item != null || _disposed) return item;
        try
        {
            _signal.WaitOne(timeout);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        return TryTake();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signal.Set();
        _signal.Dispose();
    }
}
