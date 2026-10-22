// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.App.ViewModels;

/// <summary>状態表示の色。</summary>
public enum IndicatorState
{
    /// <summary>使っていない（灰）。</summary>
    Off,

    /// <summary>正常（緑）。</summary>
    Good,

    /// <summary>注意（黄）。</summary>
    Warn,

    /// <summary>異常・見えていない（赤）。</summary>
    Bad,
}

/// <summary>状態表示 1 つ分（トラッカーや OSC）。</summary>
public sealed partial class StatusIndicator(string label) : ObservableObject
{
    public string Label { get; } = label;

    [ObservableProperty] private IndicatorState _state;
}

/// <summary>手順ガイドの 1 段階。</summary>
/// <param name="tabIndex">押したときに開く設定タブ。</param>
public sealed partial class GuideStep(int number, string title, string hint, int tabIndex) : ObservableObject
{
    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Hint { get; } = hint;
    public int TabIndex { get; } = tabIndex;

    [ObservableProperty] private bool _isDone;

    /// <summary>次にやる段階（済んでいない最初のもの）。</summary>
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>
/// メイン画面の ViewModel のうち、画面の案内と状態表示に関わる部分（フェーズ 5b）。
///  - 手順ガイド: 開始 → カメラの校正 → 体のキャリブレーション → VRChat に送信。済んだものに印を付け、押すと設定のタブを開く
///  - 状態表示: 腰・両足・頭・OSC を色で表す
///  - 上から見た図: 最新のトラッカーの位置と向き
/// </summary>
public sealed partial class MainViewModel
{
    // 設定パネルのタブの番号（XAML の並びと同じ）
    private const int TabCamera = 0;
    private const int TabTracking = 1;
    private const int TabCalibration = 2;
    private const int TabVrChat = 3;

    [ObservableProperty] private int _selectedSettingsTab;

    /// <summary>上から見た図に描くトラッカー（実行中でなければ null）。</summary>
    [ObservableProperty] private TrackerFrame? _topDownFrame;

    public ObservableCollection<GuideStep> GuideSteps { get; } =
    [
        new(1, "開始", "カメラを選んで下の「開始」を押す", TabCamera),
        new(2, "カメラの校正（推奨）", "チェッカーボードでレンズを測る。2台なら必須", TabCalibration),
        new(3, "体のキャリブレーション", "カメラの方を向いて T ポーズで測る", TabTracking),
        new(4, "VRChat に送信", "送信先を確認して、向きを合わせる", TabVrChat),
    ];

    public ObservableCollection<StatusIndicator> Indicators { get; } =
    [
        new("腰"), new("左足"), new("右足"), new("頭"), new("OSC"),
    ];

    [RelayCommand]
    private void GoToStep(GuideStep step) => SelectedSettingsTab = step.TabIndex;

    /// <summary>手順ガイドの済み・次を更新する（UI スレッド）。</summary>
    private void UpdateGuide()
    {
        bool cameraCalibrated = IsRunning
            ? _camera1Calibration != null && (_pipeline2 == null || _stereoCalibration != null)
            // 開始前は、選んでいるカメラと解像度の校正が保存されているかで判断する
            : _calibrationStore.GetCamera(SelectedCamera.Value, SelectedResolution.Value.W, SelectedResolution.Value.H) != null
              && (!TwoCameraMode || _calibrationStore.GetStereo(SelectedCamera.Value, SelectedCamera2.Value) != null);

        bool[] done =
        [
            IsRunning,
            cameraCalibrated,
            _tracking?.Calibration.IsCalibrated == true,
            IsRunning && OscEnabled && _osc.SendRate.Rate > 1,
        ];

        bool currentFound = false;
        for (int i = 0; i < GuideSteps.Count; i++)
        {
            GuideSteps[i].IsDone = done[i];
            bool current = !done[i] && !currentFound;
            GuideSteps[i].IsCurrent = current;
            currentFound |= current;
        }
    }

    /// <summary>状態表示を更新する（UI スレッドのタイマーから）。</summary>
    private void UpdateIndicators(TrackerFrame? frame)
    {
        IndicatorState Tracker(TrackerRole role)
            => frame != null && frame.Trackers.Any(t => t.Role == role && t.Valid) ? IndicatorState.Good : IndicatorState.Bad;

        Indicators[0].State = Tracker(TrackerRole.Hip);
        Indicators[1].State = Tracker(TrackerRole.LeftFoot);
        Indicators[2].State = Tracker(TrackerRole.RightFoot);
        Indicators[3].State = !SendHeadPosition ? IndicatorState.Off
            : frame?.HeadValid == true ? IndicatorState.Good : IndicatorState.Bad;
        Indicators[4].State = !OscEnabled ? IndicatorState.Off
            : _osc.LastError != null ? IndicatorState.Bad
            : _osc.SendRate.Rate > 1 ? IndicatorState.Good
            : IndicatorState.Warn; // 送信オンだが送るものがない（体が見えていない等）
    }

    /// <summary>停止したときに状態表示を消す。</summary>
    private void ResetIndicators()
    {
        foreach (var indicator in Indicators) indicator.State = IndicatorState.Off;
        TopDownFrame = null;
    }
}
