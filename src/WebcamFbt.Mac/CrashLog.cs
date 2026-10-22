namespace WebcamFbt.Mac;

/// <summary>
/// 想定外のエラーをファイルに残す（~/Library/Application Support/WebcamFbt/error.log）。
/// 友達の Mac で動かしたときに、何が起きたかを後から送ってもらえるようにするため。
/// </summary>
public static class CrashLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "error.log");

    public static void Write(string? message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n\n");
        }
        catch
        {
            // ログが書けなくても何もしない
        }
    }
}
