using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace WebcamFbt.Mac.Views;

/// <summary>
/// 開発用: 起動して設定パネルの 4 つのタブを順に開き、ウィンドウを PNG に保存して終了する
/// （`WebcamFbt --screenshot <保存先フォルダ>`）。GitHub Actions の macOS ランナーで Mac での見た目を確かめるのに使う。
/// 設定ファイルは読むが書かない（何も変更しないので保存されない）。
/// </summary>
public static class ScreenshotRunner
{
    public static string? OutputDirectory { get; set; }

    public static void Start(MainWindow window)
    {
        var dir = OutputDirectory;
        if (dir == null) return;
        Directory.CreateDirectory(dir);

        int tab = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            // 前のタブを撮ってから次へ（タブを切り替えた直後は描画が間に合わないので 1 拍おく）
            Save(window, Path.Combine(dir, $"tab{tab}.png"));
            tab++;
            if (tab < 4)
            {
                window.ViewModel.SelectedSettingsTab = tab;
                return;
            }
            timer.Stop();
            Console.WriteLine($"画面を保存しました: {dir}");
            window.ExitApplication();
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        };
        window.Opened += (_, _) => timer.Start();
    }

    private static void Save(MainWindow window, string path)
    {
        var scaling = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        bitmap.Save(path);
    }
}
