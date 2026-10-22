using Avalonia.Controls;
using WebcamFbt.Mac.ViewModels;

namespace WebcamFbt.Mac.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // カメラと推論スレッドを止めてから閉じる
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosing(e);
    }
}
