# 設計メモ

## 決定事項（フェーズ0）

| 項目 | 決定 | 理由 |
|---|---|---|
| ランタイム | .NET 10（LTS） | .NET 8 は 2026-11-10 にサポート終了予定のため |
| UI | WPF + CommunityToolkit.Mvvm | Windows 専用（DirectML）なので Avalonia の利点が薄い。情報が多い |
| 推論 | ONNX Runtime + DirectML、失敗時は CPU | DirectML は保守のみの扱い（新機能は WinML）だがサポートは継続 |
| 人物検出 | YOLOX-tiny（HumanArt） | 軽い。Apache-2.0。見失ったときだけ実行 |
| 姿勢推定 | RTMPose-m Halpe26 | 足先（かかと・つま先）があり足の回転を出せる |
| OSC | 自作 | float×3 を送るだけなので依存を増やさない |
| モデル配布 | 初回ダウンロード | 学習データの利用条件に配慮し、再配布しない |
| ライセンス | GPL-3.0-or-later（DirectML との組み合わせを認める追加の許可付き） | 公開にあたり MIT から変更（改変して配る人にもソースの公開を求めるため）。GPLv2 は Apache-2.0 の OpenCV と両立しないので v3。DirectML.dll は GPL と両立しない独自ライセンスなので、GPLv3 第 7 条の追加の許可を LICENSE-EXCEPTION.txt に書いた |

## DirectML の注意点（公式ドキュメントより）

- セッション作成時に `EnableMemoryPattern = false`、`ExecutionMode = ORT_SEQUENTIAL` が必須
- 1 つのセッションの `Run` は同時に 1 スレッドからしか呼べない → カメラごとに推定器を分ける

## VRChat OSC トラッカー（公式ドキュメントより）

- `/tracking/trackers/1〜8/position`、`/rotation`（float×3）
- `/tracking/trackers/head/position`、`/rotation`（位置合わせ用）
- 単位 1.0 = 1m、Unity 座標系（左手系、+Y が上）、回転はオイラー角（度）
- 回転は Z → X → Y の順に適用してクォータニオンにする
- head の position: 毎フレーム、補間なしで OSC 空間全体をアバターの頭ボーンに合わせる
- head の rotation: 1 回だけ送ると即座にヨーを合わせる。300ms 以内に続けて送るとストリーミング扱い（補間、10 秒でタイムアウト）
- 方針: position は毎フレーム送る。rotation は T ポーズのリセット時に 1 回だけ送る

## RTMPose の前処理・後処理

- 矩形を 1.25 倍に広げ、192:256 の縦横比に合わせてアフィン変換で切り出す
- BGR → RGB、mean = (123.675, 116.28, 103.53)、std = (58.395, 57.12, 57.375)
- 出力 simcc_x [1,K,384]、simcc_y [1,K,512]。argmax / 2.0 が入力画像上の座標、信頼度は x と y の最大値の小さい方

## YOLOX（mmdeploy 形式）の前処理・後処理

- 縦横比を保って縮小、左上に寄せて右と下を 114 で埋める。正規化なし、BGR のまま
- 出力 dets [1,N,5]（x1,y1,x2,y2,score）、labels [1,N]。NMS はモデルに含まれる

## 座標系（フェーズ2）

- カメラ座標（OpenCV）: +X 右、+Y 下、+Z 奥
- トラッキング座標（VRChat に送る）: +X 右、+Y 上、+Z 前（Unity と同じ左手系）、m
  - 「カメラの方を向いて立った人」から見た向き。+Z = 人からカメラへ向かう方向
  - 変換: Y 反転 → カメラの傾きを戻す（X 軸回転）→ Y 軸まわりに 180 度
  - 原点はカメラ。VRChat は head の位置で OSC 空間全体を合わせるので、原点はどこでもよい
- 「向きを合わせる」: head の rotation (0,0,0) を 1 回だけ送る → 今の HMD の向きが「カメラを向いた向き」になるよう VRChat が回転させる

## オイラー角

- VRChat は Z → X → Y の順に適用（M = Ry · Rx · Rz、Unity の Quaternion.Euler と同じ）
- 回転行列の列 = (右, 上, 前) とすると
  - x = asin(-前.y)、y = atan2(前.x, 前.z)、z = atan2(右.y, 上.y)
  - |前.y| ≈ 1 のとき z = 0、y = atan2(-右.z, 右.x)

## ステレオ化（フェーズ4）

単眼（フェーズ2/3）の原理的な弱点——背面・足を後ろに引く動き・骨長仮定による距離誤差——を、
2 台のカメラで同じ関節を見て三角測量し、真の 3D（メートル）を得ることで解消する。校正はチェッカーボードで行う。

### 全体の流れ

```
[カメラ1] → 2D推定 ┐
                   ├→ タイムスタンプで対応付け → 三角測量 → Pose3D → TrackerSolver → OSC
[カメラ2] → 2D推定 ┘                                  （フェーズ2/3の後段はそのまま）
```

### コンポーネント分担

Core は OpenCV 非依存を保つ。チェッカーボード検出・ステレオ校正は OpenCV を持つ Vision 側、
三角測量の数式は検算しやすいよう純 C# で Core 側に置く。

- `StereoCalibrator`（Vision / OpenCV）: 両カメラで同時にチェッカーボードを写し、
  `Cv2.FindChessboardCornersSB` + `CornerSubPix` でコーナー検出。良好なペアを N 組（15〜20）集め、
  `Cv2.CalibrateCamera`（各カメラの内部パラメータ＋歪み）→ `Cv2.StereoCalibrate` で相対姿勢 R, t を推定。
  再投影誤差を品質指標として表示。カメラ1を世界原点（基準）とする。
- `StereoCalibration`（Core）: `K1, dist1, K2, dist2, R, t, ReprojError`。既存 `CameraIntrinsics` を歪み対応に拡張 or 併用。
  再校正は手間なのでファイル保存（`%LOCALAPPDATA%\WebcamFbt`）。設定全般の永続化はフェーズ5だが、ステレオ校正だけ先行保存。
- `StereoTriangulator`（Core / 純C#）: 両視点で信頼度の高い関節について、歪み除去 → 射影行列
  `P1=K1[I|0]`, `P2=K2[R|t]` で DLT 三角測量 → カメラ1座標の 3D 点。
  片方しか見えない関節は単眼リフト（フェーズ2）にフォールバック or ホールド。
  得た 3D をトラッキング座標へ（`CameraToTracking` 相当＋フェーズ3の床・向き校正を再利用）。
- `StereoCameraPipeline`（Vision）: `OpenCvCameraSource` ×2、`IPoseEstimator2D` ×2
  （IF は 1 スレッド 1 インスタンス前提なので推定器も 2 つ＝RTMPose セッション 2 つぶんの負荷）。
  2 本の推論スレッド。各 `Pose2D` をタイムスタンプ付きで小さなリングに保持し、
  最も近い時刻同士（dt < ~20ms）で対応付ける。大きくズレたペアは破棄（ハード同期がないため）。
- 既存後段（`Pose3D → TrackerSolver → VrcTrackerSender`）は無改修。
  単眼 `MonocularLifter` とステレオ `StereoTriangulator` を切り替える構造にする。

### UI 追加

- カメラ2の選択（デバイス/解像度/取得方式）
- モード切替: 1台（単眼）/ 2台（ステレオ）
- ステレオ校正フロー: 両カメラ映像＋検出コーナー重畳、安定したら自動キャプチャ、
  カウンタ（例 15/20）、完了で再投影誤差表示・保存
- チェッカーボード設定: 内側コーナー数（列×行）、マス一辺 [mm]

### 主な課題

- 非同期カメラ: 時刻マッチングで吸収。速い動きでペア誤差が出るので dt 制限
- 負荷: 推定器2つ＝GPU/VRAM 増。DirectML 制約（EnableMemoryPattern=false / ORT_SEQUENTIAL）は既存どおり
- 人物対応: 単一人物前提（各カメラで最高スコアの検出を採用）。複数人は対象外
- OpenCvSharp の `FindChessboardCornersSB` / `StereoCalibrate` / `UndistortPoints` を活用し手実装を最小化
- ライセンス: OpenCV（Apache-2.0）/ OpenCvSharp / 自作数式のみ。AGPL 不使用——配布制約なし

### サブステップ（各ステップで止まって確認）

- 4a: 2台同時プレビュー＋各カメラの2D推定表示（三角測量なし）
- 4b: チェッカーボードでステレオ校正（R,t＋再投影誤差）＋保存
- 4c: 三角測量で3D化 → 既存後段へ。単眼/ステレオ切替
- 4d: 床・向き校正のステレオ対応と精度確認

## 将来の検討: Mac 対応（Avalonia + CoreML）

Windows を持たず Mac を使う人向け。Mac でこのアプリを動かし、OSC を Quest 単体（スタンドアロン）の
VRChat に飛ばせば、Windows なしで FBT ができる（VRChat 自体は Mac では動かないのでプレイは Quest 側）。

現在の構成は OS 依存を UI・推論 EP・カメラ取得の 3 点に閉じ込めてあるので、“作り直し”ではなく
“フロントの載せ替え”で済む見込み。すぐ着手する話ではなく、Windows 版をフェーズ5まで固めてから検討する。

- そのまま流用: `Core`（Geometry/Tracking/Pose、純 C#）、`Osc`（UDP）、.NET 10（macOS / Apple Silicon 対応）
- 作り替え:
  - UI: WPF → **Avalonia**（クロスプラットフォーム XAML）。CommunityToolkit.Mvvm はそのまま使えるので ViewModel は流用
  - 推論 EP: ONNX Runtime **DirectML → CoreML EP**（Apple GPU/Neural Engine）または CPU。
    `OnnxSessionFactory` に EP ごとの分岐が要る（DirectML 専用の EnableMemoryPattern=false / ORT_SEQUENTIAL は CoreML には不要／別制約）
  - カメラ取得: 最大の難所。OpenCvSharp4 は Apple Silicon ネイティブの提供が弱い。
    `OpenCvCameraSource` を `ICameraSource` 的に抽象化し、AVFoundation ベースの Mac 専用実装を差し替える案。
    RTMPose の前処理（アフィン切り出し等）も OpenCV 依存なので、OpenCV 非依存化 or Mac で動く置き換えを検討
- 進め方の目安: ①Windows 版をフェーズ5まで → ②Avalonia 版を Core/Osc 共有で追加 → ③推論 EP 抽象化 → ④カメラ取得抽象化＋Mac 実装
