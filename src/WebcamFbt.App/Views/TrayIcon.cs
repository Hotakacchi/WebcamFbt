using System.ComponentModel;
using System.Windows;
using WebcamFbt.App.ViewModels;
using Forms = System.Windows.Forms;

namespace WebcamFbt.App.Views;

/// <summary>
/// タスクトレイ（通知領域）のアイコン。× で閉じたウィンドウはここに隠れ、トラッキングと OSC の送信は裏で続く。
/// 右クリックのメニューで「表示」「開始 / 停止」「終了」、ダブルクリックで表示。
/// WPF には通知領域のアイコンがないので、Windows Forms の NotifyIcon を使う（.NET の一部）。
/// メニューのイベントは WPF の UI スレッドで呼ばれる（同じメッセージループで動くため）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _startStopItem;
    private bool _hiddenNoticeShown;

    /// <summary>最後に出した通知が「新しい版」のものか（押されたらダウンロードのページを開く）。</summary>
    private bool _updateNoticeShown;

    /// <summary>「表示」が選ばれた（ダブルクリックを含む）。</summary>
    public event Action? ShowRequested;

    /// <summary>「終了」が選ばれた。</summary>
    public event Action? ExitRequested;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;

        var show = new Forms.ToolStripMenuItem("表示", null, (_, _) => ShowRequested?.Invoke());
        show.Font = new System.Drawing.Font(show.Font, System.Drawing.FontStyle.Bold); // ダブルクリックと同じ動作
        _startStopItem = new Forms.ToolStripMenuItem("開始", null, (_, _) => _vm.StartStopCommand.Execute(null));
        var exit = new Forms.ToolStripMenuItem("終了", null, (_, _) => ExitRequested?.Invoke());
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.AddRange([show, _startStopItem, new Forms.ToolStripSeparator(), exit]);
        // 開いたときの状態に合わせて「開始」「停止」を出し分ける（準備中は押すと中止になる）
        _menu.Opening += (_, _) => _startStopItem.Text = _vm.IsBusy ? "中止（準備中）" : _vm.StartStopLabel;

        // ウィンドウと同じアイコンを、通知領域の大きさ（DPI に応じた小アイコン）で読む
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/WebcamFbt.ico"));
        using (var stream = resource.Stream)
        {
            _icon = new Forms.NotifyIcon
            {
                Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
                ContextMenuStrip = _menu,
                Visible = true,
            };
        }
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_updateNoticeShown) _vm.OpenUpdatePageCommand.Execute(null);
            else ShowRequested?.Invoke();
        };

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateText();
    }

    /// <summary>
    /// ウィンドウをトレイに隠したことを知らせる（起動中に 1 回だけ。閉じたつもりで裏で動いていることに気づけるように）。
    /// </summary>
    public void ShowHiddenNotice()
    {
        if (_hiddenNoticeShown) return;
        _hiddenNoticeShown = true;
        _updateNoticeShown = false;
        _icon.ShowBalloonTip(4000, "WebcamFbt は動き続けています",
            "タスクトレイのアイコンから、画面の表示・開始 / 停止・終了ができます。", Forms.ToolTipIcon.Info);
    }

    /// <summary>新しい版が出たことを知らせる（画面が隠れているとき）。押すとダウンロードのページを開く。</summary>
    public void ShowUpdateNotice(string version)
    {
        _updateNoticeShown = true;
        _icon.ShowBalloonTip(8000, $"WebcamFbt の新しい版 v{version} が出ています",
            "ここを押すとダウンロードのページを開きます。", Forms.ToolTipIcon.Info);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsBusy)) UpdateText();
    }

    /// <summary>マウスを乗せたときの説明（63 文字まで）。</summary>
    private void UpdateText()
        => _icon.Text = $"{AppInfo.DisplayName} - {(_vm.IsBusy ? "準備中" : _vm.IsRunning ? "実行中" : "停止中")}";

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
