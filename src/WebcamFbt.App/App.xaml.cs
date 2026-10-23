using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WebcamFbt.App.Views;

namespace WebcamFbt.App;

public partial class App : Application
{
    // 二重起動を防ぐための名前（同じユーザーのログオン中だけで有効）
    private const string InstanceName = "WebcamFbt.SingleInstance";

    /// <summary>--exit で、動いている WebcamFbt が終わるのを待つ最長の時間。</summary>
    private static readonly TimeSpan ExitWaitTimeout = TimeSpan.FromSeconds(15);

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 想定外の例外でアプリが無言で落ちないよう、内容を表示する
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // --exit: 画面は出さずに、動いている WebcamFbt（トレイに隠れていても）に終了を頼み、終わるまで待つ。
        // 開発用の _run.cmd が、ビルドの前に使う（動いているとビルドが DLL を上書きできないため）
        if (e.Args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(RequestExitOfRunningInstance() ? 0 : 1);
            return;
        }

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
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 2 つ目の起動からの「画面を出して」と、--exit からの「終了して」を待つ
        Listen(_showEvent, () => window.ShowFromTray(), "SingleInstance.Show");
        Listen(_exitEvent, () => window.ExitApplication(), "SingleInstance.Exit");
    }

    /// <summary>名前付きのイベントが来るたびに、UI スレッドで <paramref name="action"/> を行う。</summary>
    private void Listen(EventWaitHandle handle, Action action, string threadName)
    {
        new Thread(() =>
        {
            while (handle.WaitOne())
            {
                Dispatcher.BeginInvoke(action);
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        }.Start();
    }

    /// <summary>
    /// 動いている WebcamFbt に「設定を保存して終了して」と頼み（トレイの「終了」と同じ処理）、終わるまで待つ。
    /// 動いていなければすぐ true。時間内に終わらなければ false（古い版で --exit を聞き取れない場合など）。
    /// </summary>
    private static bool RequestExitOfRunningInstance()
    {
        Mutex running;
        try
        {
            running = Mutex.OpenExisting(InstanceName + ".Mutex");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return true; // 動いていない
        }

        using (running)
        {
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Exit"))
            {
                exitEvent.Set();
            }
            // 動いている方が終わると Mutex が手放される（手放さずに終わると AbandonedMutexException になる）
            try
            {
                if (!running.WaitOne(ExitWaitTimeout)) return false;
                running.ReleaseMutex();
                return true;
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "WebcamFbt - エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
