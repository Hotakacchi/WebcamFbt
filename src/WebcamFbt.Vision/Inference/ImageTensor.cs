using OpenCvSharp;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// OpenCV の画像（HWC, BGR, uint8）を ONNX 用の CHW float 配列に変換する補助。
/// </summary>
internal static class ImageTensor
{
    /// <summary>
    /// BGR 画像を CHW の float 配列に書き込む。
    /// </summary>
    /// <param name="image">CV_8UC3 の画像（幅・高さはモデル入力と同じであること）。</param>
    /// <param name="dst">長さ 3*H*W の出力先。</param>
    /// <param name="toRgb">true ならチャンネル順を RGB にする。</param>
    /// <param name="mean">チャンネルごとの平均（出力のチャンネル順で指定）。null なら正規化しない。</param>
    /// <param name="std">チャンネルごとの標準偏差（出力のチャンネル順で指定）。</param>
    public static unsafe void ToChw(Mat image, float[] dst, bool toRgb, float[]? mean = null, float[]? std = null)
    {
        if (image.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("CV_8UC3 の画像が必要です。", nameof(image));
        }

        int h = image.Rows;
        int w = image.Cols;
        int plane = h * w;
        if (dst.Length < plane * 3)
        {
            throw new ArgumentException("出力配列が小さすぎます。", nameof(dst));
        }

        // 出力チャンネル c に入れる入力チャンネル番号（BGR の何番目か）
        int src0 = toRgb ? 2 : 0;
        int src2 = toRgb ? 0 : 2;

        float m0 = 0, m1 = 0, m2 = 0, s0 = 1, s1 = 1, s2 = 1;
        if (mean != null && std != null)
        {
            m0 = mean[0]; m1 = mean[1]; m2 = mean[2];
            s0 = 1f / std[0]; s1 = 1f / std[1]; s2 = 1f / std[2];
        }

        fixed (float* pDst = dst)
        {
            float* c0 = pDst;
            float* c1 = pDst + plane;
            float* c2 = pDst + plane * 2;
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)image.Ptr(y);
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    byte* px = row + x * 3;
                    c0[o + x] = (px[src0] - m0) * s0;
                    c1[o + x] = (px[1] - m1) * s1;
                    c2[o + x] = (px[src2] - m2) * s2;
                }
            }
        }
    }
}
