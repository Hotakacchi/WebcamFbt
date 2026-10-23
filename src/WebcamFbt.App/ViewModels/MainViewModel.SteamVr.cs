// Windows 版だけ（Mac には SteamVR が無いので、Mac 版はこのファイルをコンパイルしない）。
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、SteamVR に合わせた自動の開始・停止。
/// オンの間は数秒ごとに SteamVR のサーバー（vrserver.exe）が動いているかを見て、
/// 起動したら開始し、終了したら停止する。切り替わったときだけ動くので、SteamVR を使わずに手で開始したものは止めない。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>SteamVR が動いているかを見る間隔。</summary>
    private static readonly TimeSpan SteamVrPollInterval = TimeSpan.FromSeconds(3);

    /// <summary>SteamVR が動いている間だけあるプロセスの名前（.exe を除く）。</summary>
    private const string SteamVrProcessName = "vrserver";

    [ObservableProperty] private bool _autoStartWithSteamVr;

    private Timer? _steamVrTimer;
    private bool _steamVrRunning;
    private int _steamVrPolling; // 前の確認が終わる前に次を始めない

    partial void OnAutoStartWithSteamVrChanged(bool value)
    {
        if (value)
        {
            // 最初の確認で SteamVR がもう動いていれば、そのまま開始する（後から WebcamFbt を起動したときなど）
            _steamVrRunning = false;
            _steamVrTimer ??= new Timer(_ => PollSteamVr(), null, TimeSpan.Zero, SteamVrPollInterval);
        }
        else
        {
            _steamVrTimer?.Dispose();
            _steamVrTimer = null;
        }
    }

    /// <summary>スレッドプールで呼ばれる。動いているかが変わったときだけ、UI スレッドで開始・停止する。</summary>
    private void PollSteamVr()
    {
        if (Interlocked.Exchange(ref _steamVrPolling, 1) == 1) return;
        try
        {
            bool running = IsProcessRunning(SteamVrProcessName);
            Application.Current?.Dispatcher.InvokeAsync(() => OnSteamVrState(running));
        }
        finally
        {
            Volatile.Write(ref _steamVrPolling, 0);
        }
    }

    private void OnSteamVrState(bool running)
    {
        if (!AutoStartWithSteamVr || running == _steamVrRunning) return;
        _steamVrRunning = running;

        if (running)
        {
            if (IsRunning || IsBusy) return;
            Status = "SteamVR が起動したので開始します";
            _ = StartPipelineAsync();
        }
        else
        {
            if (IsBusy) _startCts?.Cancel(); // 準備中なら中止
            if (!IsRunning) return;
            StopPipeline();
            Status = "SteamVR が終了したので停止しました";
        }
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private void StopSteamVrWatch()
    {
        _steamVrTimer?.Dispose();
        _steamVrTimer = null;
    }
}
