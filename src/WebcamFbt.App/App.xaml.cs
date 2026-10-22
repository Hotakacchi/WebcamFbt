using System.Windows;
using System.Windows.Threading;

namespace WebcamFbt.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 想定外の例外でアプリが無言で落ちないよう、内容を表示する
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "WebcamFbt - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
