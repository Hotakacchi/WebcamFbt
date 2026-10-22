using System.Text.Json;

namespace WebcamFbt.Core.Persistence;

/// <summary>
/// 設定をファイルに保存・読み込みする（既定は %LOCALAPPDATA%\WebcamFbt\settings.json）。
/// 読めない・壊れているときは既定値で起動する。そのとき元のファイルは日時付きの別名で残し、
/// 黙って上書きしない（新しい版が古いファイルを読めなかったときに設定が消えないように）。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly JsonFileGuard _guard;

    public string FilePath { get; }

    /// <summary>直近の <see cref="Load"/> で読めなかった理由（読めていれば null）。</summary>
    public string? LoadProblem => _guard.Problem;

    /// <summary>読めなかったファイルを残した場所（残していなければ null）。</summary>
    public string? BackupPath => _guard.BackupPath;

    public AppSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "settings.json");
        _guard = new JsonFileGuard(FilePath);
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _guard.OnLoadFailed(ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        _guard.BeforeSave();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // 書き込み途中で落ちても元のファイルが壊れないよう、一時ファイルに書いてから置き換える
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
