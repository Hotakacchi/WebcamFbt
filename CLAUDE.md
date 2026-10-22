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
- [ ] **フェーズ2: 腰・両足の位置/回転 → One Euro Filter → OSC 送信（カメラ1台）**
  - コードは書き終えているが **未ビルド・未テスト・未コミット**（作業ツリーに変更が残っている）
  - 次にやること: `dotnet test` と `dotnet build` を通す → 起動してユーザーに確認依頼 → OK ならコミット
  - 確認してほしい点: 左下にトラッカー座標が出るか、動きに追従するか、VRChat で腰・足が動くか
- [ ] フェーズ3: キャリブレーション（身長、床の高さ、向き、T ポーズでのリセット。BodyProportions を実測値で置き換える）
- [ ] フェーズ4: カメラ2台のステレオキャリブレーション（チェッカーボード）と三角測量
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

## フォルダ構成

```
src/WebcamFbt.App     WPF（MainViewModel.cs = カメラ/推論、MainViewModel.Tracking.cs = トラッキング/OSC）
src/WebcamFbt.Core    Geometry（フィルタ・回転・カメラモデル）、Tracking、Pose、Threading
src/WebcamFbt.Vision  カメラ、ONNX 推論（YOLOX / RTMPose / TopDownPoseEstimator）、描画、パイプライン
src/WebcamFbt.Osc     OSC エンコーダと VrcTrackerSender
tests/WebcamFbt.Core.Tests  xUnit
```

注意: 名前空間 `WebcamFbt.Core.Math` は `System.Math` と衝突するので使わない（Geometry にした）。
