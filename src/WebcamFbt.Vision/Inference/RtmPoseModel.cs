using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// RTMPose（SimCC 方式）による、人物矩形内のキーポイント推定。
/// 前処理・後処理は MMPose / rtmlib の実装に合わせている。
///  1. 矩形を 1.25 倍に広げ、モデル入力の縦横比（192:256）に合わせて切り出す（アフィン変換）
///  2. RGB に並べ替え、ImageNet の平均・標準偏差で正規化
///  3. 出力 simcc_x / simcc_y の最大位置を座標に、最大値を信頼度にする（split ratio = 2.0）
/// </summary>
public sealed class RtmPoseModel : IKeypointModel
{
    private static readonly float[] Mean = [123.675f, 116.28f, 103.53f]; // RGB 順
    private static readonly float[] Std = [58.395f, 57.12f, 57.375f];
    private const float BoxPadding = 1.25f;
    private const float SimccSplitRatio = 2.0f;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputW;
    private readonly int _inputH;
    private readonly float[] _inputBuffer;
    private readonly Mat _warped;
    private readonly Mat _affine = new(2, 3, MatType.CV_64FC1);

    public string Provider { get; }
    public KeypointLayout Layout { get; }

    public RtmPoseModel(string modelPath, KeypointLayout layout, InferenceDevice device = InferenceDevice.Auto,
        int defaultInputW = 192, int defaultInputH = 256)
    {
        var created = OnnxSessionFactory.Create(modelPath, device);
        _session = created.Session;
        Provider = created.Provider;
        Layout = layout;

        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions; // [1, 3, H, W]
        _inputH = dims.Length == 4 && dims[2] > 0 ? dims[2] : defaultInputH;
        _inputW = dims.Length == 4 && dims[3] > 0 ? dims[3] : defaultInputW;
        _inputBuffer = new float[3 * _inputH * _inputW];
        _warped = new Mat(_inputH, _inputW, MatType.CV_8UC3);
    }

    public Keypoint2D[] Predict(Mat bgrImage, BoundingBox box)
    {
        // --- 前処理: 切り出し範囲（中心とサイズ）を決める ---
        float cx = box.CenterX;
        float cy = box.CenterY;
        float sw = box.Width * BoxPadding;
        float sh = box.Height * BoxPadding;
        float aspect = (float)_inputW / _inputH;
        if (sw > sh * aspect) sh = sw / aspect;
        else sw = sh * aspect;

        // 元画像 → モデル入力 への変換（回転なし、拡大縮小と平行移動のみ）
        double k = _inputW / (double)sw;
        _affine.Set(0, 0, k);
        _affine.Set(0, 1, 0.0);
        _affine.Set(0, 2, -k * (cx - sw / 2.0));
        _affine.Set(1, 0, 0.0);
        _affine.Set(1, 1, k);
        _affine.Set(1, 2, -k * (cy - sh / 2.0));
        Cv2.WarpAffine(bgrImage, _warped, _affine, new Size(_inputW, _inputH),
            InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        ImageTensor.ToChw(_warped, _inputBuffer, toRgb: true, Mean, Std);
        var tensor = new DenseTensor<float>(_inputBuffer, [1, 3, _inputH, _inputW]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        // --- 推論 ---
        using var outputs = _session.Run(inputs);
        var simccX = (outputs.FirstOrDefault(o => o.Name == "simcc_x") ?? outputs.ElementAt(0)).AsTensor<float>();
        var simccY = (outputs.FirstOrDefault(o => o.Name == "simcc_y") ?? outputs.ElementAt(1)).AsTensor<float>();

        int kCount = simccX.Dimensions[1];
        int wx = simccX.Dimensions[2];
        int wy = simccY.Dimensions[2];
        if (kCount != Layout.Count)
        {
            throw new InvalidOperationException(
                $"モデルのキーポイント数 {kCount} が、指定されたレイアウト {Layout.Name}（{Layout.Count} 点）と一致しません。");
        }

        float[] xs = simccX.ToArray();
        float[] ys = simccY.ToArray();

        // --- 後処理: SimCC のデコード ---
        var result = new Keypoint2D[kCount];
        double invK = 1.0 / k;
        double ox = cx - sw / 2.0;
        double oy = cy - sh / 2.0;
        for (int j = 0; j < kCount; j++)
        {
            (int ix, float vx) = ArgMax(xs.AsSpan(j * wx, wx));
            (int iy, float vy) = ArgMax(ys.AsSpan(j * wy, wy));
            // MMPose と同じく、x と y の最大値のうち小さい方を信頼度とする
            float score = Math.Min(vx, vy);
            float px = (float)(ix / SimccSplitRatio * invK + ox);
            float py = (float)(iy / SimccSplitRatio * invK + oy);
            result[j] = new Keypoint2D(px, py, score);
        }
        return result;
    }

    private static (int Index, float Value) ArgMax(ReadOnlySpan<float> values)
    {
        int best = 0;
        float bestValue = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] > bestValue)
            {
                bestValue = values[i];
                best = i;
            }
        }
        return (best, bestValue);
    }

    public void Dispose()
    {
        _session.Dispose();
        _warped.Dispose();
        _affine.Dispose();
    }
}
