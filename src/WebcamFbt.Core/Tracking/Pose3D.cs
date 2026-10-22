using System.Numerics;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 3D の姿勢（トラッキング座標、単位 m）。関節ごとに位置と有効フラグを持つ。
/// </summary>
public sealed class Pose3D
{
    private static readonly int JointCount = Enum.GetValues<Joint>().Length;

    private readonly Vector3[] _positions = new Vector3[JointCount];
    private readonly bool[] _valid = new bool[JointCount];

    /// <summary>カメラから体（骨盤）までの推定距離 [m]。</summary>
    public double BodyDepth { get; init; }

    /// <summary>推定した体の向き（ヨー）[度]。カメラの方を向いていると 0、右を向くと正。</summary>
    public double BodyYawDeg { get; init; }

    public void Set(Joint joint, Vector3 position)
    {
        _positions[(int)joint] = position;
        _valid[(int)joint] = true;
    }

    public bool TryGet(Joint joint, out Vector3 position)
    {
        position = _positions[(int)joint];
        return _valid[(int)joint];
    }

    /// <summary>2 つの関節の中点。どちらかが無効なら false。</summary>
    public bool TryGetMid(Joint a, Joint b, out Vector3 mid)
    {
        if (TryGet(a, out var pa) && TryGet(b, out var pb))
        {
            mid = (pa + pb) * 0.5f;
            return true;
        }
        mid = default;
        return false;
    }
}
