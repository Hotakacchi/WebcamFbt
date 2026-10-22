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
| ライセンス | MIT | |

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
