using Microsoft.ML.OnnxRuntime;

namespace WebcamFbt.Vision.Inference;

/// <summary>推論に使うデバイス。</summary>
public enum InferenceDevice
{
    /// <summary>DirectML が使えれば GPU、だめなら CPU。</summary>
    Auto,
    DirectML,
    Cpu,
}

/// <summary>
/// ONNX Runtime のセッションを作る。DirectML（GPU）での作成に失敗したら CPU に切り替える。
/// </summary>
public static class OnnxSessionFactory
{
    /// <summary>作成したセッションと、実際に使われた実行プロバイダー名。</summary>
    public sealed record Result(InferenceSession Session, string Provider);

    public static Result Create(string modelPath, InferenceDevice device = InferenceDevice.Auto, int gpuDeviceId = 0)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("モデルファイルが見つかりません。", modelPath);
        }

        if (device != InferenceDevice.Cpu)
        {
            try
            {
                var options = CreateBaseOptions();
                // DirectML はメモリパターン最適化と並列実行に未対応なので、両方オフにする（公式ドキュメントより）
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(gpuDeviceId);
                return new Result(new InferenceSession(modelPath, options), "DirectML");
            }
            catch (Exception) when (device == InferenceDevice.Auto)
            {
                // GPU が DirectX 12 非対応などの場合は CPU にフォールバック
            }
        }

        var cpuOptions = CreateBaseOptions();
        cpuOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // VRChat と同時に動かすので、CPU を使い切らないようスレッド数を控えめにする
        cpuOptions.IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        return new Result(new InferenceSession(modelPath, cpuOptions), "CPU");
    }

    private static SessionOptions CreateBaseOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
    };
}
