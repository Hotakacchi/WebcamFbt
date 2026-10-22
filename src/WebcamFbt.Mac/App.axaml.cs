using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using WebcamFbt.Mac.Views;

namespace WebcamFbt.Mac;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // UI スレッドの想定外の例外でアプリが無言で落ちないよう、ログに残して続行する
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception.ToString());
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
