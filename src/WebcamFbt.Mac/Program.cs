using Avalonia;

namespace WebcamFbt.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 画面を出さずに推論などを確かめる（配布ビルドの自動チェック用）
        if (args.Contains("--self-test")) return SelfTest.Run();

        // 開発用: 各タブの画面を PNG に保存して終了する（Mac での見た目を CI で確かめる）
        int shot = Array.IndexOf(args, "--screenshot");
        if (shot >= 0 && shot + 1 < args.Length) Views.ScreenshotRunner.OutputDirectory = args[shot + 1];

        // 想定外の例外は落ちる前にログへ残す（Mac では画面に出せないまま終了することがあるため）
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject.ToString());

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // デザイナー（IDE のプレビュー）からも呼ばれる
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
