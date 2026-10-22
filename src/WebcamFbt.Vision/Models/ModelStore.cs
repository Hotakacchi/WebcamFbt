using System.IO.Compression;
using System.Security.Cryptography;

namespace WebcamFbt.Vision.Models;

/// <summary>
/// モデルファイルの置き場所を管理し、無ければダウンロードする。
/// 探す順番:
///  1. exe と同じフォルダの models\（手動で置いた場合）
///  2. %LOCALAPPDATA%\WebcamFbt\models\（自動ダウンロード先）
/// </summary>
public sealed class ModelStore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public string BundledDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "models");

    public string DownloadDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "models");

    /// <summary>既にあるモデルのパス。無ければ null。</summary>
    public string? Find(ModelInfo model)
    {
        foreach (var dir in new[] { BundledDirectory, DownloadDirectory })
        {
            var path = Path.Combine(dir, model.FileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// モデルを用意する（既にあればそのパスを返し、無ければダウンロードする）。
    /// </summary>
    /// <param name="progress">進捗（0〜1）と状況メッセージ。</param>
    public async Task<string> EnsureAsync(ModelInfo model, IProgress<(double Ratio, string Message)>? progress, CancellationToken ct)
    {
        var existing = Find(model);
        if (existing != null) return existing;

        Directory.CreateDirectory(DownloadDirectory);
        var zipPath = Path.Combine(DownloadDirectory, model.Id + ".zip.part");
        var onnxPath = Path.Combine(DownloadDirectory, model.FileName);
        var tmpOnnxPath = onnxPath + ".part";

        try
        {
            // --- zip をダウンロード ---
            progress?.Report((0, $"{model.DisplayName} をダウンロード中…"));
            using (var response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total is > 0)
                    {
                        progress?.Report(((double)received / total.Value,
                            $"{model.DisplayName} をダウンロード中… {received / 1048576.0:F1} / {total.Value / 1048576.0:F1} MB"));
                    }
                }
            }

            // --- zip から end2end.onnx を取り出す ---
            progress?.Report((1, $"{model.DisplayName} を展開中…"));
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("end2end.onnx", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{model.DisplayName} の zip に .onnx ファイルが見つかりません。");
                entry.ExtractToFile(tmpOnnxPath, overwrite: true);
            }

            // --- ハッシュ照合 ---
            var hash = await ComputeSha256Async(tmpOnnxPath, ct);
            if (model.Sha256 != null && !hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{model.DisplayName} のハッシュが一致しません（期待値 {model.Sha256}、実際 {hash}）。ダウンロードが壊れている可能性があります。");
            }
            File.WriteAllText(onnxPath + ".sha256", hash);
            File.Move(tmpOnnxPath, onnxPath, overwrite: true);
            return onnxPath;
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(tmpOnnxPath);
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 一時ファイルの削除失敗は無視する
        }
    }
}
