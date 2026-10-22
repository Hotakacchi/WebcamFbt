namespace WebcamFbt.Core.Pose;

/// <summary>
/// モデルが出力するキーポイントの並び（何番目がどの関節か）と、描画用の骨の組み合わせを表す。
/// </summary>
public sealed class KeypointLayout
{
    public string Name { get; }

    /// <summary>キーポイント番号 → 関節。</summary>
    public IReadOnlyList<Joint> Joints { get; }

    /// <summary>描画用の骨（キーポイント番号の組）。</summary>
    public IReadOnlyList<(int A, int B)> Bones { get; }

    private readonly Dictionary<Joint, int> _indexOf;

    public KeypointLayout(string name, IReadOnlyList<Joint> joints, IReadOnlyList<(Joint A, Joint B)> bones)
    {
        Name = name;
        Joints = joints;
        _indexOf = new Dictionary<Joint, int>();
        for (int i = 0; i < joints.Count; i++)
        {
            _indexOf[joints[i]] = i;
        }
        Bones = bones
            .Where(b => _indexOf.ContainsKey(b.A) && _indexOf.ContainsKey(b.B))
            .Select(b => (_indexOf[b.A], _indexOf[b.B]))
            .ToArray();
    }

    public int Count => Joints.Count;

    /// <summary>関節に対応するキーポイント番号。モデルがその関節を持たない場合は -1。</summary>
    public int IndexOf(Joint joint) => _indexOf.TryGetValue(joint, out int i) ? i : -1;

    /// <summary>
    /// Halpe26 形式（RTMPose の body7-halpe26 モデルが出力する 26 点）。
    /// COCO17 の 17 点に、頭頂・首・骨盤・足先（親指・小指・かかと）を加えたもの。
    /// </summary>
    public static KeypointLayout Halpe26 { get; } = new(
        "Halpe26",
        [
            Joint.Nose, Joint.LeftEye, Joint.RightEye, Joint.LeftEar, Joint.RightEar,
            Joint.LeftShoulder, Joint.RightShoulder, Joint.LeftElbow, Joint.RightElbow,
            Joint.LeftWrist, Joint.RightWrist, Joint.LeftHip, Joint.RightHip,
            Joint.LeftKnee, Joint.RightKnee, Joint.LeftAnkle, Joint.RightAnkle,
            Joint.HeadTop, Joint.Neck, Joint.Pelvis,
            Joint.LeftBigToe, Joint.RightBigToe, Joint.LeftSmallToe, Joint.RightSmallToe,
            Joint.LeftHeel, Joint.RightHeel,
        ],
        [
            // 頭
            (Joint.Nose, Joint.LeftEye), (Joint.Nose, Joint.RightEye),
            (Joint.LeftEye, Joint.LeftEar), (Joint.RightEye, Joint.RightEar),
            (Joint.HeadTop, Joint.Neck), (Joint.Neck, Joint.Nose),
            // 胴体
            (Joint.Neck, Joint.LeftShoulder), (Joint.Neck, Joint.RightShoulder),
            (Joint.Neck, Joint.Pelvis),
            (Joint.Pelvis, Joint.LeftHip), (Joint.Pelvis, Joint.RightHip),
            // 腕
            (Joint.LeftShoulder, Joint.LeftElbow), (Joint.LeftElbow, Joint.LeftWrist),
            (Joint.RightShoulder, Joint.RightElbow), (Joint.RightElbow, Joint.RightWrist),
            // 脚
            (Joint.LeftHip, Joint.LeftKnee), (Joint.LeftKnee, Joint.LeftAnkle),
            (Joint.RightHip, Joint.RightKnee), (Joint.RightKnee, Joint.RightAnkle),
            // 足
            (Joint.LeftAnkle, Joint.LeftHeel), (Joint.LeftAnkle, Joint.LeftBigToe), (Joint.LeftAnkle, Joint.LeftSmallToe),
            (Joint.RightAnkle, Joint.RightHeel), (Joint.RightAnkle, Joint.RightBigToe), (Joint.RightAnkle, Joint.RightSmallToe),
        ]);

    /// <summary>関節が体の左側か（描画の色分け用）。</summary>
    public static bool IsLeft(Joint j) => j.ToString().StartsWith("Left", StringComparison.Ordinal);

    /// <summary>関節が体の右側か（描画の色分け用）。</summary>
    public static bool IsRight(Joint j) => j.ToString().StartsWith("Right", StringComparison.Ordinal);
}
