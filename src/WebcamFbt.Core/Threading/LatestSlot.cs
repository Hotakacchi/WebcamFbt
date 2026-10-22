namespace WebcamFbt.Core.Threading;

/// <summary>
/// 「常に最新の 1 個だけ」を受け渡すための入れ物。
/// 書き込み側は古い値を上書きし（上書きされた値は破棄される）、読み出し側は取り出した値の所有権を受け取る。
/// カメラ → 推論、推論 → UI のように、処理が追いつかないときは古いデータを捨てたい場面で使う。
/// </summary>
public sealed class LatestSlot<T> : IDisposable where T : class, IDisposable
{
    private T? _item;
    private readonly AutoResetEvent _signal = new(false);
    private bool _disposed;

    /// <summary>新しい値を置く。前の値が未取得なら破棄する。</summary>
    public void Put(T item)
    {
        if (_disposed)
        {
            item.Dispose();
            return;
        }
        var old = Interlocked.Exchange(ref _item, item);
        old?.Dispose();
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // 終了処理と重なった場合。置いた値は破棄する
            TryTake()?.Dispose();
        }
    }

    /// <summary>値があれば取り出す（所有権は呼び出し側へ移る）。</summary>
    public T? TryTake() => Interlocked.Exchange(ref _item, null);

    /// <summary>値が置かれるまで最大 timeout 待ってから取り出す。</summary>
    public T? Take(TimeSpan timeout)
    {
        var item = TryTake();
        if (item != null) return item;
        if (_disposed) return null;
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

    /// <summary>中身を破棄して空にする。</summary>
    public void Clear() => TryTake()?.Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _signal.Set();
        _signal.Dispose();
    }
}
