using WebcamFbt.Core.Geometry;

namespace WebcamFbt.Core.Tracking;

/// <summary>
/// トラッキング計算の設定。UI から実行中に変更でき、次のフレームから反映される。
/// </summary>
public sealed class TrackingSettings
{
    /// <summary>身長 [m]。骨の長さの目安と、カメラからの距離の推定に使う。</summary>
    public double HeightMeters { get; set; } = 1.65;

    /// <summary>カメラの水平画角 [度]。分からなければ 70 前後。</summary>
    public double HorizontalFovDeg { get; set; } = 70;

    /// <summary>カメラの下向きの傾き [度]。</summary>
    public double CameraPitchDownDeg { get; set; }

    /// <summary>この信頼度未満のキーポイントは使わない。</summary>
    public float KeypointScoreThreshold { get; set; } = 0.4f;

    /// <summary>信頼度が低いキーポイントについて、直前の値を使い続ける最大時間 [秒]。</summary>
    public double KeypointHoldSeconds { get; set; } = 0.5;

    /// <summary>ステレオで、再投影誤差がこれ [px] より大きい関節は捨てる（2 台で別の点を指している）。</summary>
    public double StereoMaxReprojErrorPx { get; set; } = 15;

    /// <summary>位置の平滑化。</summary>
    public OneEuroParams PositionFilter { get; } = new(minCutoff: 1.5, beta: 1.0);

    /// <summary>回転の平滑化（速さは rad/s 単位）。</summary>
    public OneEuroParams RotationFilter { get; } = new(minCutoff: 1.0, beta: 0.3);

    /// <summary>カメラからの距離の平滑化（ブレやすいので強めにかける）。</summary>
    public OneEuroParams DepthFilter { get; } = new(minCutoff: 0.4, beta: 0.3);
}

/// <summary>
/// 身長に対する体の各部の長さの比率（キーポイント間の距離）。
/// Drillis &amp; Contini の人体寸法比を目安に、姿勢推定モデルのキーポイント位置に合わせて調整した値。
/// フェーズ 3 のキャリブレーションで、本人の実測値に置き換える予定。
/// </summary>
public sealed record BodyProportions(
    double ShoulderWidth = 0.22,
    double HipWidth = 0.14,
    double Torso = 0.30,      // 首（両肩の中点）〜 骨盤（両股関節の中点）
    double Thigh = 0.245,     // 股関節 〜 膝
    double Shin = 0.246,      // 膝 〜 足首
    double Foot = 0.15,       // かかと 〜 つま先
    double NeckToEars = 0.09) // 首 〜 両耳の中点（高さ方向）
{
    public static BodyProportions Default { get; } = new();
}
