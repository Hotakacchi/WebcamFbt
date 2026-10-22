using Avalonia.Controls;
using WebcamFbt.App.ViewModels;

namespace WebcamFbt.Mac.Views;

public partial class MainWindow : Window
{
    /// <summary>本当に終了する（閉じるボタンで隠れるのではなく）。</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        // 隠れている間は見えない映像を描かない（負荷を減らす）。トラッキングと OSC の送信は続く
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) ViewModel.SuspendPreview(!IsVisible);
        };
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext!;

    /// <summary>メニューバーのアイコンや Dock から画面を出す。</summary>
    public void ShowFromBackground()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>アプリを終了する（メニューバーの「終了」）。</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 閉じるボタン（赤い丸）では終了せず、画面だけ隠す（Windows 版の「タスクトレイに入る」に相当）。
        // command + Q やメニューの「終了」、ログアウト・シャットダウンでは終了する
        if (!_exiting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            (Avalonia.Application.Current as App)?.ShowHiddenNotice();
            return;
        }

        // カメラと推論スレッドを止めてから閉じる（設定もここで保存される）
        ViewModel.Dispose();
        base.OnClosing(e);
    }
}
