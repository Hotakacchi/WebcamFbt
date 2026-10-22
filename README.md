# WebcamFbt

Webカメラ（1〜2台）の映像から姿勢を推定し、VRChat の OSC トラッカーとして腰・両足の位置と回転を送る Windows 向けアプリです。

> **現在の状態: フェーズ1**（カメラ1台の映像に 2D キーポイントを重ねて表示）。まだ VRChat には送信しません。

## 動作環境

- Windows 10 / 11（64bit）
- DirectX 12 対応の GPU（推奨）。使えない場合は CPU で動作します（遅くなります）
- Webカメラ（720p / 60fps 対応だと遅延が少なくなります）
- ビルドする場合: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## ビルドと実行

```powershell
git clone <このリポジトリ>
cd WebcamFbt
dotnet run --project src/WebcamFbt.App -c Release
```

Visual Studio 2026 では `WebcamFbt.slnx` を開いて実行できます。

### モデルファイル

初回の「開始」時に、次のモデルを OpenMMLab の公式サーバーから自動でダウンロードします（合計 約 70MB）。

| 用途 | モデル | 入力サイズ |
|---|---|---|
| 人物検出 | YOLOX-tiny（HumanArt + COCO で学習） | 416×416 |
| 姿勢推定 | RTMPose-m（Halpe26 形式 26 点、body7 で学習） | 192×256 |

保存先は `%LOCALAPPDATA%\WebcamFbt\models\` です。
ネットに繋がらない環境では、zip を手動でダウンロードして中の `end2end.onnx` を次の名前で `exe と同じフォルダの models\` に置いてください。

- `yolox_tiny_humanart.onnx` ← `yolox_tiny_8xb8-300e_humanart-6f3252f9.zip`
- `rtmpose_m_halpe26.onnx` ← `rtmpose-m_simcc-body7_pt-body7-halpe26_700e-256x192-4d3e73dd_20230605.zip`

（URL の場所: `https://download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/`）

## 使い方（フェーズ1）

1. 右側でカメラ番号・解像度・fps を選ぶ
2. 「開始」を押す（初回はモデルのダウンロードと GPU の初期化で少し時間がかかります）
3. 映像に骨格が重なって表示されます。左上に fps と処理時間が出ます

| 表示 | 意味 |
|---|---|
| カメラ | カメラから実際に届いているフレーム数／秒 |
| 処理 | 姿勢推定が終わったフレーム数／秒 |
| 検出 | 人物検出にかかった時間（人を見失ったときと、60 フレームごとにだけ実行） |
| 姿勢 | キーポイント推定にかかった時間 |
| 遅延 | フレームを受け取ってから推論が終わるまで（カメラ内部の遅延は含まない） |
| 実行 | 使われている推論デバイス（DirectML = GPU / CPU） |

うまく映らないときは「取得方式」を DirectShow に変えるか、解像度・fps を下げてみてください。

## 構成

```
src/
├─ WebcamFbt.App/     WPF の画面（View / ViewModel）
├─ WebcamFbt.Core/    外部ライブラリに依存しない部分（姿勢データ、統計、スレッド間の受け渡し）
└─ WebcamFbt.Vision/  カメラ取得、ONNX 推論、描画、処理パイプライン
```

処理の流れ:

```
[カメラスレッド] ─最新フレーム→ [推論スレッド] 検出→切り出し→キーポイント推定→描画 ─最新プレビュー→ [UI]
```

- 各スレッド間は「最新の 1 個だけ」を受け渡すので、処理が追いつかなくても遅延がたまりません
- 人物検出は重いので毎フレームは動かさず、前フレームのキーポイントから次の切り出し範囲を決めます
- 姿勢推定は `IPoseEstimator2D` で抽象化してあり、BlazePose などに差し替えられます

## 開発の予定

- [x] フェーズ0: 設計
- [x] フェーズ1: カメラ1台の映像と 2D キーポイントの表示、fps・推論時間の表示
- [ ] フェーズ2: 腰と両足の位置・回転を計算し、One Euro Filter をかけて OSC で送信
- [ ] フェーズ3: キャリブレーション（身長、床の高さ、向き、T ポーズでのリセット）
- [ ] フェーズ4: カメラ2台のステレオキャリブレーションと三角測量
- [ ] フェーズ5: 設定の保存、UI の整備、単一 exe での配布

## ライセンス

このソフトウェアのソースコードは [MIT License](LICENSE) です。

使用しているライブラリとモデルのライセンスは [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。
**特に、姿勢推定モデルの学習データには非商用条件のデータセットが含まれます。** 詳しくは同ファイルの注意事項を読んでください。
