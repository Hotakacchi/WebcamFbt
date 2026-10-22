using System.Numerics;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// 回転の変換。座標系は Unity / VRChat と同じ（+X 右、+Y 上、+Z 前、左手系）。
///
/// 数値の上では、回転行列の列がそれぞれ「物体のローカル X / Y / Z 軸がワールドでどちらを向くか」になる。
/// System.Numerics の Quaternion も同じ数値規則で扱えるので、Unity と同じ値のまま計算できる。
/// </summary>
public static class Rotation
{
    private const double Rad2Deg = 180.0 / System.Math.PI;
    private const double Deg2Rad = System.Math.PI / 180.0;

    /// <summary>
    /// 物体の右・上・前方向（ワールド座標）から回転を作る。
    /// 3 本が直交していなくても、forward を優先して直交化する。
    /// </summary>
    /// <param name="forward">物体の前方向（+Z になる向き）。</param>
    /// <param name="upHint">おおよその上方向。</param>
    /// <returns>直交化できなければ null（ベクトルが短すぎる・平行など）。</returns>
    public static Quaternion? FromForwardUp(Vector3 forward, Vector3 upHint)
    {
        if (forward.LengthSquared() < 1e-10f || upHint.LengthSquared() < 1e-10f) return null;
        var f = Vector3.Normalize(forward);
        // Unity（左手系）の外積: cross(上, 前) = 右
        var r = Vector3.Cross(Vector3.Normalize(upHint), f);
        if (r.LengthSquared() < 1e-6f) return null;
        r = Vector3.Normalize(r);
        var u = Vector3.Cross(f, r); // cross(前, 右) = 上
        return FromBasis(r, u, f);
    }

    /// <summary>
    /// 物体の右方向と上方向から回転を作る（up を優先して直交化）。
    /// 腰のように「左右の軸」と「背骨の軸」から向きを決めるときに使う。
    /// </summary>
    public static Quaternion? FromRightUp(Vector3 right, Vector3 upHint)
    {
        if (right.LengthSquared() < 1e-10f || upHint.LengthSquared() < 1e-10f) return null;
        var u = Vector3.Normalize(upHint);
        var f = Vector3.Cross(Vector3.Normalize(right), u); // cross(右, 上) = 前
        if (f.LengthSquared() < 1e-6f) return null;
        f = Vector3.Normalize(f);
        var r = Vector3.Cross(u, f);
        return FromBasis(r, u, f);
    }

    /// <summary>直交する 3 軸（右・上・前）から回転を作る。</summary>
    public static Quaternion FromBasis(Vector3 right, Vector3 up, Vector3 forward)
    {
        // System.Numerics は行ベクトル規約なので、軸を「行」に入れる
        var m = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>
    /// 回転を Unity のオイラー角 [度] に変換する。
    /// VRChat は Z → X → Y の順に回転を適用してクォータニオンにする（Unity の Quaternion.Euler と同じ）。
    /// 回転行列で書くと M = Ry(y) · Rx(x) · Rz(z)。
    /// </summary>
    /// <returns>(x, y, z)。x は -90〜90、y と z は -180〜180。</returns>
    public static Vector3 ToUnityEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        var right = Vector3.Transform(Vector3.UnitX, q);
        var up = Vector3.Transform(Vector3.UnitY, q);
        var forward = Vector3.Transform(Vector3.UnitZ, q);

        // M の要素: m12 = -sin(x) = forward.Y, m02 = sin(y)cos(x) = forward.X, m22 = cos(y)cos(x) = forward.Z,
        //           m10 = cos(x)sin(z) = right.Y, m11 = cos(x)cos(z) = up.Y
        double sx = System.Math.Clamp(-forward.Y, -1.0, 1.0);
        double x = System.Math.Asin(sx);
        double y, z;
        if (System.Math.Abs(sx) < 0.99999)
        {
            y = System.Math.Atan2(forward.X, forward.Z);
            z = System.Math.Atan2(right.Y, up.Y);
        }
        else
        {
            // 真上・真下を向いている（ジンバルロック）。z = 0 として y を求める
            y = System.Math.Atan2(-right.Z, right.X);
            z = 0;
        }
        return new Vector3((float)(x * Rad2Deg), (float)(y * Rad2Deg), (float)(z * Rad2Deg));
    }

    /// <summary>Unity のオイラー角 [度] から回転を作る（Quaternion.Euler と同じ）。</summary>
    public static Quaternion FromUnityEuler(Vector3 eulerDeg)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(eulerDeg.X * Deg2Rad));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(eulerDeg.Y * Deg2Rad));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(eulerDeg.Z * Deg2Rad));
        // Ry · Rx · Rz（Z を最初に適用）。System.Numerics の a * b は「b を適用してから a」
        return Quaternion.Normalize(qy * qx * qz);
    }
}
