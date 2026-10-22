// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Diagnostics;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、診断用の記録に関わる部分。
/// 不具合の報告用に、数十秒ぶんの 2D の関節と計算結果（トラッカー・3D の関節・距離・向き）をファイルに書き出す。
/// 映像は記録しない。ファイルの先頭にはその時の設定とキャリブレーションを入れるので、
/// 開発側で同じ計算を再現できる（<see cref="TrackingRecording.Replay"/>）。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>1 回の記録の長さ（これで自動で止まる）。</summary>
    private static readonly TimeSpan RecordingLength = TimeSpan.FromSeconds(30);

    private TrackingRecorder? _recorder;
    private CancellationTokenSource? _recordingCts;

    [ObservableProperty] private bool _isRecording;

    /// <summary>何をしたかのメモ（記録のファイルに入れる）。</summary>
    [ObservableProperty] private string _recordingNote = "";

    [ObservableProperty] private string _recordingText = "";

    /// <summary>記録の保存先（ドキュメント\WebcamFbt\記録）。</summary>
    public static string RecordingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WebcamFbt", "記録");

    /// <summary>記録を始める。<see cref="RecordingLength"/> たつか「停止」で止まって保存する。</summary>
    [RelayCommand]
    private async Task StartRecording()
    {
        if (IsRecording) return;
        var tracking = _tracking;
        if (!IsRunning || tracking == null)
        {
            Status = "先に「開始」を押してください";
            return;
        }

        var path = Path.Combine(RecordingsFolder, $"record-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        try
        {
            // 姿勢推定は Halpe26 の並びだけを使っている
            var header = RecordingHeader.Create(tracking, KeypointLayout.Halpe26, AppInfo.FullVersion, RecordingNote.Trim());
            _recorder = new TrackingRecorder(path, header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"記録を始められませんでした: {ex.Message}";
            return;
        }

        tracking.Sampler = _recorder.Write;
        IsRecording = true;
        RecordingText = $"記録中… 症状が出る動きをしてください（{RecordingLength.TotalSeconds:F0} 秒で自動で止まります）";
        Status = "診断用の記録を始めました";

        var cts = new CancellationTokenSource();
        _recordingCts = cts;
        try
        {
            await Task.Delay(RecordingLength, cts.Token);
        }
        catch (TaskCanceledException)
        {
            return; // 「停止」で止めた
        }
        StopRecordingCore();
    }

    [RelayCommand]
    private void StopRecording()
    {
        _recordingCts?.Cancel();
        StopRecordingCore();
    }

    /// <summary>記録をやめて保存する（止めたとき・トラッキングを停止したとき・アプリを終了するとき）。</summary>
    private void StopRecordingCore()
    {
        if (_recorder is not { } recorder) return;
        if (_tracking != null) _tracking.Sampler = null;
        recorder.Dispose();
        _recorder = null;
        _recordingCts = null;
        IsRecording = false;
        RecordingText = $"保存しました（{recorder.FrameCount} フレーム）: {recorder.FilePath}";
        Status = "診断用の記録を保存しました。「フォルダを開く」からファイルを送ってください";
    }

    /// <summary>記録の保存先をエクスプローラー（Mac は Finder）で開く。</summary>
    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            Process.Start(new ProcessStartInfo(RecordingsFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"フォルダを開けませんでした: {ex.Message}（{RecordingsFolder}）";
        }
    }
}
