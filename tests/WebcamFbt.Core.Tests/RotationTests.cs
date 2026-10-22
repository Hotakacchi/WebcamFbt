using System.Numerics;
using WebcamFbt.Core.Geometry;

namespace WebcamFbt.Core.Tests;

public class RotationTests
{
    private const float Tol = 0.05f; // 度

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, 0, 0)]
    [InlineData(0, 45, 0)]
    [InlineData(0, 0, -60)]
    [InlineData(20, -130, 15)]
    [InlineData(-70, 170, -100)]
    [InlineData(45, 90, 45)]
    public void UnityEuler_往復で元に戻る(float x, float y, float z)
    {
        var q = Rotation.FromUnityEuler(new Vector3(x, y, z));
        var e = Rotation.ToUnityEuler(q);
        // 角度表現は複数ありうるので、回転として一致するかで比べる
        var q2 = Rotation.FromUnityEuler(e);
        Assert.True(OneEuroFilterRotation.AngleBetween(q, q2) < 1e-3, $"({x},{y},{z}) → ({e.X},{e.Y},{e.Z})");
        Assert.Equal(x, e.X, Tol);
        Assert.Equal(y, e.Y, Tol);
        Assert.Equal(z, e.Z, Tol);
    }

    [Fact]
    public void UnityEuler_回転順はZXY()
    {
        // Unity の Quaternion.Euler(90, 90, 0) は、前方向 (0,0,1) を真下 (0,-1,0) に向ける
        // （先に X で 90 度 → 下を向き、Y の回転は下向きベクトルを変えない）
        var q = Rotation.FromUnityEuler(new Vector3(90, 90, 0));
        var f = Vector3.Transform(Vector3.UnitZ, q);
        Assert.Equal(0, f.X, 1e-4f);
        Assert.Equal(-1, f.Y, 1e-4f);
        Assert.Equal(0, f.Z, 1e-4f);

        // Euler(0, 90, 0) は前方向を右 (+X) に向ける
        var q2 = Rotation.FromUnityEuler(new Vector3(0, 90, 0));
        var f2 = Vector3.Transform(Vector3.UnitZ, q2);
        Assert.Equal(1, f2.X, 1e-4f);
        Assert.Equal(0, f2.Z, 1e-4f);
    }

    [Fact]
    public void FromForwardUp_右を向くとヨー90度()
    {
        var q = Rotation.FromForwardUp(Vector3.UnitX, Vector3.UnitY)!.Value;
        var e = Rotation.ToUnityEuler(q);
        Assert.Equal(0, e.X, Tol);
        Assert.Equal(90, e.Y, Tol);
        Assert.Equal(0, e.Z, Tol);
    }

    [Fact]
    public void FromRightUp_正面向きは単位回転()
    {
        var q = Rotation.FromRightUp(Vector3.UnitX, Vector3.UnitY)!.Value;
        Assert.True(OneEuroFilterRotation.AngleBetween(q, Quaternion.Identity) < 1e-4);
    }

    [Fact]
    public void FromForwardUp_平行なら失敗する()
    {
        Assert.Null(Rotation.FromForwardUp(Vector3.UnitY, Vector3.UnitY));
    }
}
