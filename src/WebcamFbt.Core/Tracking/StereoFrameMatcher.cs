using System.Diagnostics;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>1 台のカメラの姿勢推定結果（ステレオの対応付け用）。</summary>
public readonly record struct StereoView(Pose2D? Pose, long CapturedTimestamp, int ImageWidth, int ImageHeight);

/// <summary>
/// 2 台のカメラの姿勢推定結果を、撮影時刻が近いもの同士で組にする。
/// カメラは同期していないので、各カメラの最新の結果を持っておき、相手の最新と時刻が十分近ければ組にする。
/// 組にした結果は捨てる（同じフレームを 2 回使わない）。時刻が離れすぎた組は、動いている途中の
/// 別の瞬間を三角測量することになるので作らない。
/// カメラごとの推論スレッドから呼ぶのでロックで守る。
/// </summary>
public sealed class StereoFrameMatcher
{
    private readonly object _lock = new();
    private readonly StereoView?[] _latest = new StereoView?[2];

    /// <summary>組にする撮影時刻の差の上限 [秒]。60fps なら常に満たし、30fps では位相次第で落ちる組がある。</summary>
    public double MaxDtSeconds { get; init; } = 0.025;

    /// <summary>
    /// 結果を渡す。相手のカメラの最新の結果と組になれば true。
    /// </summary>
    /// <param name="camera">0 = カメラ 1、1 = カメラ 2。</param>
    public bool TrySubmit(int camera, StereoView view, out StereoView view1, out StereoView view2)
    {
        lock (_lock)
        {
            _latest[camera] = view;
            var other = _latest[1 - camera];
            if (other is { } o && System.Math.Abs(o.CapturedTimestamp - view.CapturedTimestamp) / (double)Stopwatch.Frequency <= MaxDtSeconds)
            {
                view1 = camera == 0 ? view : o;
                view2 = camera == 0 ? o : view;
                _latest[0] = null;
                _latest[1] = null;
                return true;
            }
            view1 = default;
            view2 = default;
            return false;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _latest[0] = null;
            _latest[1] = null;
        }
    }
}
