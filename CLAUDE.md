# WebcamFbt — 開発メモ（Claude 向け）

Webカメラ（1〜2台）の映像から姿勢推定し、VRChat の OSC トラッカー（腰・両足）として送る Windows アプリ。
詳細は README.md と docs/design.md を参照。

## 開発ルール（ユーザー指定）

- コードのコメント・README・コミットメッセージは**日本語**
- コミットメッセージに `Co-Authored-By` の行は**入れない**
- **フェーズごとに止まって、ユーザーに動作確認を求める**。確認が取れたらフェーズ単位でコミット
- 使うモデル・ライブラリは配布して問題ないライセンスのものだけ（THIRD_PARTY_NOTICES.md に記載）。Ultralytics YOLO（AGPL）は使わない
- 推論・カメラ・OSC・UI は別スレッド。UI を固めない
- 姿勢推定は `IPoseEstimator2D` で抽象化し、差し替え可能に保つ

## 技術スタック（決定済み）

- .NET 10 / WPF + CommunityToolkit.Mvvm（.NET 8 は 2026-11 にサポート終了のため 10 を採用）
- ONNX Runtime DirectML 1.24.4（失敗時は CPU）。DirectML はセッションで EnableMemoryPattern=false / ORT_SEQUENTIAL が必須
- モデル: YOLOX-tiny（HumanArt, 人物検出）+ RTMPose-m Halpe26（26点）。初回起動時に OpenMMLab から自動 DL（%LOCALAPPDATA%\WebcamFbt\models）
- OpenCvSharp4 4.13.0.20260627、OSC は自作（src/WebcamFbt.Osc）

## ビルド・テスト

```powershell
dotnet test tests\WebcamFbt.Core.Tests -c Release
dotnet build src\WebcamFbt.App -c Release
dotnet run --project src\WebcamFbt.App -c Release
```

`_run.cmd`（テスト→ビルド→起動、ログは `_build.log`）はユーザーがダブルクリックで使う補助スクリプト。git 管理外。

## 進捗

- [x] フェーズ0: 設計
- [x] フェーズ1: カメラ1台の映像 + 2Dキーポイント表示、fps・推論時間表示（ユーザー確認済み・コミット済み）
- [x] フェーズ2: 腰・両足の位置/回転 → One Euro Filter → OSC 送信（カメラ1台）（ユーザー確認済み・コミット済み）
- [x] フェーズ3: キャリブレーション（身長、床の高さ、向き、T ポーズでのリセット。BodyProportions を実測値で置き換える）（ユーザー確認済み・コミット済み）
- [ ] **フェーズ4: カメラ2台のステレオキャリブレーション（チェッカーボード）と三角測量**
  - 設計は docs/design.md「ステレオ化（フェーズ4）」と下記「設計の要点（フェーズ4）」にまとめ済み（未実装）
  - サブステップ: 4a 2台同時プレビュー＋各カメラ2D表示 → 4b ステレオ校正＋保存 → 4c 三角測量で3D化・単眼/ステレオ切替 → 4d 床・向き校正のステレオ対応
  - [x] 4a: 実装・コミット済み。**1台モードはユーザー確認済み、2台モードは実機未検証**（ユーザーの Web カメラが 1 台のため）。
    2台目が用意できたら「左右に2映像＋各2D骨格・統計が出るか」を確認し、問題があれば直す
    （カメラ2は MainViewModel.Camera2.cs。別推定器で並行推論、解像度・fps・取得方式はカメラ1と共通、トラッキング/OSC はカメラ1のまま）
  - [x] 4b: 実装・コミット済み。**カメラ1台での校正はユーザー確認済み、ステレオ校正は実機未検証**（計算は合成データのテストで検算済み）。
    校正中はパイプラインの `FrameProcessor` でボード検出に切り替え、止まった・十分違う画像を 15 枚（ステレオは組）集めて計算。
    結果は `%LOCALAPPDATA%\WebcamFbt\calibration.json`（デバイス×解像度ごとの内部パラメータ＋ステレオ 1 組）。
    カメラ1の内部パラメータは単眼トラッキングにも使う（画角の近似の代わり＋キーポイントの歪み補正）
- [ ] フェーズ5: 設定の保存、UI の整備、self-contained 単一 exe の配布ビルド

## 設計の要点（フェーズ2）

- 座標: カメラ座標（OpenCV: +X右 +Y下 +Z奥）→ トラッキング座標（Unity: +X右 +Y上 +Z前、左手系、m）。
  Y反転 → カメラのピッチを戻す → Y軸180度回転（`CameraToTracking`）。+Z は「カメラを向いた人の前方」
- VRChat OSC（公式 docs: https://docs.vrchat.com/docs/osc-trackers）:
  - `/tracking/trackers/1..8/position|rotation`（float×3、m、オイラー角[度]、Z→X→Y の順で適用）
  - head/position は毎フレーム送信（VRChat が OSC 空間全体を頭に合わせる）
  - head/rotation は「向きを合わせる」ボタンで 1 回だけ (0,0,0) を送る（1回送信＝即時ヨー合わせ、300ms以内連続はストリーミング扱い）
- トラッカー番号: 1=腰、2=左足、3=右足
- 単眼 3D 化（`MonocularLifter`）: 骨長（身長×比率）と画像上の長さの比で距離、肩幅の縮みと鼻の位置で体のヨー、
  太もも・足の甲の縮みで膝・つま先の前後。背面や足を後ろに引く動きは原理的に不正確（フェーズ4で改善）
- オイラー変換（`Rotation.ToUnityEuler`）は Python で 1 万ケース検算済み

## 設計の要点（フェーズ3）

- キャリブレーション（`Calibrator` / `CalibrationData`）: カメラの方を向いて T ポーズで立ち、
  数フレーム（既定 90）の 3D 姿勢を貯めて確定する。
  - 比率: 各骨長（トラッキング座標での距離）の中央値 ÷ 身長で `BodyProportions` を実測し、固定値を置き換える
  - 床の高さ: 足（足首・かかと・つま先）の最低 Y の中央値を床面とし、出力位置から差し引いて「床 = Y0」に
  - 向き: 体ヨーの中央値を正面として記録し、腰を軸にその場で回して +Z 正面にそろえる（位置・回転の両方）
- 適用は `CalibrationData.Apply(TrackerFrame)`。未キャリブレーション時（`CalibrationData.None`）は固定値で従来通り動く
- キャリブレーションは推論スレッドでフレームを貯め、確定結果はフラグ経由で UI スレッド（`_statsTimer`）が受け取る
- 設定の永続化はフェーズ5。現状は再起動で消える（メモリ保持のみ）

## 設計の要点（フェーズ4）

詳細は docs/design.md「ステレオ化（フェーズ4）」。要点のみ:

- 2台のカメラで同じ関節を見て三角測量し、真の3D（m）を得る。単眼の弱点（背面・足を後ろ・距離誤差）を解消
- Core は OpenCV 非依存を維持。チェッカーボード検出・ステレオ校正は Vision 側（OpenCV）、
  三角測量の数式は検算しやすいよう純C#で Core 側（`StereoTriangulator`）
- `StereoCalibration`（Core: K1/dist1/K2/dist2/R/t/再投影誤差）。カメラ1が世界原点。
  再校正は手間なのでファイル保存（`%LOCALAPPDATA%\WebcamFbt`、永続化全般はフェーズ5）
- `StereoCameraPipeline`: カメラ×2・推定器×2（IFは1スレッド1インスタンス前提）。
  非同期カメラなのでタイムスタンプが近いペア（dt<~20ms）だけ対応付ける
- 既存後段（`Pose3D → TrackerSolver → OSC`）は無改修。`MonocularLifter` と `StereoTriangulator` を切替
- OpenCvSharp の `FindChessboardCornersSB` / `StereoCalibrate` / `UndistortPoints` を活用
- サブステップ 4a〜4d で止まって確認（上記「進捗」参照）

## フォルダ構成

```
src/WebcamFbt.App     WPF（MainViewModel.cs = カメラ/推論、MainViewModel.Tracking.cs = トラッキング/OSC）
src/WebcamFbt.Core    Geometry（フィルタ・回転・カメラモデル）、Tracking、Pose、Threading
src/WebcamFbt.Vision  カメラ、ONNX 推論（YOLOX / RTMPose / TopDownPoseEstimator）、描画、パイプライン
src/WebcamFbt.Osc     OSC エンコーダと VrcTrackerSender
tests/WebcamFbt.Core.Tests  xUnit（OpenCV の校正計算も合成データで検算するため Vision を参照し、net10.0-windows）
```

注意: 名前空間 `WebcamFbt.Core.Math` は `System.Math` と衝突するので使わない（Geometry にした）。
