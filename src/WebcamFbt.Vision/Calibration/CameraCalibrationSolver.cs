using System.Numerics;
using OpenCvSharp;
using WebcamFbt.Core.Geometry;

namespace WebcamFbt.Vision.Calibration;

/// <summary>
/// 集めたチェッカーボードのサンプルから、OpenCV で内部パラメータとステレオの相対姿勢を求める。
/// 計算に 1 秒前後かかることがあるので UI スレッドからは Task.Run で呼ぶ。
/// </summary>
public static class CameraCalibrationSolver
{
    /// <summary>内部パラメータの推定に最低限必要なサンプル数。</summary>
    public const int MinSamples = 6;

    /// <summary>1 台のカメラの内部パラメータ（焦点距離・中心・歪み）を求める。</summary>
    public static CameraCalibration CalibrateMono(IReadOnlyList<Vector2[]> samples, CheckerboardSpec spec, int width, int height)
    {
        if (samples.Count < MinSamples)
        {
            throw new InvalidOperationException($"サンプルが足りません（{samples.Count} / 最低 {MinSamples}）。");
        }

        var obj = spec.ObjectPoints();
        var objectPoints = samples.Select(_ => (IEnumerable<Point3f>)obj).ToList();
        var imagePoints = samples.Select(s => (IEnumerable<Point2f>)ToPoints(s)).ToList();
        var k = new double[3, 3];
        var dist = new double[5];
        double rms = Cv2.CalibrateCamera(objectPoints, imagePoints, new Size(width, height), k, dist,
            out _, out _, CalibrationFlags.None, null);
        return new CameraCalibration(width, height, k[0, 0], k[1, 1], k[0, 2], k[1, 2], dist, rms, samples.Count);
    }

    /// <summary>
    /// 2 台の相対姿勢（カメラ 1 → カメラ 2 の R, T）を求める。
    /// 内部パラメータは各カメラ単体で求めたものを固定して使う（組のサンプルだけで同時に推定するより安定するため）。
    /// </summary>
    public static StereoCalibration CalibrateStereo(
        IReadOnlyList<(Vector2[] Camera1, Vector2[] Camera2)> pairs,
        CameraCalibration camera1,
        CameraCalibration camera2,
        CheckerboardSpec spec,
        int device1,
        int device2)
    {
        if (pairs.Count < MinSamples)
        {
            throw new InvalidOperationException($"同時撮影の組が足りません（{pairs.Count} / 最低 {MinSamples}）。");
        }

        var obj = spec.ObjectPoints();
        var objectPoints = pairs.Select(_ => (IEnumerable<Point3f>)obj).ToList();
        var image1 = pairs.Select(p => (IEnumerable<Point2f>)ToPoints(p.Camera1)).ToList();
        var image2 = pairs.Select(p => (IEnumerable<Point2f>)ToPoints(p.Camera2)).ToList();

        using var r = new Mat();
        using var t = new Mat();
        using var e = new Mat();
        using var f = new Mat();
        double rms = Cv2.StereoCalibrate(objectPoints, image1, image2,
            ToMatrix(camera1), (double[])camera1.Distortion.Clone(),
            ToMatrix(camera2), (double[])camera2.Distortion.Clone(),
            new Size(camera1.Width, camera1.Height), r, t, e, f, CalibrationFlags.FixIntrinsic, null);

        var rotation = new double[9];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) rotation[i * 3 + j] = r.At<double>(i, j);
        }
        var translation = new[] { t.At<double>(0, 0), t.At<double>(1, 0), t.At<double>(2, 0) };
        return new StereoCalibration(device1, device2, camera1, camera2, rotation, translation, rms, pairs.Count);
    }

    private static Point2f[] ToPoints(Vector2[] corners)
    {
        var points = new Point2f[corners.Length];
        for (int i = 0; i < corners.Length; i++) points[i] = new Point2f(corners[i].X, corners[i].Y);
        return points;
    }

    private static double[,] ToMatrix(CameraCalibration c) => new double[,]
    {
        { c.Fx, 0, c.Cx },
        { 0, c.Fy, c.Cy },
        { 0, 0, 1 },
    };
}
