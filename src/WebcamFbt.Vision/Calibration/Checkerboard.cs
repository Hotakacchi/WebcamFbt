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
/// 印刷用のチェッカーボードの画像を作る。マス目は「内側の交点 + 1」個ずつ並べ、周りに白い余白を付ける
/// （検出にはボードの周りの余白が必要）。印刷の拡大・縮小で大きさが変わるので、1 マスは利用者に定規で測ってもらう。
/// </summary>
public static class CheckerboardImage
{
    /// <param name="squarePixels">1 マスの大きさ [px]。既定は 300dpi で約 21mm（9×6 なら A4 横に等倍で収まる）。</param>
    public static Mat Create(int innerCols, int innerRows, int squarePixels = 250)
    {
        int cols = innerCols + 1, rows = innerRows + 1;
        int sq = squarePixels;
        int margin = sq;               // 周りの白い余白（1 マス分。半マスだと検出できないことがあった）
        int footer = sq / 2;           // 下の説明書きの分
        int width = cols * sq + 2 * margin;
        int height = rows * sq + 2 * margin + footer;

        var image = new Mat(height, width, MatType.CV_8UC1, Scalar.All(255));
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                if ((r + c) % 2 == 0) Cv2.Rectangle(image, new Rect(margin + c * sq, margin + r * sq, sq, sq), Scalar.All(0), -1);
            }
        }

        // 説明書き（OpenCV は日本語を描けないので英語）。幅に収まるよう文字の大きさを決める
        var text = $"WebcamFbt calibration board: {innerCols} x {innerRows} inner corners. Measure one square with a ruler and enter its size (mm).";
        const HersheyFonts font = HersheyFonts.HersheySimplex;
        int thickness = Math.Max(1, sq / 100);
        var size = Cv2.GetTextSize(text, font, 1.0, thickness, out _);
        double scale = Math.Min(sq / 60.0, (width - 2.0 * margin) / size.Width);
        // ボードの下の白い余白（1 マス分）は検出に要るので空けておき、その下の欄に書く
        Cv2.PutText(image, text, new Point(margin, margin + rows * sq + margin + footer / 2),
            font, scale, Scalar.All(90), thickness, LineTypes.AntiAlias);
        return image;
    }

    /// <summary>画像を PNG で保存する。</summary>
    public static void Save(string path, int innerCols, int innerRows)
    {
        using var image = Create(innerCols, innerRows);
        if (!Cv2.ImWrite(path, image)) throw new IOException($"画像を保存できませんでした: {path}");
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
