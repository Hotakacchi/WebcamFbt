using System.Numerics;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// 1 台のカメラの内部パラメータ（チェッカーボード校正の結果）。
/// 歪み係数の並びは OpenCV と同じ (k1, k2, p1, p2, k3)。
/// 校正したときの解像度でしか使えない（解像度が変わると焦点距離・中心がずれる）。
/// </summary>
/// <param name="ReprojErrorPx">再投影誤差の RMS [px]。おおむね 0.5 以下なら良好。</param>
/// <param name="SampleCount">校正に使ったチェッカーボードの枚数。</param>
public sealed record CameraCalibration(
    int Width,
    int Height,
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    double[] Distortion,
    double ReprojErrorPx,
    int SampleCount)
{
    /// <summary>歪みを無視したピンホールモデル。</summary>
    public CameraIntrinsics ToIntrinsics() => new(Fx, Fy, Cx, Cy, Width, Height);

    /// <summary>この解像度の画像に使えるか。</summary>
    public bool Matches(int width, int height) => width == Width && height == Height;

    /// <summary>歪みのない画像上の点 → 実際に写る（歪んだ）画像上の点。</summary>
    public Vector2 Distort(Vector2 p)
    {
        double x = (p.X - Cx) / Fx, y = (p.Y - Cy) / Fy;
        double r2 = x * x + y * y;
        double radial = Radial(r2);
        var (dx, dy) = Tangential(x, y, r2);
        return new Vector2((float)((x * radial + dx) * Fx + Cx), (float)((y * radial + dy) * Fy + Cy));
    }

    /// <summary>
    /// 実際に写った（歪んだ）画像上の点 → 歪みのない画像上の点。
    /// 解析的には解けないので、OpenCV の undistortPoints と同じく反復で逆算する。
    /// </summary>
    public Vector2 Undistort(Vector2 p)
    {
        double xd = (p.X - Cx) / Fx, yd = (p.Y - Cy) / Fy;
        double x = xd, y = yd;
        for (int i = 0; i < 20; i++)
        {
            double r2 = x * x + y * y;
            var (dx, dy) = Tangential(x, y, r2);
            double radial = Radial(r2);
            x = (xd - dx) / radial;
            y = (yd - dy) / radial;
        }
        return new Vector2((float)(x * Fx + Cx), (float)(y * Fy + Cy));
    }

    private double Radial(double r2) => 1 + r2 * (K(0) + r2 * (K(1) + r2 * K(4)));

    private (double Dx, double Dy) Tangential(double x, double y, double r2)
    {
        double p1 = K(2), p2 = K(3);
        return (2 * p1 * x * y + p2 * (r2 + 2 * x * x), p1 * (r2 + 2 * y * y) + 2 * p2 * x * y);
    }

    private double K(int i) => i < Distortion.Length ? Distortion[i] : 0;
}

/// <summary>
/// カメラ 2 台の校正結果。カメラ 1 を基準（世界原点）とする。
/// R, T は「カメラ 1 の座標 → カメラ 2 の座標」の変換で、X2 = R·X1 + T（OpenCV の stereoCalibrate と同じ）。
/// </summary>
/// <param name="Device1">カメラ 1 のデバイス番号（校正したときのもの）。</param>
/// <param name="Device2">カメラ 2 のデバイス番号。</param>
/// <param name="R">回転行列（3×3、行優先で 9 要素）。</param>
/// <param name="T">平行移動 [m]（3 要素）。</param>
/// <param name="ReprojErrorPx">再投影誤差の RMS [px]。</param>
/// <param name="PairCount">校正に使った同時撮影の組の数。</param>
public sealed record StereoCalibration(
    int Device1,
    int Device2,
    CameraCalibration Camera1,
    CameraCalibration Camera2,
    double[] R,
    double[] T,
    double ReprojErrorPx,
    int PairCount)
{
    /// <summary>2 台のカメラの間の距離（基線長）[m]。</summary>
    public double BaselineMeters => System.Math.Sqrt(T[0] * T[0] + T[1] * T[1] + T[2] * T[2]);
}
