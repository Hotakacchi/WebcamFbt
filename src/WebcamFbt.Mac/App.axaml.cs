using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using WebcamFbt.App.ViewModels;
using WebcamFbt.Mac.Views;

namespace WebcamFbt.Mac;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _startStopItem;
    private bool _hiddenNoticeShown;

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
            // 閉じるボタンでは画面を隠すだけにするので、終了は「終了」（command + Q）を選んだときだけ
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new MainWindow();
            desktop.MainWindow = _window;
            CreateTrayIcon(_window);
            ScreenshotRunner.Start(_window);

            // Dock のアイコンを押したら（アプリがもう動いているとき）画面を出す
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind == ActivationKind.Reopen) _window.ShowFromBackground();
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// メニューバー（画面右上）のアイコン。Windows 版のタスクトレイに相当し、「表示」「開始 / 停止」「終了」を出す。
    /// </summary>
    private void CreateTrayIcon(MainWindow window)
    {
        var vm = window.ViewModel;
        var show = new NativeMenuItem("表示");
        show.Click += (_, _) => window.ShowFromBackground();
        _startStopItem = new NativeMenuItem("開始");
        _startStopItem.Click += (_, _) => vm.StartStopCommand.Execute(null);
        var exit = new NativeMenuItem("終了");
        exit.Click += (_, _) => Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(show);
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://WebcamFbt/Assets/WebcamFbt.ico"))),
            Menu = menu,
            IsVisible = true,
        };
        TrayIcon.SetIcons(this, [_tray]);

        vm.PropertyChanged += OnViewModelPropertyChanged;
        UpdateTrayText(vm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MainViewModel vm && e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.IsBusy))
        {
            UpdateTrayText(vm);
        }
    }

    /// <summary>メニューの「開始 / 停止」と、マウスを乗せたときの説明を今の状態に合わせる。</summary>
    private void UpdateTrayText(MainViewModel vm)
    {
        if (_startStopItem != null) _startStopItem.Header = vm.IsBusy ? "中止（準備中）" : vm.StartStopLabel;
        if (_tray != null) _tray.ToolTipText = $"{WebcamFbt.App.AppInfo.DisplayName} - {(vm.IsBusy ? "準備中" : vm.IsRunning ? "実行中" : "停止中")}";
    }

    /// <summary>
    /// 閉じるボタンで画面を隠したことを知らせる（起動中に 1 回だけ）。
    /// Mac には Windows のような通知の吹き出しが無いので、状態の欄に出す（次に画面を出したときに見える）。
    /// </summary>
    public void ShowHiddenNotice()
    {
        if (_hiddenNoticeShown || _window == null) return;
        _hiddenNoticeShown = true;
        if (_window.ViewModel.IsRunning)
        {
            _window.ViewModel.Status = "画面を閉じても動き続けます。終了するときはメニューバーのアイコン →「終了」、または command + Q";
        }
    }

    private void Shutdown()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _window?.ExitApplication();
            _tray?.Dispose();
            desktop.Shutdown();
        }
    }
}
