using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 信頼度の低いキーポイントを除外する。
/// 信頼度がしきい値未満になったら、直前の信頼できる位置を一定時間だけ使い続け（保持）、
/// それを過ぎたら「無効」にする。一瞬隠れただけでトラッカーが飛ぶのを防ぐ。
/// </summary>
public sealed class KeypointGate
{
    private (float X, float Y)[] _last = [];
    private double[] _lastTime = [];

    /// <summary>ゲートを通した結果。</summary>
    public readonly record struct GatedPoint(float X, float Y, bool Valid, bool Held);

    /// <param name="pose">今回の推定結果。人がいなければ null。</param>
    /// <param name="time">時刻 [秒]。</param>
    /// <param name="threshold">信頼度のしきい値。</param>
    /// <param name="holdSeconds">直前の値を使い続ける最大時間。</param>
    public GatedPoint[] Apply(Pose2D? pose, int keypointCount, double time, float threshold, double holdSeconds)
    {
        if (_last.Length != keypointCount)
        {
            _last = new (float, float)[keypointCount];
            _lastTime = Enumerable.Repeat(double.NegativeInfinity, keypointCount).ToArray();
        }

        var result = new GatedPoint[keypointCount];
        for (int i = 0; i < keypointCount; i++)
        {
            if (pose != null && pose.Keypoints[i].Score >= threshold)
            {
                var k = pose.Keypoints[i];
                _last[i] = (k.X, k.Y);
                _lastTime[i] = time;
                result[i] = new GatedPoint(k.X, k.Y, true, false);
            }
            else if (time - _lastTime[i] <= holdSeconds)
            {
                result[i] = new GatedPoint(_last[i].X, _last[i].Y, true, true);
            }
            else
            {
                result[i] = default;
            }
        }
        return result;
    }

    public void Reset()
    {
        _last = [];
        _lastTime = [];
    }
}
