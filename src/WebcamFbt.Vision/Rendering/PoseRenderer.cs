using OpenCvSharp;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Rendering;

/// <summary>
/// 推定結果（骨格・キーポイント・矩形）を画像に描き込む。
/// 色: 体の左側 = 水色、右側 = オレンジ、中央 = 黄緑（BGR で指定）。
/// 信頼度がしきい値未満の点は描かない。
/// </summary>
public static class PoseRenderer
{
    private static readonly Scalar LeftColor = new(255, 200, 0);
    private static readonly Scalar RightColor = new(0, 140, 255);
    private static readonly Scalar CenterColor = new(80, 230, 120);
    private static readonly Scalar BoxColor = new(200, 200, 200);

    public static void Draw(Mat image, Pose2D pose, float scoreThreshold, bool drawBox = true)
    {
        // 解像度に応じて線の太さを変える
        int thickness = Math.Max(2, image.Cols / 400);
        int radius = thickness + 2;

        if (drawBox)
        {
            var b = pose.Box;
            Cv2.Rectangle(image,
                new Point((int)b.X1, (int)b.Y1), new Point((int)b.X2, (int)b.Y2),
                BoxColor, 1, LineTypes.AntiAlias);
        }

        var kps = pose.Keypoints;
        foreach (var (a, b) in pose.Layout.Bones)
        {
            if (kps[a].Score < scoreThreshold || kps[b].Score < scoreThreshold) continue;
            var ja = pose.Layout.Joints[a];
            var jb = pose.Layout.Joints[b];
            var color = (KeypointLayout.IsLeft(ja) || KeypointLayout.IsLeft(jb)) && !(KeypointLayout.IsRight(ja) || KeypointLayout.IsRight(jb))
                ? LeftColor
                : (KeypointLayout.IsRight(ja) || KeypointLayout.IsRight(jb)) && !(KeypointLayout.IsLeft(ja) || KeypointLayout.IsLeft(jb))
                    ? RightColor
                    : CenterColor;
            Cv2.Line(image, ToPoint(kps[a]), ToPoint(kps[b]), color, thickness, LineTypes.AntiAlias);
        }

        for (int i = 0; i < kps.Length; i++)
        {
            if (kps[i].Score < scoreThreshold) continue;
            var j = pose.Layout.Joints[i];
            var color = KeypointLayout.IsLeft(j) ? LeftColor : KeypointLayout.IsRight(j) ? RightColor : CenterColor;
            Cv2.Circle(image, ToPoint(kps[i]), radius, color, -1, LineTypes.AntiAlias);
            Cv2.Circle(image, ToPoint(kps[i]), radius, Scalar.Black, 1, LineTypes.AntiAlias);
        }
    }

    private static Point ToPoint(Keypoint2D k) => new((int)MathF.Round(k.X), (int)MathF.Round(k.Y));
}
