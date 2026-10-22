namespace WebcamFbt.Core.Persistence;

/// <summary>
/// 設定ファイルが読めなかったときに、元のファイルを守る。
/// 新しい版が古いファイルをうまく読めなかった（項目の型が変わった等）とき、既定値で起動してそのまま保存すると
/// 利用者の設定が消えてしまう。そこで、読めなかったファイルは上書きする前に日時付きの別名で残す。
/// 残せなかったときは、上書きそのものをやめる。
/// </summary>
public sealed class JsonFileGuard(string filePath)
{
    /// <summary>上書きする前に、元のファイルを残す必要があるか（読み込みに失敗した）。</summary>
    private bool _protect;

    /// <summary>読めなかった理由（表示用。読めていれば null）。</summary>
    public string? Problem { get; private set; }

    /// <summary>元のファイルを残した場所（残していなければ null）。</summary>
    public string? BackupPath { get; private set; }

    /// <summary>読み込みに失敗したことを記録し、すぐに元のファイルを残す。</summary>
    public void OnLoadFailed(Exception ex)
    {
        _protect = true;
        Problem = ex is System.Text.Json.JsonException
            ? "ファイルの形式が合わず読めませんでした"
            : $"ファイルを開けませんでした（{ex.Message}）";
        TryBackup();
    }

    /// <summary>
    /// 保存する前に呼ぶ。読めなかったファイルがまだ残せていなければ残す。残せなければ例外（上書きしない）。
    /// </summary>
    public void BeforeSave()
    {
        if (!_protect) return;
        if (BackupPath == null && File.Exists(filePath) && !TryBackup())
        {
            throw new IOException($"読めなかった {Path.GetFileName(filePath)} を残せないので、上書きしません。");
        }
        _protect = false;
    }

    private bool TryBackup()
    {
        if (BackupPath != null) return true;
        if (!File.Exists(filePath)) return false;
        try
        {
            var backup = $"{filePath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(filePath, backup, overwrite: false);
            BackupPath = backup;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
