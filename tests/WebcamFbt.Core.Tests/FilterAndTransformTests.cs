using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Osc;

namespace WebcamFbt.Core.Tests;

public class OneEuroFilterTests
{
    [Fact]
    public void 一定の値には収束する()
    {
        var f = new OneEuroFilter(new OneEuroParams(1.0, 0.5));
        double y = 0;
        for (int i = 0; i < 300; i++) y = f.Filter(i == 0 ? 0 : 1, i / 60.0);
        Assert.Equal(1.0, y, 3);
    }

    [Fact]
    public void ノイズを減らす()
    {
        var f = new OneEuroFilter(new OneEuroParams(1.0, 0.0));
        var rng = new Random(1);
        double sumIn = 0, sumOut = 0;
        for (int i = 0; i < 600; i++)
        {
            double noisy = (rng.NextDouble() - 0.5) * 0.1;
            double y = f.Filter(noisy, i / 60.0);
            if (i > 60)
            {
                sumIn += noisy * noisy;
                sumOut += y * y;
            }
        }
        Assert.True(sumOut < sumIn * 0.3);
    }

    [Fact]
    public void 回転フィルタは目標に近づく()
    {
        var f = new OneEuroFilterRotation(new OneEuroParams(1.0, 0.3));
        var target = Rotation.FromUnityEuler(new Vector3(0, 90, 0));
        f.Filter(Quaternion.Identity, 0);
        Quaternion q = Quaternion.Identity;
        for (int i = 1; i < 300; i++) q = f.Filter(target, i / 60.0);
        Assert.True(OneEuroFilterRotation.AngleBetween(q, target) < 0.01);
    }
}

public class CameraTransformTests
{
    [Fact]
    public void カメラ正面の点は_トラッキング座標では後ろ()
    {
        // カメラから 2m 先の点 → 人（カメラを向いている）から見て、カメラは +Z 側にあるので、点は -Z
        var t = new CameraToTracking();
        var p = t.Transform(new Vector3(0, 0, 2));
        Assert.Equal(0, p.X, 1e-5f);
        Assert.Equal(0, p.Y, 1e-5f);
        Assert.Equal(-2, p.Z, 1e-5f);
    }

    [Fact]
    public void 画像の右と下は_トラッキングの左と下()
    {
        var t = new CameraToTracking();
        var p = t.Transform(new Vector3(1, 1, 2)); // 画像の右下
        Assert.Equal(-1, p.X, 1e-5f); // カメラを向いた人の左手側
        Assert.Equal(-1, p.Y, 1e-5f); // 下
    }

    [Fact]
    public void 下向きのカメラの光軸上の点は水平より下()
    {
        var t = new CameraToTracking { PitchDownDeg = 30 };
        var p = t.Transform(new Vector3(0, 0, 2));
        Assert.Equal(-1.0, p.Y, 1e-4f); // 2 × sin30°
    }

    [Fact]
    public void 画角から焦点距離を求める()
    {
        var k = CameraIntrinsics.FromHorizontalFov(1280, 720, 90);
        Assert.Equal(640, k.Fx, 1e-6);
        var p = k.Unproject(1280, 360, 2.0); // 右端 → 45 度
        Assert.Equal(2.0, p.X, 1e-5f);
    }
}

public class OscMessageTests
{
    [Fact]
    public void Floatを3つ持つメッセージの形式()
    {
        var bytes = OscMessage.Encode("/a", 1f, 2f, -1f);
        byte[] expected =
        [
            (byte)'/', (byte)'a', 0, 0,                   // アドレス（4 バイト境界）
            (byte)',', (byte)'f', (byte)'f', (byte)'f', 0, 0, 0, 0, // 型タグ（",fff" + NUL → 8 バイト）
            0x3F, 0x80, 0x00, 0x00,                       // 1.0f（ビッグエンディアン）
            0x40, 0x00, 0x00, 0x00,                       // 2.0f
            0xBF, 0x80, 0x00, 0x00,                       // -1.0f
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void アドレス長が4の倍数ならNULで4バイト足す()
    {
        var bytes = OscMessage.Encode("/abc", 0f);
        Assert.Equal(8 + 4 + 4, bytes.Length);
        Assert.Equal(0, bytes[4]);
    }

    [Fact]
    public void VRChatのトラッカーアドレス()
    {
        var bytes = OscMessage.Encode("/tracking/trackers/1/position", 0f, 1f, 0f);
        // "/tracking/trackers/1/position" は 29 文字 → NUL 込みで 32 バイト
        Assert.Equal(32 + 8 + 12, bytes.Length);
    }
}
