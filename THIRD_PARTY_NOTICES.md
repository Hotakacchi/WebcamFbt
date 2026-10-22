# サードパーティのライセンス

WebcamFbt は以下のライブラリとモデルを使用しています。

## ライブラリ

| 名前 | 用途 | ライセンス |
|---|---|---|
| [ONNX Runtime](https://github.com/microsoft/onnxruntime)（Microsoft.ML.OnnxRuntime.DirectML） | 推論 | MIT |
| [DirectML](https://github.com/microsoft/DirectML) | GPU 推論（ONNX Runtime に同梱） | MIT |
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) | カメラ入力・画像処理 | Apache-2.0 |
| [OpenCV](https://opencv.org/)（OpenCvSharp4.runtime.win に同梱） | 同上 | Apache-2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM | MIT |

## モデル（実行時にダウンロード。このリポジトリと配布物には含まない）

| 名前 | 配布元 | ライセンス |
|---|---|---|
| RTMPose-m（body7 / Halpe26） | [MMPose](https://github.com/open-mmlab/mmpose)（OpenMMLab） | Apache-2.0 |
| YOLOX-tiny（HumanArt） | [MMPose](https://github.com/open-mmlab/mmpose) / [YOLOX](https://github.com/Megvii-BaseDetection/YOLOX) | Apache-2.0 |

前処理・後処理は [rtmlib](https://github.com/Tau-J/rtmlib)（Apache-2.0）と MMPose の実装を参考にしています（コードの直接の移植ではありません）。

### 注意事項（学習データ）

モデルの重み自体は Apache-2.0 で公開されていますが、学習に使われたデータセットの一部
（例: AI Challenger、Halpe、HumanArt など）には、研究・非商用目的に限る利用条件が付いているものがあります。

- 本プロジェクトは **非商用の無料ソフトウェア** として配布することを前提にしています
- モデルは配布物に同梱せず、利用者の PC で公式サーバーから直接ダウンロードする方式にしています
- 商用で利用・再配布する場合は、各データセットの利用条件を各自で確認してください

## 使わないもの

- Ultralytics YOLO（v5/v8/v11 など）: AGPL-3.0 のため使用しません
