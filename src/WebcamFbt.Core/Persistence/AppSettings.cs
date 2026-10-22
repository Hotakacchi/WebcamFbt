using System.Numerics;
using WebcamFbt.Core.Tracking;

namespace WebcamFbt.Core.Persistence;

/// <summary>
/// 次回の起動に引き継ぐ設定（フェーズ 5）。JSON にそのまま保存する。
/// ファイルに無い項目は既定値になるので、項目を足しても古いファイルを読める。
/// カメラの取得方式や推論デバイスは Vision の列挙型なので、Core では名前（文字列）で持つ。
/// カメラの校正結果は別ファイル（<see cref="Geometry.CameraCalibrationStore"/>）。
/// </summary>
public sealed class AppSettings
{
    // ---- カメラ ----
    public int CameraDevice { get; set; }
    public int Camera2Device { get; set; } = 1;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 60;
    public string Backend { get; set; } = "MediaFoundation";
    public string InferenceDevice { get; set; } = "Auto";
    public bool TwoCameraMode { get; set; }

    // ---- 表示 ----
    public double ScoreThreshold { get; set; } = 0.3;
    public bool MirrorPreview { get; set; } = true;
    public bool PreviewEnabled { get; set; } = true;

    // ---- 体とカメラ ----
    public double HeightMeters { get; set; } = 1.65;
    public double HorizontalFov { get; set; } = 70;
    public double CameraPitch { get; set; }
    public double TrackingThreshold { get; set; } = 0.4;
    public bool UseStereoTracking { get; set; } = true;

    // ---- 平滑化 ----
    public double PositionMinCutoff { get; set; } = 1.5;
    public double PositionBeta { get; set; } = 1.0;
    public double RotationMinCutoff { get; set; } = 1.0;
    public double RotationBeta { get; set; } = 0.3;

    // ---- OSC（送信先は「反映」に成功したもの） ----
    public bool OscEnabled { get; set; } = true;
    public string OscHost { get; set; } = "127.0.0.1";
    public int OscPort { get; set; } = 9000;
    public bool SendHeadPosition { get; set; } = true;

    // ---- チェッカーボード ----
    public int BoardInnerCols { get; set; } = 9;
    public int BoardInnerRows { get; set; } = 6;
    public double BoardSquareMm { get; set; } = 25;

    // ---- 体のキャリブレーション（未実施なら null） ----
    public BodyCalibrationSettings? BodyCalibration { get; set; }

    // ---- 新しい版の確認 ----
    /// <summary>新しい版が出ていないか GitHub Releases に問い合わせる。</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>「この版は通知しない」を選んだ版（例: 0.1.2）。</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>最後に確認できた日時（UTC）。1 日に 1 回だけ問い合わせるため。</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
}

/// <summary>
/// 体のキャリブレーション結果（<see cref="CalibrationData"/>）の保存形式。
/// Quaternion はフィールドしか持たず System.Text.Json がそのままでは保存しないので、配列にする。
/// </summary>
public sealed class BodyCalibrationSettings
{
    public double ShoulderWidth { get; set; }
    public double HipWidth { get; set; }
    public double Torso { get; set; }
    public double Thigh { get; set; }
    public double Shin { get; set; }
    public double Foot { get; set; }
    public double NeckToEars { get; set; }

    /// <summary>床の高さ [m]（未測定なら null）。</summary>
    public float? FloorY { get; set; }

    public double YawOffsetDeg { get; set; }

    /// <summary>カメラの傾きの補正 (x, y, z, w)。ステレオで測ったときだけ。</summary>
    public float[]? Tilt { get; set; }

    public static BodyCalibrationSettings From(CalibrationData data)
    {
        var b = data.Body;
        return new BodyCalibrationSettings
        {
            ShoulderWidth = b.ShoulderWidth,
            HipWidth = b.HipWidth,
            Torso = b.Torso,
            Thigh = b.Thigh,
            Shin = b.Shin,
            Foot = b.Foot,
            NeckToEars = b.NeckToEars,
            FloorY = data.HasFloor ? data.FloorY : null,
            YawOffsetDeg = data.YawOffsetDeg,
            Tilt = data.Tilt is { } q ? [q.X, q.Y, q.Z, q.W] : null,
        };
    }

    /// <summary>
    /// 元の形に戻す。値がおかしい（比率が 0 以下・回転が 4 要素でない）ときは null（キャリブレーションし直してもらう）。
    /// </summary>
    public CalibrationData? ToCalibrationData()
    {
        double[] ratios = [ShoulderWidth, HipWidth, Torso, Thigh, Shin, Foot, NeckToEars];
        if (ratios.Any(r => !double.IsFinite(r) || r <= 0 || r >= 1)) return null;
        if (Tilt is not null && Tilt.Length != 4) return null;

        var body = new BodyProportions(ShoulderWidth, HipWidth, Torso, Thigh, Shin, Foot, NeckToEars);
        Quaternion? tilt = Tilt is { } t ? Quaternion.Normalize(new Quaternion(t[0], t[1], t[2], t[3])) : null;
        return new CalibrationData(body, FloorY ?? float.NaN, YawOffsetDeg, tilt);
    }
}
