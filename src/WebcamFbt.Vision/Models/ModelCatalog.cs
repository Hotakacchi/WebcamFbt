namespace WebcamFbt.Vision.Models;

/// <summary>
/// ダウンロードできるモデルの情報。
/// 配布元は OpenMMLab（MMPose）の公式サーバー。zip の中の end2end.onnx を取り出して使う。
/// </summary>
/// <param name="Id">保存するファイル名（拡張子なし）。</param>
/// <param name="DisplayName">表示名。</param>
/// <param name="Url">zip のダウンロード URL。</param>
/// <param name="Sha256">取り出した onnx の SHA-256（小文字 16 進）。null なら照合しない。</param>
/// <param name="License">ライセンス表記。</param>
public sealed record ModelInfo(string Id, string DisplayName, string Url, string? Sha256, string License)
{
    public string FileName => Id + ".onnx";
}

/// <summary>このアプリが使うモデルの一覧。</summary>
public static class ModelCatalog
{
    private const string OnnxSdkBase = "https://download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/";

    /// <summary>人物検出: YOLOX-tiny（HumanArt + COCO で学習、入力 416x416、人物のみ）。</summary>
    public static ModelInfo YoloxTinyHuman { get; } = new(
        "yolox_tiny_humanart",
        "YOLOX-tiny (HumanArt)",
        OnnxSdkBase + "yolox_tiny_8xb8-300e_humanart-6f3252f9.zip",
        Sha256: null, // TODO: 初回ダウンロードで確認した値を記入する
        "Apache-2.0 (MMPose / MMDetection)");

    /// <summary>姿勢推定: RTMPose-m（Halpe26 形式 26 点、入力 192x256）。</summary>
    public static ModelInfo RtmPoseMHalpe26 { get; } = new(
        "rtmpose_m_halpe26",
        "RTMPose-m (Halpe26)",
        OnnxSdkBase + "rtmpose-m_simcc-body7_pt-body7-halpe26_700e-256x192-4d3e73dd_20230605.zip",
        Sha256: null, // TODO: 初回ダウンロードで確認した値を記入する
        "Apache-2.0 (MMPose)");

    public static IReadOnlyList<ModelInfo> All { get; } = [YoloxTinyHuman, RtmPoseMHalpe26];
}
