using System.Numerics;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// カメラの内部パラメータ（ピンホールモデル、歪みは無視）。
/// カメラ座標は OpenCV と同じ（+X 右、+Y 下、+Z 奥＝カメラの向いている方向、単位 m）。
/// </summary>
public readonly record struct CameraIntrinsics(double Fx, double Fy, double Cx, double Cy, int Width, int Height)
{
    /// <summary>
    /// 水平画角から内部パラメータを作る（キャリブレーションしていないときの近似）。
    /// 一般的な Web カメラの水平画角は 60〜80 度程度。
    /// </summary>
    public static CameraIntrinsics FromHorizontalFov(int width, int height, double horizontalFovDeg)
    {
        double f = (width / 2.0) / System.Math.Tan(horizontalFovDeg * System.Math.PI / 360.0);
        return new CameraIntrinsics(f, f, width / 2.0, height / 2.0, width, height);
    }

    /// <summary>画像上の点 (u, v) を、奥行き z [m] の位置に戻す。</summary>
    public Vector3 Unproject(double u, double v, double z)
        => new((float)((u - Cx) * z / Fx), (float)((v - Cy) * z / Fy), (float)z);

    /// <summary>奥行き z [m] での、画像上の長さ [px] → 実際の長さ [m]。</summary>
    public double PixelsToMeters(double pixels, double z) => pixels * z / Fx;
}

/// <summary>
/// カメラ座標 → トラッキング座標（VRChat に送る座標系）の変換。
///
/// トラッキング座標: +X 右、+Y 上、+Z 前（Unity と同じ左手系）、単位 m。
/// 「カメラの方を向いて立った人」から見た向きにそろえる。つまり
///   +Z = 人からカメラへ向かう方向、+X = その人の右手側。
/// 原点はカメラの位置（VRChat 側は head の位置で全体を合わせるので、原点はどこでもよい）。
/// </summary>
public sealed class CameraToTracking
{
    /// <summary>カメラの下向きの傾き [度]（下を向いていると正）。</summary>
    public double PitchDownDeg { get; set; }

    public Vector3 Transform(Vector3 cam)
    {
        // 1. Y を反転して「+Y 上」のカメラ基準の座標にする（OpenCV の右手系 → Unity の左手系）
        double x = cam.X, y = -cam.Y, z = cam.Z;

        // 2. カメラの傾きを戻す（X 軸まわりの回転）。下向きのカメラの光軸上の点は、水平より下にある
        double t = PitchDownDeg * System.Math.PI / 180.0;
        double c = System.Math.Cos(t), s = System.Math.Sin(t);
        double y1 = y * c - z * s;
        double z1 = y * s + z * c;

        // 3. Y 軸まわりに 180 度回して、「カメラを向いた人」の前後左右にそろえる
        return new Vector3((float)-x, (float)y1, (float)-z1);
    }
}
