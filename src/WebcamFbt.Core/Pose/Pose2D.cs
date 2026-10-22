namespace WebcamFbt.Core.Pose;

/// <summary>画像上の 1 点のキーポイント（ピクセル座標と信頼度）。</summary>
public readonly record struct Keypoint2D(float X, float Y, float Score);

/// <summary>画像上の矩形（左上と右下、ピクセル座標）。</summary>
public readonly record struct BoundingBox(float X1, float Y1, float X2, float Y2, float Score = 1f)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
    public float Area => MathF.Max(0, Width) * MathF.Max(0, Height);
    public float CenterX => (X1 + X2) * 0.5f;
    public float CenterY => (Y1 + Y2) * 0.5f;
}

/// <summary>
/// 1 人分の 2D 姿勢推定結果。
/// </summary>
public sealed class Pose2D
{
    public KeypointLayout Layout { get; }
    public Keypoint2D[] Keypoints { get; }

    /// <summary>推定に使った人物の矩形。</summary>
    public BoundingBox Box { get; }

    public Pose2D(KeypointLayout layout, Keypoint2D[] keypoints, BoundingBox box)
    {
        if (keypoints.Length != layout.Count)
        {
            throw new ArgumentException($"キーポイント数 {keypoints.Length} がレイアウト {layout.Name} の {layout.Count} と一致しません。");
        }
        Layout = layout;
        Keypoints = keypoints;
        Box = box;
    }

    /// <summary>関節のキーポイントを取得する。モデルがその関節を持たない場合は false。</summary>
    public bool TryGet(Joint joint, out Keypoint2D keypoint)
    {
        int i = Layout.IndexOf(joint);
        if (i < 0)
        {
            keypoint = default;
            return false;
        }
        keypoint = Keypoints[i];
        return true;
    }

    /// <summary>信頼度がしきい値以上のキーポイントの数。</summary>
    public int CountConfident(float threshold)
    {
        int n = 0;
        foreach (var k in Keypoints)
        {
            if (k.Score >= threshold) n++;
        }
        return n;
    }
}
