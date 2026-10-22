using System.ComponentModel;
using System.Windows;
using WebcamFbt.App.ViewModels;

namespace WebcamFbt.App.Views;

public partial class MainWindow : Window
{
    private readonly TrayIcon _tray;

    /// <summary>本当に終了する（× でトレイに隠れるのではなく）。</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        _tray = new TrayIcon(ViewModel);
        _tray.ShowRequested += ShowFromTray;
        _tray.ExitRequested += ExitApplication;

        // Windows のシャットダウン・ログオフや、インストーラの更新（Restart Manager）では、トレイに隠れずに終了する
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        // 隠れている間は見えない映像を描かない（ゲーム中の負荷を減らす）
        IsVisibleChanged += (_, _) => ViewModel.SuspendPreview(!IsVisible);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>トレイから（または 2 つ目の起動から）画面を出す。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>アプリを終了する（トレイの「終了」）。</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // × では終了せず、タスクトレイに隠れる（トラッキングと OSC の送信は裏で続く）
            e.Cancel = true;
            Hide();
            _tray.ShowHiddenNotice();
            return;
        }

        // カメラと推論スレッドを止めてから閉じる（設定もここで保存される）
        ViewModel.Dispose();
        _tray.Dispose();
        base.OnClosing(e);
    }
}
