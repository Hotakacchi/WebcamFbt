using System.ComponentModel;
using System.Windows;
using WebcamFbt.App.ViewModels;

namespace WebcamFbt.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // カメラと推論スレッドを止めてから閉じる
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosing(e);
    }
}
