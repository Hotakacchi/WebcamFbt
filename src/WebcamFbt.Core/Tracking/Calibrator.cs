using System.Numerics;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 数フレームの 3D 姿勢を貯めて、体の比率・床の高さ・正面方向を求める（カメラ 1 台）。
/// カメラの方を向いて立った状態（T ポーズ）でキャプチャする前提。
///
/// 骨の長さは <see cref="Pose3D"/>（トラッキング座標、単位 m）から直接測る。
/// 片側しか見えないフレームもあるので、各骨は見えたものだけ・左右それぞれを 1 標本として貯め、
/// 最後に中央値を取る（外れ値に強くするため）。身長で割って比率にする。
/// </summary>
public sealed class Calibrator
{
    private readonly List<double> _shoulderWidth = new();
    private readonly List<double> _hipWidth = new();
    private readonly List<double> _torso = new();
    private readonly List<double> _thigh = new();
    private readonly List<double> _shin = new();
    private readonly List<double> _foot = new();
    private readonly List<double> _neckToEars = new();
    private readonly List<float> _floor = new();
    private readonly List<double> _yaw = new();

    /// <summary>取り込んだフレーム数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>1 フレーム分の姿勢を取り込む。使える骨だけ記録する。</summary>
    public void Add(Pose3D pose)
    {
        FrameCount++;

        if (Neck(pose, out var neck))
        {
            if (Pelvis(pose, out var pelvis)) _torso.Add(Vector3.Distance(neck, pelvis));
            if (MidEars(pose, out var ears)) _neckToEars.Add(System.Math.Abs(neck.Y - ears.Y));
        }

        AddBone(_thigh, pose, Joint.LeftHip, Joint.LeftKnee);
        AddBone(_thigh, pose, Joint.RightHip, Joint.RightKnee);
        AddBone(_shin, pose, Joint.LeftKnee, Joint.LeftAnkle);
        AddBone(_shin, pose, Joint.RightKnee, Joint.RightAnkle);
        AddBone(_foot, pose, Joint.LeftHeel, Joint.LeftBigToe);
        AddBone(_foot, pose, Joint.RightHeel, Joint.RightBigToe);
        AddBone(_shoulderWidth, pose, Joint.LeftShoulder, Joint.RightShoulder);
        AddBone(_hipWidth, pose, Joint.LeftHip, Joint.RightHip);

        // 床: このフレームで見えている足（足首・かかと・つま先）のうち、いちばん低い Y
        float? lowest = null;
        foreach (var j in new[]
        {
            Joint.LeftAnkle, Joint.RightAnkle, Joint.LeftHeel, Joint.RightHeel,
            Joint.LeftBigToe, Joint.RightBigToe, Joint.LeftSmallToe, Joint.RightSmallToe,
        })
        {
            if (pose.TryGet(j, out var p) && (lowest is null || p.Y < lowest)) lowest = p.Y;
        }
        if (lowest is not null) _floor.Add(lowest.Value);

        _yaw.Add(pose.BodyYawDeg);
    }

    /// <summary>
    /// 貯めた観測から結果を作る。胴と脚の比率が取れていなければ（体が見えていなければ）null。
    /// </summary>
    public CalibrationData? Build(double heightMeters)
    {
        if (heightMeters <= 0) return null;
        if (_torso.Count == 0 || _thigh.Count == 0 || _shin.Count == 0) return null;

        var d = BodyProportions.Default;
        double Ratio(List<double> samples, double fallback)
            => samples.Count > 0 ? Median(samples) / heightMeters : fallback;

        var body = new BodyProportions(
            ShoulderWidth: Ratio(_shoulderWidth, d.ShoulderWidth),
            HipWidth: Ratio(_hipWidth, d.HipWidth),
            Torso: Ratio(_torso, d.Torso),
            Thigh: Ratio(_thigh, d.Thigh),
            Shin: Ratio(_shin, d.Shin),
            Foot: Ratio(_foot, d.Foot),
            NeckToEars: Ratio(_neckToEars, d.NeckToEars));

        float floorY = _floor.Count > 0 ? MedianF(_floor) : float.NaN;
        double yaw = _yaw.Count > 0 ? Median(_yaw) : 0;
        return new CalibrationData(body, floorY, yaw);
    }

    private static void AddBone(List<double> list, Pose3D pose, Joint a, Joint b)
    {
        if (pose.TryGet(a, out var pa) && pose.TryGet(b, out var pb)) list.Add(Vector3.Distance(pa, pb));
    }

    private static bool Neck(Pose3D p, out Vector3 v)
        => p.TryGet(Joint.Neck, out v) || p.TryGetMid(Joint.LeftShoulder, Joint.RightShoulder, out v);

    private static bool Pelvis(Pose3D p, out Vector3 v)
        => p.TryGet(Joint.Pelvis, out v) || p.TryGetMid(Joint.LeftHip, Joint.RightHip, out v);

    private static bool MidEars(Pose3D p, out Vector3 v)
        => p.TryGetMid(Joint.LeftEar, Joint.RightEar, out v) || p.TryGetMid(Joint.LeftEye, Joint.RightEye, out v);

    /// <summary>中央値。元のリストは並べ替えない。</summary>
    private static double Median(List<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) * 0.5;
    }

    private static float MedianF(List<float> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        int n = a.Length;
        return n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) * 0.5f;
    }
}
