using System.Numerics;
using OpenCvSharp;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Vision.Calibration;

namespace WebcamFbt.Core.Tests;

/// <summary>カメラの校正中の案内に使う情報と、印刷用のボードの画像。</summary>
public class CalibrationGuidanceTests
{
    private static Vector2[] Board()
    {
        var pts = new Vector2[54];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(100 + i % 9 * 30, 100 + i / 9 * 30);
        return pts;
    }

    [Fact]
    public void ボードが見えないフレームを数え_見えたら戻す()
    {
        var c = new CheckerboardSampleCollector(stereo: false, targetCount: 15);
        Assert.False(c.IsBoardVisible(0));
        for (int i = 0; i < 5; i++) c.Submit(0, i * 0.1, null, 1280, 720);
        Assert.Equal(5, c.FramesWithoutBoard(0));
        Assert.False(c.IsBoardVisible(0));

        c.Submit(0, 0.6, Board(), 1280, 720);
        Assert.Equal(0, c.FramesWithoutBoard(0));
        Assert.True(c.IsBoardVisible(0));

        c.Submit(0, 0.7, null, 1280, 720);
        Assert.Equal(1, c.FramesWithoutBoard(0));
    }

    [Theory]
    [InlineData(9, 6)]
    [InlineData(7, 5)]
    public void 印刷用のボードはアプリ自身が検出できる(int cols, int rows)
    {
        using var gray = CheckerboardImage.Create(cols, rows, squarePixels: 60);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);

        var corners = CheckerboardDetector.Detect(bgr, new CheckerboardSpec(cols, rows, 0.025));

        Assert.NotNull(corners);
        Assert.Equal(cols * rows, corners!.Length);
        // 最初の内側の交点は、余白（1 マス）+ 1 マスの位置
        Assert.Equal(120, corners[0].X, 1.5f);
        Assert.Equal(120, corners[0].Y, 1.5f);
    }

    [Fact]
    public void 交点の数が違うボードは検出しない()
    {
        // 9×6 用に印刷したボードを 8×6 の設定で探しても見つからない（案内で「交点の数を確かめて」と出す理由）
        using var gray = CheckerboardImage.Create(9, 6, squarePixels: 60);
        using var bgr = new Mat();
        Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        Assert.Null(CheckerboardDetector.Detect(bgr, new CheckerboardSpec(8, 6, 0.025)));
    }
}
