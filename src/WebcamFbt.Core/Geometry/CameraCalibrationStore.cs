using System.Text.Json;

namespace WebcamFbt.Core.Geometry;

/// <summary>
/// カメラの校正結果をファイルに保存・読み込みする（既定は %LOCALAPPDATA%\WebcamFbt\calibration.json）。
/// 内部パラメータは「デバイス番号 × 解像度」ごと、ステレオは 1 組だけ持つ。
/// 校正はやり直しに手間がかかるので、設定全般の保存（フェーズ 5）より先にこれだけ保存する。
/// UI スレッドから呼ぶ（スレッドセーフではない）。
/// </summary>
public sealed class CameraCalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class FileModel
    {
        public Dictionary<string, CameraCalibration> Cameras { get; set; } = new();
        public StereoCalibration? Stereo { get; set; }
    }

    private readonly Persistence.JsonFileGuard _guard;

    public string FilePath { get; }

    /// <summary>読めなかったファイルを残した場所（残していなければ null）。</summary>
    public string? BackupPath => _guard.BackupPath;

    public CameraCalibrationStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebcamFbt", "calibration.json");
        _guard = new Persistence.JsonFileGuard(FilePath);
    }

    private static string Key(int deviceIndex, int width, int height) => $"device{deviceIndex}_{width}x{height}";

    /// <summary>デバイスと解像度に合う内部パラメータ。無ければ null。</summary>
    public CameraCalibration? GetCamera(int deviceIndex, int width, int height)
        => Load().Cameras.GetValueOrDefault(Key(deviceIndex, width, height));

    public void SaveCamera(int deviceIndex, CameraCalibration calibration)
    {
        var model = Load();
        model.Cameras[Key(deviceIndex, calibration.Width, calibration.Height)] = calibration;
        Save(model);
    }

    /// <summary>ステレオの校正結果。2 台の内部パラメータも、それぞれのデバイスの分として保存する。</summary>
    public void SaveStereo(StereoCalibration stereo)
    {
        var model = Load();
        model.Stereo = stereo;
        model.Cameras[Key(stereo.Device1, stereo.Camera1.Width, stereo.Camera1.Height)] = stereo.Camera1;
        model.Cameras[Key(stereo.Device2, stereo.Camera2.Width, stereo.Camera2.Height)] = stereo.Camera2;
        Save(model);
    }

    /// <summary>指定したデバイスの組で校正したステレオの結果。組が違う・無ければ null。</summary>
    public StereoCalibration? GetStereo(int device1, int device2)
    {
        var stereo = Load().Stereo;
        return stereo is not null && stereo.Device1 == device1 && stereo.Device2 == device2 ? stereo : null;
    }

    private FileModel Load()
    {
        if (!File.Exists(FilePath)) return new FileModel();
        try
        {
            return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), JsonOptions) ?? new FileModel();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 読めないファイルは無視して進める。保存はファイル全体の書き直しなので、
            // 他のカメラの校正まで消えないよう、元のファイルは日時付きの別名で残しておく
            _guard.OnLoadFailed(ex);
            return new FileModel();
        }
    }

    private void Save(FileModel model)
    {
        _guard.BeforeSave();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        // 書き込み途中で落ちても元のファイルが壊れないよう、一時ファイルに書いてから置き換える
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(model, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
