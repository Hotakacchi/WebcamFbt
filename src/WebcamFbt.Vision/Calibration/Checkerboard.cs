using System.Diagnostics;
using System.Numerics;
using OpenCvSharp;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Vision.Camera;

namespace WebcamFbt.Vision.Calibration;

/// <summary>
/// チェッカーボードの仕様。コーナー数は「内側の交点」の数（マス目が 10×7 なら 9×6）。
/// </summary>
/// <param name="SquareMeters">1 マスの一辺 [m]。印刷したものを定規で測った値を入れる（校正結果の長さの単位になる）。</param>
public sealed record CheckerboardSpec(int InnerCols, int InnerRows, double SquareMeters)
{
    public Size PatternSize => new(InnerCols, InnerRows);

    public int CornerCount => InnerCols * InnerRows;

    /// <summary>ボード上のコーナーの 3D 座標（ボード平面が Z = 0、検出順と同じ行優先）。</summary>
    public Point3f[] ObjectPoints()
    {
        var points = new Point3f[CornerCount];
        for (int r = 0; r < InnerRows; r++)
        {
            for (int c = 0; c < InnerCols; c++)
            {
                points[r * InnerCols + c] = new Point3f((float)(c * SquareMeters), (float)(r * SquareMeters), 0);
            }
        }
        return points;
    }
}

/// <summary>チェッカーボードの検出と描画。</summary>
public static class CheckerboardDetector
{
    /// <summary>
    /// コーナーを検出する。見つからなければ null。
    /// findChessboardCornersSB はサブピクセル精度まで求めるので、cornerSubPix は不要。
    /// </summary>
    public static Vector2[]? Detect(Mat bgrImage, CheckerboardSpec spec)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgrImage, gray, ColorConversionCodes.BGR2GRAY);
        if (!Cv2.FindChessboardCornersSB(gray, spec.PatternSize, out Point2f[] corners, ChessboardFlags.NormalizeImage)
            || corners.Length != spec.CornerCount)
        {
            return null;
        }

        var points = new Vector2[corners.Length];
        for (int i = 0; i < corners.Length; i++) points[i] = new Vector2(corners[i].X, corners[i].Y);

        // ボードを 180 度回すと逆順に検出されることがある。2 台で順番が食い違うとステレオの組が壊れるので、
        // 最初のコーナーが画像の左上寄りになるようにそろえる（行優先の並びを逆にすると、ちょうど 180 度回した並びになる）
        if (points[0].X + points[0].Y > points[^1].X + points[^1].Y) Array.Reverse(points);
        return points;
    }

    public static void Draw(Mat bgrImage, CheckerboardSpec spec, Vector2[] corners)
    {
        var points = new Point2f[corners.Length];
        for (int i = 0; i < corners.Length; i++) points[i] = new Point2f(corners[i].X, corners[i].Y);
        Cv2.DrawChessboardCorners(bgrImage, spec.PatternSize, points, true);
    }
}

/// <summary>
/// 校正用の撮影 1 回分。推論スレッドから各フレームを渡すと、検出してサンプルを貯め、コーナーを画像に描く。
/// </summary>
public sealed class CheckerboardCaptureSession
{
    public CheckerboardSpec Spec { get; }
    public CheckerboardSampleCollector Collector { get; }

    public CheckerboardCaptureSession(CheckerboardSpec spec, bool stereo, int targetCount)
    {
        Spec = spec;
        Collector = new CheckerboardSampleCollector(stereo, targetCount);
    }

    /// <summary>1 フレームを処理する（カメラごとの推論スレッドから呼ぶ）。</summary>
    public void Process(Frame frame)
    {
        if (frame.CameraIndex is < 0 or > 1) return;
        var corners = CheckerboardDetector.Detect(frame.Image, Spec);
        double time = frame.CapturedTimestamp / (double)Stopwatch.Frequency;
        Collector.Submit(frame.CameraIndex, time, corners, frame.Image.Cols, frame.Image.Rows);
        if (corners != null) CheckerboardDetector.Draw(frame.Image, Spec, corners);
    }
}
