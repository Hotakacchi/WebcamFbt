// Mac 版（WebcamFbt.Mac）からも同じファイルをコンパイルして共有している。WPF の型（System.Windows.*）を使わないこと。
using System.ComponentModel;
using System.IO;
using WebcamFbt.Core.Persistence;
using WebcamFbt.Core.Tracking;
using WebcamFbt.Vision.Camera;
using WebcamFbt.Vision.Inference;
#if WINDOWS
using System.Windows.Threading;
#else
using Avalonia.Threading; // Mac 版。DispatcherTimer の使い方は WPF と同じ
#endif

namespace WebcamFbt.App.ViewModels;

/// <summary>
/// メイン画面の ViewModel のうち、設定の保存・復元に関わる部分（フェーズ 5a）。
/// 起動時に %LOCALAPPDATA%\WebcamFbt\settings.json を読んで画面に反映し、
/// 設定が変わったら 1 秒後にまとめて保存する（終了時にも保存）。体のキャリブレーション結果も保存する。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>変わったら保存する設定（画面のプロパティ名）。</summary>
    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(SelectedCamera), nameof(SelectedCamera2), nameof(SelectedResolution), nameof(SelectedFps),
        nameof(SelectedBackend), nameof(SelectedDevice), nameof(TwoCameraMode),
        nameof(ScoreThreshold), nameof(MirrorPreview), nameof(PreviewEnabled),
        nameof(HeightMeters), nameof(HorizontalFov), nameof(CameraPitch), nameof(TrackingThreshold), nameof(UseStereoTracking),
        nameof(PositionMinCutoff), nameof(PositionBeta), nameof(RotationMinCutoff), nameof(RotationBeta),
        nameof(OscEnabled), nameof(SendHeadPosition),
        nameof(BoardInnerCols), nameof(BoardInnerRows), nameof(BoardSquareMm),
    ];

    private readonly AppSettingsStore _settingsStore = new();
    private DispatcherTimer? _settingsSaveTimer;
    private bool _settingsDirty;
    private bool _loadingSettings;

    // OSC の送信先は、入力欄の値ではなく「反映」に成功したものを保存する
    private string _appliedOscHost = "127.0.0.1";
    private int _appliedOscPort = 9000;

    /// <summary>コンストラクタの最後に呼ぶ。保存してある設定を画面に反映し、以後の変更を保存するようにする。</summary>
    private void InitializeSettings()
    {
        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _settingsSaveTimer.Tick += (_, _) => SaveSettingsNow();

        _loadingSettings = true;
        try
        {
            ApplySettings(_settingsStore.Load());
        }
        finally
        {
            _loadingSettings = false;
        }
        PropertyChanged += OnSettingPropertyChanged;
        UpdateGuide();
    }

    /// <summary>
    /// 読み込んだ設定を画面に反映する。今の選択肢に無い値（つながっていないカメラの番号など）は既定のまま、
    /// 数値はスライダーの範囲に収める。
    /// </summary>
    private void ApplySettings(AppSettings s)
    {
        SelectedCamera = CameraChoices.FirstOrDefault(c => c.Value == s.CameraDevice) ?? SelectedCamera;
        SelectedCamera2 = CameraChoices.FirstOrDefault(c => c.Value == s.Camera2Device) ?? SelectedCamera2;
        SelectedResolution = ResolutionChoices.FirstOrDefault(c => c.Value == (s.Width, s.Height)) ?? SelectedResolution;
        SelectedFps = FpsChoices.FirstOrDefault(c => c.Value == s.Fps) ?? SelectedFps;
        if (Enum.TryParse<CameraBackend>(s.Backend, out var backend))
        {
            SelectedBackend = BackendChoices.FirstOrDefault(c => c.Value == backend) ?? SelectedBackend;
        }
        if (Enum.TryParse<InferenceDevice>(s.InferenceDevice, out var device))
        {
            SelectedDevice = DeviceChoices.FirstOrDefault(c => c.Value == device) ?? SelectedDevice;
        }
        TwoCameraMode = s.TwoCameraMode;

        ScoreThreshold = Math.Clamp(s.ScoreThreshold, 0, 1);
        MirrorPreview = s.MirrorPreview;
        PreviewEnabled = s.PreviewEnabled;

        HeightMeters = Math.Clamp(s.HeightMeters, 1.2, 2.1);
        HorizontalFov = Math.Clamp(s.HorizontalFov, 40, 120);
        CameraPitch = Math.Clamp(s.CameraPitch, -45, 45);
        TrackingThreshold = Math.Clamp(s.TrackingThreshold, 0.1, 0.9);
        UseStereoTracking = s.UseStereoTracking;

        PositionMinCutoff = Math.Clamp(s.PositionMinCutoff, 0.05, 5);
        PositionBeta = Math.Clamp(s.PositionBeta, 0, 5);
        RotationMinCutoff = Math.Clamp(s.RotationMinCutoff, 0.05, 5);
        RotationBeta = Math.Clamp(s.RotationBeta, 0, 3);

        OscEnabled = s.OscEnabled;
        SendHeadPosition = s.SendHeadPosition;
        try
        {
            _osc.SetEndpoint(s.OscHost.Trim(), s.OscPort);
            _appliedOscHost = s.OscHost.Trim();
            _appliedOscPort = s.OscPort;
        }
        catch (Exception)
        {
            // 保存されていた送信先が使えない（名前を引けない等）ときは既定のまま
        }
        OscHost = _appliedOscHost;
        OscPort = _appliedOscPort;

        BoardInnerCols = Math.Clamp(s.BoardInnerCols, 3, 30);
        BoardInnerRows = Math.Clamp(s.BoardInnerRows, 3, 30);
        BoardSquareMm = s.BoardSquareMm > 0 ? s.BoardSquareMm : 25;

        if (s.BodyCalibration?.ToCalibrationData() is { } body)
        {
            _tracking ??= new TrackingProcessor(_trackingSettings);
            _tracking.RestoreCalibration(body);
            CalibrationText = DescribeBodyCalibration(body);
        }
    }

    private AppSettings BuildSettings() => new()
    {
        CameraDevice = SelectedCamera.Value,
        Camera2Device = SelectedCamera2.Value,
        Width = SelectedResolution.Value.W,
        Height = SelectedResolution.Value.H,
        Fps = SelectedFps.Value,
        Backend = SelectedBackend.Value.ToString(),
        InferenceDevice = SelectedDevice.Value.ToString(),
        TwoCameraMode = TwoCameraMode,
        ScoreThreshold = ScoreThreshold,
        MirrorPreview = MirrorPreview,
        PreviewEnabled = PreviewEnabled,
        HeightMeters = HeightMeters,
        HorizontalFov = HorizontalFov,
        CameraPitch = CameraPitch,
        TrackingThreshold = TrackingThreshold,
        UseStereoTracking = UseStereoTracking,
        PositionMinCutoff = PositionMinCutoff,
        PositionBeta = PositionBeta,
        RotationMinCutoff = RotationMinCutoff,
        RotationBeta = RotationBeta,
        OscEnabled = OscEnabled,
        OscHost = _appliedOscHost,
        OscPort = _appliedOscPort,
        SendHeadPosition = SendHeadPosition,
        BoardInnerCols = BoardInnerCols,
        BoardInnerRows = BoardInnerRows,
        BoardSquareMm = BoardSquareMm,
        BodyCalibration = _tracking?.Calibration is { IsCalibrated: true } cal ? BodyCalibrationSettings.From(cal) : null,
    };

    private void OnSettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not { } name || !PersistedProperties.Contains(name)) return;
        MarkSettingsDirty();
        // 開始前にカメラや解像度を変えたら、手順ガイドの「カメラの校正」の済み・未を見直す
        if (!IsRunning) UpdateGuide();
    }

    /// <summary>設定が変わったことを記録し、少し待ってから保存する（スライダーを動かしている間に何度も書かないため）。</summary>
    private void MarkSettingsDirty()
    {
        if (_loadingSettings) return;
        _settingsDirty = true;
        _settingsSaveTimer?.Stop();
        _settingsSaveTimer?.Start();
    }

    /// <summary>変更があれば今すぐ保存する（終了時にも呼ぶ）。</summary>
    private void SaveSettingsNow()
    {
        _settingsSaveTimer?.Stop();
        if (!_settingsDirty) return;
        try
        {
            _settingsStore.Save(BuildSettings());
            _settingsDirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"設定を保存できませんでした: {ex.Message}";
        }
    }
}
