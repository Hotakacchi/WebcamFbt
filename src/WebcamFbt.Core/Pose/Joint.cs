namespace WebcamFbt.Core.Pose;

/// <summary>
/// アプリ内部で使う関節の種類。
/// モデルごとのキーポイント番号は <see cref="KeypointLayout"/> でこの列挙型に対応付ける。
/// </summary>
public enum Joint
{
    Nose,
    LeftEye,
    RightEye,
    LeftEar,
    RightEar,
    LeftShoulder,
    RightShoulder,
    LeftElbow,
    RightElbow,
    LeftWrist,
    RightWrist,
    LeftHip,
    RightHip,
    LeftKnee,
    RightKnee,
    LeftAnkle,
    RightAnkle,
    HeadTop,
    Neck,
    Pelvis,
    LeftBigToe,
    RightBigToe,
    LeftSmallToe,
    RightSmallToe,
    LeftHeel,
    RightHeel,
}
