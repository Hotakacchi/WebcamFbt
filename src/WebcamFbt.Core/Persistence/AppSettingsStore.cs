using System.Text.Json;

namespace WebcamFbt.Core.Persistence;

/// <summary>
/// 設定をファイルに保存・読み込みする（既定は %LOCALAPPDATA%\WebcamFbt\settings.json）。
/// 読めない・壊れているときは既定値で起動する（次に保存したときに上書きされる）。
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string FilePath { get; }

    public AppSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "settings.json");
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
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // 書き込み途中で落ちても元のファイルが壊れないよう、一時ファイルに書いてから置き換える
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
