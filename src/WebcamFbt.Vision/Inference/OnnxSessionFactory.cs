using Microsoft.ML.OnnxRuntime;

namespace WebcamFbt.Vision.Inference;

/// <summary>推論に使うデバイス。</summary>
public enum InferenceDevice
{
    /// <summary>GPU（Windows は DirectML、Mac は CoreML）が使えれば GPU、だめなら CPU。</summary>
    Auto,
    DirectML,
    Cpu,

    /// <summary>Mac 版の GPU / Neural Engine。Windows では DirectML と同じ扱い。</summary>
    CoreML,
}

/// <summary>
/// ONNX Runtime のセッションを作る。GPU（Windows は DirectML、Mac は CoreML）での作成に失敗したら CPU に切り替える。
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
#if WINDOWS
                var options = CreateBaseOptions();
                // DirectML はメモリパターン最適化と並列実行に未対応なので、両方オフにする（公式ドキュメントより）
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(gpuDeviceId);
                return new Result(new InferenceSession(modelPath, options), "DirectML");
#else
                // Mac 版: CoreML（Apple の GPU / Neural Engine）。DirectML のような制約は無い。
                // Windows 以外では DirectML を選んでも同じく「GPU 優先」として扱う
                if (OperatingSystem.IsMacOS())
                {
                    var options = CreateBaseOptions();
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    var coreMl = new Dictionary<string, string>
                    {
                        // MLProgram 形式のほうが対応する演算が多い（macOS 12 以降）
                        ["ModelFormat"] = "MLProgram",
                        ["MLComputeUnits"] = "ALL",
                        // 形が実行時に決まる演算は CPU に回す。YOLOX の後段（人数ぶんの箱）は人がいないと
                        // 0 要素になり、CoreML に任せると「0 要素は非対応」で推論が失敗する（CI の自己診断で確認）
                        ["RequireStaticInputShapes"] = "1",
                    };
                    var cacheDir = CoreMlCacheDirectory();
                    if (cacheDir != null) coreMl["ModelCacheDirectory"] = cacheDir;
                    options.AppendExecutionProvider("CoreML", coreMl);
                    return new Result(new InferenceSession(modelPath, options), "CoreML");
                }
                if (device != InferenceDevice.Auto)
                {
                    throw new PlatformNotSupportedException("この OS では GPU 推論に対応していません。");
                }
#endif
            }
            catch (Exception) when (device == InferenceDevice.Auto)
            {
                // GPU が DirectX 12 非対応（Windows）、CoreML で読めない（Mac）などの場合は CPU にフォールバック
            }
        }

        var cpuOptions = CreateBaseOptions();
        cpuOptions.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        // VRChat と同時に動かすので、CPU を使い切らないようスレッド数を控えめにする
        cpuOptions.IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2));
        return new Result(new InferenceSession(modelPath, cpuOptions), "CPU");
    }

#if !WINDOWS
    /// <summary>CoreML に変換したモデルの置き場所（2 回目以降の起動を速くする）。作れなければ null。</summary>
    private static string? CoreMlCacheDirectory()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "coreml-cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }
#endif

    private static SessionOptions CreateBaseOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
    };
}
