using System.Diagnostics;

namespace WebcamFbt.Mac.Platform;

/// <summary>
/// macOS の通知センターに通知を出す（Windows 版のトレイの吹き出しに相当）。
/// 署名していないアプリは通知の許可を得るのが難しいので、macOS 標準の osascript（AppleScript）の
/// display notification を使う（通知は「スクリプトエディタ」の名前で出ることがある）。Mac 以外では何もしない。
/// </summary>
public static class MacNotification
{
    public static void Show(string title, string message)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"");
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog.Write($"通知を出せませんでした: {ex.Message}");
        }
    }

    // AppleScript の文字列の中で特別な意味を持つ文字を逃がす
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
