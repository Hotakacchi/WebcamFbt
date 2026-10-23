# WebcamFbt — 開発メモ（Claude 向け）

Webカメラ（1〜2台）の映像から姿勢推定し、VRChat の OSC トラッカー（腰・両足）として送る Windows アプリ。
詳細は README.md と docs/design.md を参照。

## 開発ルール（ユーザー指定）

- コードのコメント・README・コミットメッセージは**日本語**
- コミットメッセージに `Co-Authored-By` の行は**入れない**
- **フェーズごとに止まって、ユーザーに動作確認を求める**。確認が取れたらフェーズ単位でコミット
- 使うモデル・ライブラリは配布して問題ないライセンスのものだけ（THIRD_PARTY_NOTICES.md に記載）。Ultralytics YOLO（AGPL）は使わない
- WebcamFbt 自身のライセンスは **GPL-3.0-or-later**（LICENSE）＋ DirectML との組み合わせを認める追加の許可（LICENSE-EXCEPTION.txt）。
  ライブラリを足すときは GPLv3 と両立するか確かめる（MIT・BSD・Apache-2.0・LGPL は可。GPLv2 のみ・独自ライセンスは不可か、例外の追記が要る）
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
ビルドの前に `WebcamFbt.exe --exit` で、動いている WebcamFbt（トレイに隠れていても）に設定を保存して終了するよう頼む
（× ではトレイに入って終わらないので、残っていると DLL を上書きできずビルドが失敗するため）。終わらなければ強制終了はせず、トレイから終了するよう案内して待つ。

## 進捗

- [x] フェーズ0: 設計
- [x] フェーズ1: カメラ1台の映像 + 2Dキーポイント表示、fps・推論時間表示（ユーザー確認済み・コミット済み）
- [x] フェーズ2: 腰・両足の位置/回転 → One Euro Filter → OSC 送信（カメラ1台）（ユーザー確認済み・コミット済み）
- [x] フェーズ3: キャリブレーション（身長、床の高さ、向き、T ポーズでのリセット。BodyProportions を実測値で置き換える）（ユーザー確認済み・コミット済み）
- [x] フェーズ4: カメラ2台のステレオキャリブレーション（チェッカーボード）と三角測量（4a〜4d 実装・コミット済み）
  - **残作業: 2台目のカメラでの実機確認**（ユーザーの Web カメラが 1 台のため、2台の経路はすべて合成データのテストでのみ検算済み）。
    2台そろったら 4a（2映像）→ 4b（ステレオ校正）→ 4c（三角測量）→ 4d（傾き補正・精度表示）の順に確認し、問題があれば直す
  - 設計は docs/design.md「ステレオ化（フェーズ4）」と下記「設計の要点（フェーズ4）」
  - サブステップ: 4a 2台同時プレビュー＋各カメラ2D表示 → 4b ステレオ校正＋保存 → 4c 三角測量で3D化・単眼/ステレオ切替 → 4d 床・向き校正のステレオ対応
  - [x] 4a: 実装・コミット済み。**1台モードはユーザー確認済み、2台モードは実機未検証**（ユーザーの Web カメラが 1 台のため）。
    2台目が用意できたら「左右に2映像＋各2D骨格・統計が出るか」を確認し、問題があれば直す
    （カメラ2は MainViewModel.Camera2.cs。別推定器で並行推論、解像度・fps・取得方式はカメラ1と共通、トラッキング/OSC はカメラ1のまま）
  - [x] 4b: 実装・コミット済み。**カメラ1台での校正はユーザー確認済み、ステレオ校正は実機未検証**（計算は合成データのテストで検算済み）。
    校正中はパイプラインの `FrameProcessor` でボード検出に切り替え、止まった・十分違う画像を 15 枚（ステレオは組）集めて計算。
    結果は `%LOCALAPPDATA%\WebcamFbt\calibration.json`（デバイス×解像度ごとの内部パラメータ＋ステレオ 1 組）。
    カメラ1の内部パラメータは単眼トラッキングにも使う（画角の近似の代わり＋キーポイントの歪み補正）
  - [x] 4c: 実装・コミット済み。**1台モードは確認済み、ステレオの経路は実機未検証**（合成データのテストで検算済み）。
    2台＋ステレオ校正あり＋「三角測量で3D化」オンで自動的にステレオ。`StereoFrameMatcher` で撮影時刻差 25ms 以内を組にし、
    `TrackingProcessor.ProcessStereo` → `StereoTriangulator`（歪み補正→線形三角測量、再投影誤差 15px 超は捨てる）→ 単眼と共通の後段。
    2本の推論スレッドから呼ぶので VM 側で lock。切替時は `RequestReset` でフィルタをリセット。
    弱点: 横並びの2台では「同じ高さの左右」を片方のカメラだけ取り違えても見分けられない（左右で高さが違うときだけ入れ替えで直す）
  - [x] 4d: 実装・コミット済み。**1台モードは確認済み、ステレオの部分は実機未検証**（合成データのテストで検算済み）。
    ステレオで体のキャリブレーションをすると、体の縦の向き（足首の中点→首）を真上に重ねる回転でカメラのピッチ・ロールを補正
    （`CalibrationData.Tilt`。全フレームがステレオのときだけ。適用順は 傾き→ヨー→床）。最小の回転なので縦軸まわりのねじれが少し残るが、
    ヨー補正が打ち消す。精度の目安 `StereoStats`（三角測量できた点数・再投影誤差・左右の脚の長さ差・左右入替）を左下に表示
- [ ] **フェーズ5: 設定の保存、UI の整備、self-contained 単一 exe の配布ビルド**
  - サブステップ: 5a 設定の保存 → 5b UI の整備（内容はユーザーと相談して決める）→ 5c 配布ビルド
  - [x] 5a: 実装・コミット済み（ユーザー確認済み）。`%LOCALAPPDATA%\WebcamFbt\settings.json`（`Core/Persistence`）。
    画面の設定＋体のキャリブレーション結果を保存し、起動時に復元。変更の 1 秒後と終了時に保存。
    OSC の送信先は「反映」に成功したものだけ保存。保存対象を増やすときは `AppSettings`・`PersistedProperties`・
    `ApplySettings`・`BuildSettings` の 4 か所に足す
  - [x] 5b: 実装・コミット済み（ユーザー確認済み）。右パネルを「カメラ／トラッキング／校正／VRChat」のタブに分割
    （タブ番号は MainViewModel.Guide.cs の定数と XAML の並びをそろえる）、手順ガイド（済み✓・次を強調・押すとタブ移動）、
    腰・両足・頭・OSC の色付き状態表示、上から見たトラッカーの図（`Views/TrackerTopDownView`）
  - [x] 5c: 実装・コミット済み。`scripts\build-release.ps1` でテスト→単一 exe（SingleFile.pubxml）→ Inno Setup（installer\WebcamFbt.iss）。
    DirectML.dll は App から Microsoft.AI.DirectML を直接参照して同梱（以前は同梱されず Windows のものが使われていた）。
    アイコンはユーザー提供の絵（Assets\icon-source.png）から scripts\make-icon.ps1 で作る（白い余白を角丸で切り抜いて透明に）。
    .iss と .ps1 は BOM 付き UTF-8 にする。v0.1.0 を GitHub Releases にプレリリースとして公開（ステレオは実機未検証のため）。
    確認待ち: 利用者データ（settings.json など）が %LOCALAPPDATA%\WebcamFbt に保存されているか（Claude のシェルからは見えなかった）

## Mac 版（Avalonia）

詳細は docs/mac.md。Windows 版（WPF）は変えずに残し、`src/WebcamFbt.Mac` を足す。Core・Osc・Vision を共有。
UI は**Windows 版とできるだけ同じ見た目にする**（ユーザー指定。並び・文言・色をそろえる）。
ユーザーは Mac を持っていないので、確認は友達の Mac（チップ不明）で行う。ここで確かめられるのはビルドと合成データのテストまで。

- [ ] M1: Vision の 2 ターゲット化（Mac は ONNX Runtime 1.23.2 + CoreML/CPU、OpenCvSharp 公式 osx ランタイムで AVFoundation）、
  Avalonia の画面（カメラ 1 台の映像＋2D 骨格＋統計）、実行中のスリープ・App Nap 防止（`Platform/SleepBlocker`）、配布は GitHub Actions（`.github/workflows/mac-pkg.yml`、macOS ランナー）で
  arm64/x64 両方入りの .pkg（ad-hoc 署名済み、`--self-test` とインストールの自動チェック付き）。
  `v*` タグの push で同じ版の GitHub Release に .pkg を載せる（v0.1.0 には手で追加済み）。作業は main で行う（ユーザー指定）。実装済み・友達の Mac で確認待ち
- [ ] M2+M3（まとめて実施）: Windows 版の全機能（トラッキング・OSC、体のキャリブレーション、設定の保存、校正、2 台、
  手順ガイド・状態表示・上から見た図、閉じても動き続ける＝メニューバー）とアプリのアイコン。実装済み・友達の Mac で確認待ち。
  **ViewModel の Tracking / CameraCalibration / Guide / Settings と AppInfo.cs は Windows 版のファイルを Mac 版でもコンパイルして共有**
  （WPF の型を使わない。Settings のタイマーは `#if WINDOWS`）。MainViewModel.cs と Camera2.cs は Mac 版に写してあるので、
  Windows 版のこれらを変えたら Mac 版（src/WebcamFbt.Mac/ViewModels）にも同じ変更を入れる。画面（XAML）も同様

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
- 床の面を使う単眼 3D 化（後から追加・ユーザー確認済み）: 単眼の体のキャリブレーションで、立ち姿の 2D からカメラの高さ・下向きの傾きを
  求め（`CameraPoseEstimator`）、`MonoCalibration` で床の面を使って測り直す（ありえない比率は既定値に戻して注意を出す）。
  以後は `CalibrationData.Camera` があれば `MonocularLifter.LiftOnFloor`（床に着いた足＝視線と床の交点、ほかの関節はその奥行きの面、
  座標は床が Y0）。向きは 20° の不感帯・鼻のずれのヒステリシス・平滑化。写り方の注意は `TrackingProcessor.SetupWarnings`

## 設計の要点（フェーズ3）

- キャリブレーション（`Calibrator` / `CalibrationData`）: カメラの方を向いて T ポーズで立ち、
  数フレーム（既定 90）の 3D 姿勢を貯めて確定する。
  - 比率: 各骨長（トラッキング座標での距離）の中央値 ÷ 身長で `BodyProportions` を実測し、固定値を置き換える
  - 床の高さ: 足（足首・かかと・つま先）の最低 Y の中央値を床面とし、出力位置から差し引いて「床 = Y0」に
  - 向き: 体ヨーの中央値を正面として記録し、腰を軸にその場で回して +Z 正面にそろえる（位置・回転の両方）
- 適用は `CalibrationData.Apply(TrackerFrame)`。未キャリブレーション時（`CalibrationData.None`）は固定値で従来通り動く
- キャリブレーションは推論スレッドでフレームを貯め、確定結果はフラグ経由で UI スレッド（`_statsTimer`）が受け取る
- 結果は設定ファイルに保存し、起動時に復元する（フェーズ5a）

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
