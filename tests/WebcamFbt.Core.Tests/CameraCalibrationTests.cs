using System.Numerics;
using OpenCvSharp;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Vision.Calibration;

namespace WebcamFbt.Core.Tests;

public class LensDistortionTests
{
    private static CameraCalibration Cal() => new(1280, 720, 900, 905, 645, 355,
        [-0.25, 0.08, 0.001, -0.0015, -0.01], 0, 0);

    [Fact]
    public void 歪み補正は歪みの逆()
    {
        var cal = Cal();
        foreach (var p in new[] { new Vector2(645, 355), new Vector2(100, 80), new Vector2(1200, 650), new Vector2(300, 600) })
        {
            var back = cal.Undistort(cal.Distort(p));
            Assert.Equal(p.X, back.X, 0.01f);
            Assert.Equal(p.Y, back.Y, 0.01f);
        }
    }

    [Fact]
    public void 歪みがなければ点は動かない()
    {
        var cal = Cal() with { Distortion = [0, 0, 0, 0, 0] };
        var p = new Vector2(123, 456);
        Assert.Equal(p.X, cal.Distort(p).X, 1e-3f);
        Assert.Equal(p.Y, cal.Distort(p).Y, 1e-3f);
        Assert.Equal(p.X, cal.Undistort(p).X, 1e-3f);
        Assert.Equal(p.Y, cal.Undistort(p).Y, 1e-3f);
    }
}

public class CameraCalibrationStoreTests
{
    private static CameraCalibration Cal(int w = 1280, int h = 720) => new(w, h, 900, 900, 640, 360, [0.1, -0.02, 0, 0, 0], 0.3, 15);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"webcamfbt-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void 保存した内部パラメータを読み戻せる()
    {
        var path = TempFile();
        try
        {
            new CameraCalibrationStore(path).SaveCamera(2, Cal());
            var loaded = new CameraCalibrationStore(path).GetCamera(2, 1280, 720);
            Assert.NotNull(loaded);
            Assert.Equal(900, loaded!.Fx);
            Assert.Equal(new[] { 0.1, -0.02, 0, 0, 0 }, loaded.Distortion);

            // デバイスや解像度が違えば別物
            Assert.Null(new CameraCalibrationStore(path).GetCamera(1, 1280, 720));
            Assert.Null(new CameraCalibrationStore(path).GetCamera(2, 640, 480));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ステレオはデバイスの組が合うときだけ返す()
    {
        var path = TempFile();
        try
        {
            var stereo = new StereoCalibration(0, 1, Cal(), Cal(), [1, 0, 0, 0, 1, 0, 0, 0, 1], [-0.6, 0, 0], 0.4, 15);
            var store = new CameraCalibrationStore(path);
            store.SaveStereo(stereo);
            Assert.Equal(0.6, store.GetStereo(0, 1)!.BaselineMeters, 6);
            Assert.Null(store.GetStereo(1, 0));
            Assert.NotNull(store.GetCamera(1, 1280, 720)); // 2 台の内部パラメータも保存される
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 壊れたファイルは無視する()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ 壊れている");
            var store = new CameraCalibrationStore(path);
            Assert.Null(store.GetCamera(0, 1280, 720));
            store.SaveCamera(0, Cal()); // 上書きして直る
            Assert.NotNull(store.GetCamera(0, 1280, 720));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CheckerboardSampleCollectorTests
{
    private const int W = 1280, H = 720;

    private static Vector2[] Board(float ox, float oy)
    {
        var pts = new Vector2[54];
        for (int r = 0; r < 6; r++)
            for (int c = 0; c < 9; c++)
                pts[r * 9 + c] = new Vector2(ox + c * 30, oy + r * 30);
        return pts;
    }

    [Fact]
    public void 止まったボードを採り_同じ位置や短い間隔では採らない()
    {
        var c = new CheckerboardSampleCollector(stereo: false, targetCount: 3);
        var a = Board(100, 100);
        c.Submit(0, 0.0, a, W, H);   // 最初の 1 枚は静止か判定できない
        Assert.Equal(0, c.SampleCount(0));
        c.Submit(0, 0.1, a, W, H);   // 止まっている → 採用
        Assert.Equal(1, c.SampleCount(0));
        c.Submit(0, 0.2, a, W, H);   // 前回から 0.6 秒たっていない
        c.Submit(0, 1.0, a, W, H);   // 時間はたったが同じ位置
        Assert.Equal(1, c.SampleCount(0));

        var b = Board(400, 300);
        c.Submit(0, 1.1, b, W, H);   // 動いた直後
        Assert.Equal(1, c.SampleCount(0));
        c.Submit(0, 1.2, b, W, H);   // 新しい位置で止まった → 採用
        Assert.Equal(2, c.SampleCount(0));
        Assert.False(c.IsComplete);
    }

    [Fact]
    public void 動き続けるボードは採らない()
    {
        var c = new CheckerboardSampleCollector(stereo: false, targetCount: 3);
        for (int i = 0; i < 30; i++) c.Submit(0, i * 0.1, Board(100 + i * 20, 100), W, H);
        Assert.Equal(0, c.SampleCount(0));
    }

    [Fact]
    public void 見失うと静止判定はやり直し()
    {
        var c = new CheckerboardSampleCollector(stereo: false, targetCount: 3);
        var a = Board(100, 100);
        c.Submit(0, 0.0, a, W, H);
        c.Submit(0, 0.1, null, W, H);
        c.Submit(0, 0.2, a, W, H);
        Assert.Equal(0, c.SampleCount(0));
    }

    [Fact]
    public void ステレオは2台がほぼ同時に止まったときだけ組にする()
    {
        var c = new CheckerboardSampleCollector(stereo: true, targetCount: 1);
        var a1 = Board(100, 100);
        var a2 = Board(300, 120);
        c.Submit(0, 0.00, a1, W, H);
        c.Submit(0, 0.10, a1, W, H);
        c.Submit(1, 0.50, a2, W, H);
        c.Submit(1, 0.60, a2, W, H); // カメラ 1 の静止は 0.10 秒 → 0.5 秒ずれているので組にしない
        Assert.Equal(0, c.PairCount);

        c.Submit(0, 0.62, a1, W, H); // カメラ 1 も 0.62 秒に静止 → カメラ 2 の 0.60 秒と組
        Assert.Equal(1, c.PairCount);
        var pair = c.GetPairs()[0];
        Assert.Equal(a1[0], pair.Camera1[0]);
        Assert.Equal(a2[0], pair.Camera2[0]);
        Assert.True(c.IsComplete);
    }
}

/// <summary>
/// OpenCV による校正計算を合成データで検算する（2 台目のカメラがなくてもステレオの計算を確かめられる）。
/// 既知のカメラでチェッカーボードを写した画像座標を作り、校正で元のパラメータが戻るかを見る。
/// </summary>
public class CameraCalibrationSolverTests
{
    private static readonly CheckerboardSpec Spec = new(9, 6, 0.04);

    // 真のカメラ（歪みあり）。合成には Core の CameraCalibration.Distort を使うので、
    // OpenCV の推定結果と一致すれば Core の歪みモデルが OpenCV と同じであることの確認にもなる
    private static readonly CameraCalibration TrueCamera = new(1280, 720, 900, 900, 640, 360,
        [-0.15, 0.05, 0, 0, 0], 0, 0);

    private static Vector3[] BoardPointsCentered()
    {
        var obj = Spec.ObjectPoints();
        var center = new Vector3((Spec.InnerCols - 1) * 0.04f / 2, (Spec.InnerRows - 1) * 0.04f / 2, 0);
        return obj.Select(p => new Vector3(p.X, p.Y, p.Z) - center).ToArray();
    }

    /// <summary>ボードの姿勢をいろいろ変えて、カメラ 1 座標でのコーナーの 3D 位置を作る。</summary>
    private static List<Vector3[]> BoardPoses(int count)
    {
        var rng = new Random(42);
        var basePoints = BoardPointsCentered();
        var poses = new List<Vector3[]>();
        for (int i = 0; i < count; i++)
        {
            float D(double deg) => (float)(deg * Math.PI / 180);
            var rot = Matrix4x4.CreateRotationX(D(rng.NextDouble() * 50 - 25))
                    * Matrix4x4.CreateRotationY(D(rng.NextDouble() * 50 - 25))
                    * Matrix4x4.CreateRotationZ(D(rng.NextDouble() * 30 - 15));
            var t = new Vector3((float)(rng.NextDouble() * 0.5 - 0.25), (float)(rng.NextDouble() * 0.3 - 0.15),
                (float)(0.8 + rng.NextDouble() * 0.6));
            poses.Add(basePoints.Select(p => Vector3.Transform(p, rot) + t).ToArray());
        }
        return poses;
    }

    private static Vector2[] Project(Vector3[] pointsInCamera, CameraCalibration cam)
        => pointsInCamera.Select(p => cam.Distort(new Vector2(
            (float)(cam.Fx * p.X / p.Z + cam.Cx), (float)(cam.Fy * p.Y / p.Z + cam.Cy)))).ToArray();

    [Fact]
    public void 内部パラメータと歪みが戻る()
    {
        var samples = BoardPoses(15).Select(p => Project(p, TrueCamera)).ToList();
        var cal = CameraCalibrationSolver.CalibrateMono(samples, Spec, 1280, 720);

        Assert.InRange(cal.Fx, 897, 903);
        Assert.InRange(cal.Fy, 897, 903);
        Assert.InRange(cal.Cx, 637, 643);
        Assert.InRange(cal.Cy, 357, 363);
        Assert.Equal(-0.15, cal.Distortion[0], 2);
        Assert.True(cal.ReprojErrorPx < 0.05, $"再投影誤差 {cal.ReprojErrorPx}");
    }

    [Fact]
    public void ステレオの相対姿勢が戻る()
    {
        // カメラ 2: カメラ 1 の右 0.6m に置き、20 度内側（左）を向ける。
        // R はカメラ 1 座標 → カメラ 2 座標の回転（X2 = R·(X1 - C2)）。正面 1m の点がカメラ 2 の画像内に来る向き
        var c2 = new Vector3(0.6f, 0, 0);
        double a = 20 * Math.PI / 180;
        var rTrue = new double[] { Math.Cos(a), 0, Math.Sin(a), 0, 1, 0, -Math.Sin(a), 0, Math.Cos(a) };
        Vector3 ToCamera2(Vector3 x1)
        {
            var d = x1 - c2;
            return new Vector3(
                (float)(rTrue[0] * d.X + rTrue[1] * d.Y + rTrue[2] * d.Z),
                (float)(rTrue[3] * d.X + rTrue[4] * d.Y + rTrue[5] * d.Z),
                (float)(rTrue[6] * d.X + rTrue[7] * d.Y + rTrue[8] * d.Z));
        }
        // T = -R·C2
        var tTrue = new[] { -(rTrue[0] * 0.6), -(rTrue[3] * 0.6), -(rTrue[6] * 0.6) };

        var poses = BoardPoses(15);
        var img1 = poses.Select(p => Project(p, TrueCamera)).ToList();
        var img2 = poses.Select(p => Project(p.Select(ToCamera2).ToArray(), TrueCamera)).ToList();

        var cal1 = CameraCalibrationSolver.CalibrateMono(img1, Spec, 1280, 720);
        var cal2 = CameraCalibrationSolver.CalibrateMono(img2, Spec, 1280, 720);
        var stereo = CameraCalibrationSolver.CalibrateStereo(img1.Zip(img2).ToList(), cal1, cal2, Spec, 0, 1);

        for (int i = 0; i < 9; i++) Assert.Equal(rTrue[i], stereo.R[i], 3);
        for (int i = 0; i < 3; i++) Assert.Equal(tTrue[i], stereo.T[i], 3); // 1mm 以内
        Assert.Equal(0.6, stereo.BaselineMeters, 3);
        Assert.True(stereo.ReprojErrorPx < 0.05, $"再投影誤差 {stereo.ReprojErrorPx}");
    }

    [Fact]
    public void 描画したチェッカーボードを検出できる()
    {
        // マス目 10×7（内側の交点 9×6）、1 マス 60px を白い画像に描く
        using var img = new Mat(720, 1280, MatType.CV_8UC3, Scalar.All(255));
        const int s = 60, ox = 100, oy = 80;
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 10; c++)
                if ((r + c) % 2 == 0)
                    Cv2.Rectangle(img, new Rect(ox + c * s, oy + r * s, s, s), Scalar.All(0), -1);

        var corners = CheckerboardDetector.Detect(img, new CheckerboardSpec(9, 6, 0.025));

        Assert.NotNull(corners);
        Assert.Equal(54, corners!.Length);
        // 最初のコーナーが左上の内側の交点になるようにそろえてある
        Assert.Equal(ox + s, corners[0].X, 1.5f);
        Assert.Equal(oy + s, corners[0].Y, 1.5f);
    }
}
