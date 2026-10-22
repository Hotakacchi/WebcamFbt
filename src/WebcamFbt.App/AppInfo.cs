// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.Reflection;

namespace WebcamFbt.App;

/// <summary>
/// アプリの版。Directory.Build.props の &lt;Version&gt; から自動で入る（版を上げれば表示も変わる）。
/// ビルド時には「0.1.1+コミットのハッシュ」の形の版も埋め込まれるので、不具合の報告用にそれも出せるようにする。
/// </summary>
public static class AppInfo
{
    /// <summary>コミットまで含む版（例: 0.1.1+f71030a4ea…）。</summary>
    public static string FullVersion { get; }

    /// <summary>表示用の版（例: 0.1.1）。</summary>
    public static string Version { get; }

    /// <summary>右パネルの下に出す文字（例: WebcamFbt v0.1.1）。</summary>
    public static string DisplayName => $"WebcamFbt v{Version}";

    /// <summary>ウィンドウのタイトル。</summary>
    public static string WindowTitle => $"WebcamFbt v{Version} - Webカメラ フルボディトラッキング";

    /// <summary>マウスを乗せたときに出す詳しい版（例: 版 0.1.1（コミット f71030a））。</summary>
    public static string VersionDetail { get; }

    static AppInfo()
    {
        var assembly = typeof(AppInfo).Assembly;
        FullVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "?";
        int plus = FullVersion.IndexOf('+');
        Version = plus >= 0 ? FullVersion[..plus] : FullVersion;
        var commit = plus >= 0 ? FullVersion[(plus + 1)..] : "";
        VersionDetail = commit.Length > 0
            ? $"版 {Version}（コミット {commit[..Math.Min(7, commit.Length)]}）"
            : $"版 {Version}";
    }
}
