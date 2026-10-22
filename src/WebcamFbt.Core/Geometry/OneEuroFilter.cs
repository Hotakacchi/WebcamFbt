using System.Numerics;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// One Euro Filter のパラメータ。UI から実行中に変更でき、それを使うフィルタすべてに即反映される。
/// 参考: Casiez et al., "1€ Filter: A Simple Speed-based Low-pass Filter for Noisy Input in Interactive Systems" (CHI 2012)
/// </summary>
public sealed class OneEuroParams
{
    /// <summary>
    /// 最小カットオフ周波数 [Hz]。小さいほど、止まっているときのブレが減る（その代わり遅れる）。
    /// </summary>
    public double MinCutoff { get; set; }

    /// <summary>
    /// 速度係数。大きいほど、速く動いたときの遅れが減る（その代わりブレやすい）。
    /// </summary>
    public double Beta { get; set; }

    /// <summary>速度の推定に使うカットオフ周波数 [Hz]。通常は 1 のままでよい。</summary>
    public double DCutoff { get; set; }

    public OneEuroParams(double minCutoff, double beta, double dCutoff = 1.0)
    {
        MinCutoff = minCutoff;
        Beta = beta;
        DCutoff = dCutoff;
    }

    /// <summary>平滑化係数 α（0〜1、大きいほど新しい値をそのまま使う）。</summary>
    internal static double Alpha(double dt, double cutoff)
    {
        double tau = 1.0 / (2.0 * System.Math.PI * System.Math.Max(cutoff, 1e-6));
        return 1.0 / (1.0 + tau / dt);
    }
}

/// <summary>スカラー値の One Euro Filter。</summary>
public sealed class OneEuroFilter(OneEuroParams parameters)
{
    private bool _initialized;
    private double _x;
    private double _dx;
    private double _t;

    public OneEuroParams Parameters { get; } = parameters;

    /// <param name="value">新しい値。</param>
    /// <param name="time">時刻 [秒]。単調増加であること。</param>
    public double Filter(double value, double time)
    {
        if (!_initialized)
        {
            _initialized = true;
            _x = value;
            _dx = 0;
            _t = time;
            return value;
        }

        double dt = time - _t;
        if (dt <= 1e-6) return _x;
        _t = time;

        var p = Parameters;
        double dx = (value - _x) / dt;
        _dx += OneEuroParams.Alpha(dt, p.DCutoff) * (dx - _dx);
        double cutoff = p.MinCutoff + p.Beta * System.Math.Abs(_dx);
        _x += OneEuroParams.Alpha(dt, cutoff) * (value - _x);
        return _x;
    }

    public void Reset() => _initialized = false;
}

/// <summary>
/// 3 次元ベクトルの One Euro Filter。速さ（ベクトルの大きさ）でカットオフを決めるので、
/// 軸ごとに別々にかけるより動きが自然になる。
/// </summary>
public sealed class OneEuroFilterVector3(OneEuroParams parameters)
{
    private bool _initialized;
    private Vector3 _x;
    private Vector3 _dx;
    private double _t;

    public OneEuroParams Parameters { get; } = parameters;

    public Vector3 Filter(Vector3 value, double time)
    {
        if (!_initialized)
        {
            _initialized = true;
            _x = value;
            _dx = Vector3.Zero;
            _t = time;
            return value;
        }

        double dt = time - _t;
        if (dt <= 1e-6) return _x;
        _t = time;

        var p = Parameters;
        var dx = (value - _x) / (float)dt;
        _dx += (float)OneEuroParams.Alpha(dt, p.DCutoff) * (dx - _dx);
        double cutoff = p.MinCutoff + p.Beta * _dx.Length();
        _x += (float)OneEuroParams.Alpha(dt, cutoff) * (value - _x);
        return _x;
    }

    public void Reset() => _initialized = false;
}

/// <summary>
/// 回転（クォータニオン）の One Euro Filter。角速度 [rad/s] でカットオフを決め、球面線形補間で平滑化する。
/// </summary>
public sealed class OneEuroFilterRotation(OneEuroParams parameters)
{
    private bool _initialized;
    private Quaternion _x;
    private double _speed;
    private double _t;

    public OneEuroParams Parameters { get; } = parameters;

    public Quaternion Filter(Quaternion value, double time)
    {
        value = Quaternion.Normalize(value);
        if (!_initialized)
        {
            _initialized = true;
            _x = value;
            _speed = 0;
            _t = time;
            return value;
        }

        double dt = time - _t;
        if (dt <= 1e-6) return _x;
        _t = time;

        // q と -q は同じ回転なので、近い方に揃える
        if (Quaternion.Dot(_x, value) < 0) value = Quaternion.Negate(value);

        var p = Parameters;
        double angle = AngleBetween(_x, value);
        _speed += OneEuroParams.Alpha(dt, p.DCutoff) * (angle / dt - _speed);
        double cutoff = p.MinCutoff + p.Beta * System.Math.Abs(_speed);
        _x = Quaternion.Normalize(Quaternion.Slerp(_x, value, (float)OneEuroParams.Alpha(dt, cutoff)));
        return _x;
    }

    public void Reset() => _initialized = false;

    /// <summary>2 つの回転の間の角度 [rad]。</summary>
    public static double AngleBetween(Quaternion a, Quaternion b)
    {
        double dot = System.Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)));
        return 2.0 * System.Math.Acos(System.Math.Min(1.0, dot));
    }
}
