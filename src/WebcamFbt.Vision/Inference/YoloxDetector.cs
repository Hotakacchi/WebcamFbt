using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Vision.Inference;

/// <summary>
/// YOLOX による人物検出。
/// OpenMMLab が配布している mmdeploy 形式の ONNX（NMS 込み、出力 "dets" [1,N,5] と "labels" [1,N]）を想定する。
/// 前処理は rtmlib / mmdet と同じく、縦横比を保って縮小し、右と下を灰色(114)で埋める。正規化はしない。
/// </summary>
public sealed class YoloxDetector : IPersonDetector
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _inputW;
    private readonly int _inputH;
    private readonly float[] _inputBuffer;
    private readonly Mat _canvas;
    private readonly Mat _resized = new();

    public string Provider { get; }

    /// <summary>この値未満のスコアの矩形は捨てる。</summary>
    public float ScoreThreshold { get; set; } = 0.4f;

    public YoloxDetector(string modelPath, InferenceDevice device = InferenceDevice.Auto, int defaultInputSize = 416)
    {
        var created = OnnxSessionFactory.Create(modelPath, device);
        _session = created.Session;
        Provider = created.Provider;

        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions; // [1, 3, H, W]
        _inputH = dims.Length == 4 && dims[2] > 0 ? dims[2] : defaultInputSize;
        _inputW = dims.Length == 4 && dims[3] > 0 ? dims[3] : defaultInputSize;
        _inputBuffer = new float[3 * _inputH * _inputW];
        _canvas = new Mat(_inputH, _inputW, MatType.CV_8UC3);
    }

    public IReadOnlyList<BoundingBox> Detect(Mat bgrImage)
    {
        // --- 前処理: レターボックス（左上寄せ） ---
        float ratio = Math.Min((float)_inputH / bgrImage.Rows, (float)_inputW / bgrImage.Cols);
        int rw = Math.Max(1, (int)(bgrImage.Cols * ratio));
        int rh = Math.Max(1, (int)(bgrImage.Rows * ratio));
        Cv2.Resize(bgrImage, _resized, new Size(rw, rh), 0, 0, InterpolationFlags.Linear);
        _canvas.SetTo(new Scalar(114, 114, 114));
        using (var roi = new Mat(_canvas, new Rect(0, 0, rw, rh)))
        {
            _resized.CopyTo(roi);
        }
        ImageTensor.ToChw(_canvas, _inputBuffer, toRgb: false);

        var tensor = new DenseTensor<float>(_inputBuffer, [1, 3, _inputH, _inputW]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };

        // --- 推論 ---
        using var outputs = _session.Run(inputs);
        var detsValue = outputs.FirstOrDefault(o => o.Name == "dets") ?? outputs.First();
        var dets = detsValue.AsTensor<float>();
        var labelsValue = outputs.FirstOrDefault(o => o.Name == "labels");
        var labels = labelsValue?.AsTensor<long>();

        if (dets.Dimensions.Length != 3 || dets.Dimensions[2] != 5)
        {
            throw new NotSupportedException(
                $"想定外の YOLOX 出力形式です（形状: [{string.Join(",", dets.Dimensions.ToArray())}]）。" +
                "NMS 込みの mmdeploy 形式（dets [1,N,5]）のモデルを使ってください。");
        }

        // --- 後処理: 元画像の座標に戻す ---
        int n = dets.Dimensions[1];
        var boxes = new List<BoundingBox>(n);
        for (int i = 0; i < n; i++)
        {
            float score = dets[0, i, 4];
            if (score < ScoreThreshold) continue;
            // 人物専用モデルでは全部 0 だが、COCO 全クラスのモデルでも人(0)だけ使う
            if (labels != null && labels[0, i] != 0) continue;

            float x1 = Math.Clamp(dets[0, i, 0] / ratio, 0, bgrImage.Cols - 1);
            float y1 = Math.Clamp(dets[0, i, 1] / ratio, 0, bgrImage.Rows - 1);
            float x2 = Math.Clamp(dets[0, i, 2] / ratio, 0, bgrImage.Cols - 1);
            float y2 = Math.Clamp(dets[0, i, 3] / ratio, 0, bgrImage.Rows - 1);
            if (x2 - x1 < 4 || y2 - y1 < 4) continue;
            boxes.Add(new BoundingBox(x1, y1, x2, y2, score));
        }
        boxes.Sort((a, b) => b.Score.CompareTo(a.Score));
        return boxes;
    }

    public void Dispose()
    {
        _session.Dispose();
        _canvas.Dispose();
        _resized.Dispose();
    }
}
