using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WebcamFbt.App.Views;

namespace WebcamFbt.App;

public partial class App : Application
{
    // 二重起動を防ぐための名前（同じユーザーのログオン中だけで有効）
    private const string InstanceName = "WebcamFbt.SingleInstance";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 想定外の例外でアプリが無言で落ちないよう、内容を表示する
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 2 つ目の起動なら、すでに動いている方（タスクトレイに隠れているかもしれない）の画面を出して終わる。
        // 2 つ動くと同じカメラを取り合ってしまうため
        _instanceMutex = new Mutex(true, InstanceName + ".Mutex", out bool firstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!firstInstance)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 2 つ目の起動からの「画面を出して」を待つ
        var showEvent = _showEvent;
        new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstance",
        }.Start();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "WebcamFbt - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
