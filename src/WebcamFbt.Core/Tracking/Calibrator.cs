using System.Numerics;
using WebcamFbt.Core.Geometry;
using WebcamFbt.Core.Pose;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// 数フレームの 3D 姿勢を貯めて、体の比率・床の高さ・正面方向（ステレオではカメラの傾きも）を求める。
/// カメラの方を向いてまっすぐ立った状態（T ポーズ）でキャプチャする前提。
///
/// 骨の長さは <see cref="Pose3D"/>（トラッキング座標、単位 m）から直接測る。
/// 片側しか見えないフレームもあるので、各骨は見えたものだけ・左右それぞれを 1 標本として貯め、
/// 最後に中央値を取る（外れ値に強くするため）。身長で割って比率にする。
///
/// 傾き（フェーズ 4d）: ステレオでは本物の 3D が取れるので、まっすぐ立った体の縦の向き（足首の中点 → 首）を
/// 重力の方向とみなし、それを真上（+Y）に重ねる回転を求める（カメラのピッチとロールの補正）。
/// 床の高さと正面方向は、その回転をかけた後の座標で測る。単眼では奥行きを仮定で出しているため前後の傾きが
/// 測れないので、全フレームがステレオのときだけ求める。
/// </summary>
public sealed class Calibrator
{
    private static readonly Joint[] FootJoints =
    [
        Joint.LeftAnkle, Joint.RightAnkle, Joint.LeftHeel, Joint.RightHeel,
        Joint.LeftBigToe, Joint.RightBigToe, Joint.LeftSmallToe, Joint.RightSmallToe,
    ];

    private readonly List<double> _shoulderWidth = new();
    private readonly List<double> _hipWidth = new();
    private readonly List<double> _torso = new();
    private readonly List<double> _thigh = new();
    private readonly List<double> _shin = new();
    private readonly List<double> _foot = new();
    private readonly List<double> _neckToEars = new();
    private readonly List<Vector3[]> _feet = new();
    private readonly List<(Vector3 Left, Vector3 Right)> _hips = new();
    private readonly List<Vector3> _up = new();
    private readonly List<double> _yaw = new();
    private int _stereoFrames;
    private readonly List<KeypointGate.GatedPoint[]> _points2D = new();

    /// <summary>
    /// 測った比率がこの範囲（身長に対する比）から外れていたら、測り損ねとみなして既定値を使う。
    /// 人の体の個人差より十分広く取ってある。
    /// </summary>
    private static readonly (string Name, Func<BodyProportions, double> Get, double Min, double Max)[] PlausibleRanges =
    [
        ("肩幅", b => b.ShoulderWidth, 0.15, 0.32),
        ("腰幅", b => b.HipWidth, 0.08, 0.22),
        ("胴", b => b.Torso, 0.24, 0.38),
        ("太もも", b => b.Thigh, 0.19, 0.31),
        ("すね", b => b.Shin, 0.19, 0.31),
        ("足", b => b.Foot, 0.10, 0.19),
        ("首〜耳", b => b.NeckToEars, 0.04, 0.13),
    ];

    /// <summary>取り込んだフレーム数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>単眼のときに取り込んだ 2D キーポイント（カメラの置き方を求めて測り直すのに使う）。</summary>
    public IReadOnlyList<KeypointGate.GatedPoint[]> Points2D => _points2D;

    /// <summary>2D キーポイントの並び（単眼のとき）。</summary>
    public KeypointLayout? Layout2D { get; private set; }

    /// <summary>2D キーポイントを撮ったカメラ（単眼のとき）。</summary>
    public CameraIntrinsics Camera2D { get; private set; }

    /// <summary>1 フレーム分の姿勢を取り込む。使える骨だけ記録する。</summary>
    /// <param name="stereo">三角測量で求めた姿勢か（傾きはステレオのときだけ求める）。</param>
    /// <param name="mono">単眼のときの元の 2D キーポイント（信頼度ゲート後）と、その並び・カメラ。</param>
    public void Add(Pose3D pose, bool stereo = false, (KeypointGate.GatedPoint[] Points, KeypointLayout Layout, CameraIntrinsics Camera)? mono = null)
    {
        FrameCount++;
        if (stereo) _stereoFrames++;
        if (mono is { } m)
        {
            _points2D.Add((KeypointGate.GatedPoint[])m.Points.Clone());
            Layout2D = m.Layout;
            Camera2D = m.Camera;
        }

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

        // 床: このフレームで見えている足（足首・かかと・つま先）。傾きを補正してから一番低い点を取るので位置ごと持つ
        var feet = new List<Vector3>();
        foreach (var j in FootJoints)
        {
            if (pose.TryGet(j, out var p)) feet.Add(p);
        }
        if (feet.Count > 0) _feet.Add([.. feet]);

        if (pose.TryGet(Joint.LeftHip, out var lh) && pose.TryGet(Joint.RightHip, out var rh)) _hips.Add((lh, rh));

        // 体の縦の向き: 足首の中点 → 首（長いほど誤差に強い）。足首が見えなければ骨盤 → 首
        if (Neck(pose, out var top))
        {
            if (pose.TryGetMid(Joint.LeftAnkle, Joint.RightAnkle, out var bottom) || Pelvis(pose, out bottom))
            {
                var up = top - bottom;
                if (up.LengthSquared() > 1e-4f) _up.Add(Vector3.Normalize(up));
            }
        }

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

        var measured = new BodyProportions(
            ShoulderWidth: Ratio(_shoulderWidth, d.ShoulderWidth),
            HipWidth: Ratio(_hipWidth, d.HipWidth),
            Torso: Ratio(_torso, d.Torso),
            Thigh: Ratio(_thigh, d.Thigh),
            Shin: Ratio(_shin, d.Shin),
            Foot: Ratio(_foot, d.Foot),
            NeckToEars: Ratio(_neckToEars, d.NeckToEars));
        var warnings = new List<string>();
        var body = CheckPlausible(measured, warnings);

        // 傾き（全フレームがステレオのときだけ）。体の縦の向きの平均を真上に重ねる最小の回転
        Quaternion? tilt = null;
        if (_stereoFrames == FrameCount && _up.Count > 0)
        {
            var mean = Vector3.Zero;
            foreach (var u in _up) mean += u;
            tilt = FromTo(mean, Vector3.UnitY);
        }
        var rotate = tilt ?? Quaternion.Identity;

        // 床: 傾きを補正した後の、フレームごとの一番低い足の Y の中央値
        var lowest = new List<float>();
        foreach (var feet in _feet)
        {
            float min = float.MaxValue;
            foreach (var p in feet) min = MathF.Min(min, Vector3.Transform(p, rotate).Y);
            lowest.Add(min);
        }
        float floorY = lowest.Count > 0 ? MedianF(lowest) : float.NaN;

        // 正面方向: 傾きを補正したときは、補正後の腰の左右から測り直す（推定時のヨーは傾いた座標で測っているため）
        double yaw;
        if (tilt is not null && _hips.Count > 0)
        {
            var yaws = new List<double>();
            foreach (var (l, r) in _hips)
            {
                var right = Vector3.Transform(r, rotate) - Vector3.Transform(l, rotate);
                yaws.Add(System.Math.Atan2(-right.Z, right.X) * 180.0 / System.Math.PI);
            }
            yaw = Median(yaws);
        }
        else
        {
            yaw = _yaw.Count > 0 ? Median(_yaw) : 0;
        }
        return new CalibrationData(body, floorY, yaw, tilt, Warnings: warnings);
    }

    /// <summary>
    /// 測った比率を確かめ、ありえない値（立ち方やカメラの置き方で測り損ねたもの）は既定値に戻す。
    /// 戻したものは <paramref name="warnings"/> に書く。すべて範囲内なら元のインスタンスを返す。
    /// </summary>
    public static BodyProportions CheckPlausible(BodyProportions measured, List<string> warnings)
    {
        var d = BodyProportions.Default;
        var bad = new List<string>();
        foreach (var (name, get, min, max) in PlausibleRanges)
        {
            double v = get(measured);
            if (!double.IsFinite(v) || v < min || v > max) bad.Add($"{name} {v:F2}");
        }
        if (bad.Count == 0) return measured;
        warnings.Add($"測った比率のうち、ありえない値を既定値に戻しました（{string.Join("、", bad)}）");

        double Pick(string name, double v, double fallback) => bad.Any(b => b.StartsWith(name + " ")) ? fallback : v;
        return new BodyProportions(
            ShoulderWidth: Pick("肩幅", measured.ShoulderWidth, d.ShoulderWidth),
            HipWidth: Pick("腰幅", measured.HipWidth, d.HipWidth),
            Torso: Pick("胴", measured.Torso, d.Torso),
            Thigh: Pick("太もも", measured.Thigh, d.Thigh),
            Shin: Pick("すね", measured.Shin, d.Shin),
            Foot: Pick("足", measured.Foot, d.Foot),
            NeckToEars: Pick("首〜耳", measured.NeckToEars, d.NeckToEars));
    }

    /// <summary>向き from を向き to に重ねる最小の回転。</summary>
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() < 1e-12f) return Quaternion.Identity; // もう重なっている（逆向きは立っていればありえない）
        float angle = MathF.Acos(System.Math.Clamp(Vector3.Dot(from, to), -1f, 1f));
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
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
