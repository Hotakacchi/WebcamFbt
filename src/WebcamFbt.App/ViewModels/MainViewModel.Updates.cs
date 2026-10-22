// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Updates;

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、新しい版の確認と通知に関わる部分。
/// 起動の少し後と、動かしている間は 1 時間おきに「前回の確認から 1 日たったか」を見て、たっていれば
/// GitHub Releases に問い合わせる。新しい版があれば知らせ（画面の上の帯、画面が隠れていればトレイの通知）、
/// ダウンロードのページを開けるようにする。確認はオフにできる。
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromDays(1);

    private string? _skippedUpdateVersion;
    private DateTime? _lastUpdateCheckUtc;
    private bool _checkingUpdates;

    /// <summary>新しい版が出ていないか確かめる（GitHub に問い合わせる）。</summary>
    [ObservableProperty] private bool _checkForUpdates = true;

    /// <summary>知らせる新しい版（無ければ null）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateBannerText))]
    private UpdateInfo? _availableUpdate;

    /// <summary>最後に確認した結果（設定の欄に出す）。</summary>
    [ObservableProperty] private string _updateStatusText = "";

    public bool HasUpdate => AvailableUpdate != null;

    public string UpdateBannerText => AvailableUpdate is { } u ? $"新しい版 v{u.Version} が出ています" : "";

    /// <summary>新しい版が見つかった（画面が隠れているときにトレイで知らせる用）。</summary>
    public event Action<UpdateInfo>? UpdateFound;

    /// <summary>設定を読み込んだ後に呼ぶ。自動の確認を始める。</summary>
    private void InitializeUpdates()
    {
        UpdateStatusText = CheckForUpdates ? "" : "確認はオフです";
        _ = AutoCheckUpdatesAsync();
    }

    private async Task AutoCheckUpdatesAsync()
    {
        // 起動直後はモデルの読み込みなどで忙しいので少し待つ
        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (CheckForUpdates && (_lastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= UpdateCheckInterval))
            {
                await RunUpdateCheckAsync(manual: false);
            }
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    /// <summary>今すぐ確かめる（設定の欄のボタン）。「通知しない」にした版も知らせる。</summary>
    [RelayCommand]
    private Task CheckUpdatesNow() => RunUpdateCheckAsync(manual: true);

    private async Task RunUpdateCheckAsync(bool manual)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        UpdateStatusText = "確認しています…";
        try
        {
            var result = await UpdateChecker.CheckAsync(AppInfo.Version);
            switch (result.Status)
            {
                case UpdateCheckStatus.Available when result.Update is { } update:
                    if (!manual && update.Version == _skippedUpdateVersion)
                    {
                        UpdateStatusText = $"新しい版 v{update.Version} があります（通知しない設定）";
                    }
                    else
                    {
                        AvailableUpdate = update;
                        UpdateStatusText = $"新しい版 v{update.Version} があります";
                        UpdateFound?.Invoke(update);
                    }
                    RecordUpdateCheck();
                    break;

                case UpdateCheckStatus.UpToDate:
                    AvailableUpdate = null;
                    UpdateStatusText = $"最新の版です（v{AppInfo.Version}）";
                    RecordUpdateCheck();
                    break;

                default:
                    // 確認できなかったときは記録しない（1 時間後にもう一度試す）
                    UpdateStatusText = $"確認できませんでした（{result.Problem}）";
                    break;
            }
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    private void RecordUpdateCheck()
    {
        _lastUpdateCheckUtc = DateTime.UtcNow;
        MarkSettingsDirty();
    }

    /// <summary>リリースのページをブラウザで開く。</summary>
    [RelayCommand]
    private void OpenUpdatePage()
    {
        var url = AvailableUpdate?.PageUrl ?? UpdateChecker.ReleasesPage;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status = $"ブラウザを開けませんでした: {ex.Message}（{url}）";
        }
    }

    /// <summary>「あとで」: 次に確認するまで帯を隠す。</summary>
    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    /// <summary>「この版は通知しない」: この版は自動の確認では知らせない（もっと新しい版が出たら知らせる）。</summary>
    [RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate is not { } update) return;
        _skippedUpdateVersion = update.Version;
        AvailableUpdate = null;
        UpdateStatusText = $"v{update.Version} は通知しません";
        MarkSettingsDirty();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!value) UpdateStatusText = "確認はオフです";
        else if (!_loadingSettings) _ = RunUpdateCheckAsync(manual: false);
    }
}
