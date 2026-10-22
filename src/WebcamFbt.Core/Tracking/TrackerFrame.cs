using System.Numerics;

namespace WebcamFbt.Core.Tracking;

/// <summary>トラッカーの役割。</summary>
public enum TrackerRole
{
    Hip,
    LeftFoot,
    RightFoot,
}

/// <summary>1 つのトラッカーの位置と回転（トラッキング座標）。</summary>
/// <param name="OscIndex">VRChat の /tracking/trackers/N の N（1〜8）。</param>
/// <param name="EulerDeg">Unity のオイラー角 [度]（VRChat にそのまま送る値）。</param>
/// <param name="Valid">false のときは送信しない（関節が見えていない）。</param>
public readonly record struct TrackerPose(
    TrackerRole Role,
    int OscIndex,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 EulerDeg,
    bool Valid);

/// <summary>1 フレーム分のトラッキング結果。推論スレッドで作られ、OSC スレッドと UI が読む（不変）。</summary>
public sealed class TrackerFrame
{
    public required IReadOnlyList<TrackerPose> Trackers { get; init; }

    /// <summary>頭の位置（VRChat の位置合わせ用）。</summary>
    public required Vector3 HeadPosition { get; init; }
    public required bool HeadValid { get; init; }

    /// <summary>元のカメラフレームの取得時刻（Stopwatch のタイムスタンプ）。</summary>
    public required long CapturedTimestamp { get; init; }

    public double BodyDepth { get; init; }
    public double BodyYawDeg { get; init; }
}
