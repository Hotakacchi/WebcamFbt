using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;
using WebcamFbt.Core.Pose;
using WebcamFbt.Vision.Inference;
using WebcamFbt.Vision.Models;

namespace WebcamFbt.Mac;

/// <summary>
/// 画面を出さずに、ネイティブライブラリの読み込みと推論が動くかを確かめる（`WebcamFbt --self-test`）。
/// GitHub Actions の macOS ランナーで .pkg を作るときに実行する（カメラは無いので確かめない）。
/// 終了コード: 0 = 成功、1 = 失敗。CoreML が使えないだけなら CPU に切り替わるので警告にとどめる。
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[失敗] {ex}");
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription} / プロセス: {RuntimeInformation.ProcessArchitecture}");

        // --- OpenCV（libOpenCvSharpExtern.dylib）: 読み込めるか、カメラ取得に AVFoundation が入っているか ---
        Console.WriteLine($"OpenCV: {Cv2.GetVersionString()}");
        var videoIo = Cv2.GetBuildInformation().Split('\n')
            .Where(l => l.Contains("AVFoundation", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim()).ToList();
        Console.WriteLine($"  {string.Join(" / ", videoIo)}");
        if (OperatingSystem.IsMacOS() && !videoIo.Any(l => l.Contains("YES", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("[失敗] OpenCV に AVFoundation が入っていません（カメラを開けない）。");
            return 1;
        }

        // --- モデル ---
        var store = new ModelStore();
        var detPath = await store.EnsureAsync(ModelCatalog.YoloxTinyHuman, null, default);
        var posePath = await store.EnsureAsync(ModelCatalog.RtmPoseMHalpe26, null, default);

        using var image = DrawFigure();
        var box = new BoundingBox(220, 40, 420, 460);

        // --- CPU（必ず動く必要がある） ---
        var cpu = RunModels(detPath, posePath, InferenceDevice.Cpu, image, box);
        if (cpu == null) return 1;

        // --- アプリと同じ組み立て（自動 = CoreML 優先、だめなら CPU）で、人がいない画像・いる画像を続けて推定できるか ---
        // 人がいないと YOLOX の出力が 0 件になり、CoreML ではここで失敗したことがある。失敗したら自己診断も失敗にする
        using (var estimator = await PoseEstimatorFactory.CreateDefaultAsync(store, InferenceDevice.Auto, null, default))
        using (var empty = new Mat(720, 1280, MatType.CV_8UC3, new Scalar(40, 40, 40)))
        {
            for (int i = 0; i < 3; i++)
            {
                estimator.Estimate(empty);
                estimator.Estimate(image);
            }
            Console.WriteLine($"自動: 実行={estimator.Provider}、人がいない画像・いる画像の推定 OK");
        }

        // --- CoreML（だめでも自動で CPU になるので警告） ---
        var coreMl = RunModels(detPath, posePath, InferenceDevice.CoreML, image, box);
        if (coreMl == null)
        {
            Console.WriteLine("[警告] CoreML で動きませんでした。アプリは CPU で動きます。");
        }
        else
        {
            // CoreML は 16bit 浮動小数で計算することがあるので、CPU と少しずれるのは正常
            double diff = cpu.Zip(coreMl, (a, b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y))).Average();
            Console.WriteLine($"CPU と CoreML のキーポイントの差（平均）: {diff:F2} px");
            if (diff > 10)
            {
                Console.WriteLine("[警告] CPU と CoreML の結果が大きく違います。CoreML の計算がおかしい可能性があります。");
            }
        }

        Console.WriteLine("[成功] 自己診断を終えました。");
        return 0;
    }

    /// <summary>推論を 1 回ずつ動かしてキーポイントを返す。失敗したら null。</summary>
    private static Keypoint2D[]? RunModels(string detPath, string posePath, InferenceDevice device, Mat image, BoundingBox box)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var detector = new YoloxDetector(detPath, device);
            using var pose = new RtmPoseModel(posePath, KeypointLayout.Halpe26, device);
            var loadMs = sw.Elapsed.TotalMilliseconds;

            // 1 回目は準備で遅いので、2 回目の時間を測る
            detector.Detect(image);
            pose.Predict(image, box);
            sw.Restart();
            var boxes = detector.Detect(image);
            var detMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var keypoints = pose.Predict(image, box);
            var poseMs = sw.Elapsed.TotalMilliseconds;

            Console.WriteLine($"{device}: 実行={detector.Provider}/{pose.Provider} 読み込み {loadMs:F0} ms、" +
                              $"検出 {detMs:F1} ms（{boxes.Count} 人）、姿勢 {poseMs:F1} ms");
            return keypoints;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{device}: 失敗 {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>人の形をした簡単な絵（棒人間）。推論結果を CPU と CoreML で比べるための入力。</summary>
    private static Mat DrawFigure()
    {
        var img = new Mat(480, 640, MatType.CV_8UC3, new Scalar(90, 110, 120));
        var skin = new Scalar(150, 180, 220);
        var cloth = new Scalar(60, 60, 160);
        Cv2.Circle(img, new Point(320, 90), 32, skin, -1);                          // 頭
        Cv2.Rectangle(img, new Rect(280, 125, 80, 140), cloth, -1);                 // 胴
        Cv2.Line(img, new Point(285, 135), new Point(235, 260), skin, 16);          // 左腕
        Cv2.Line(img, new Point(355, 135), new Point(405, 260), skin, 16);          // 右腕
        Cv2.Line(img, new Point(300, 265), new Point(285, 440), new Scalar(80, 50, 30), 22); // 左脚
        Cv2.Line(img, new Point(340, 265), new Point(355, 440), new Scalar(80, 50, 30), 22); // 右脚
        return img;
    }
}
