# Mac 版（開発メモと確認手順）

Windows を持たず Mac を持つ人が、Mac でこのアプリを動かし、OSC を Quest 単体の VRChat に送れるようにするためのもの。
Windows 版（`src/WebcamFbt.App`、WPF）はそのまま残し、Avalonia のアプリ `src/WebcamFbt.Mac` を別に足している。

## 構成

- 共有: `WebcamFbt.Core`（姿勢・3D 化・三角測量・キャリブレーション・設定の保存）、`WebcamFbt.Osc`、`WebcamFbt.Vision`
- `WebcamFbt.Vision` は `net10.0-windows`（Windows 版）と `net10.0`（Mac 版）の 2 つ向けにビルドする
  - Windows 向け: ONNX Runtime DirectML 1.24.4 + `OpenCvSharp4.runtime.win`（従来どおり）
  - Mac 向け: ONNX Runtime 1.23.2 + `OpenCvSharp4.runtime.osx.arm64` / `osx.x64`
    - ONNX Runtime は 1.24 から Intel Mac（osx-x64）のネイティブが無くなったので、両方そろう 1.23.2 にしている
    - OpenCvSharp の公式 macOS 版は AVFoundation（カメラ）・calib3d（校正）入りで、システムのフレームワーク以外に依存しない。
      そのためカメラ取得や前処理を作り直す必要はなく、`OpenCvCameraSource` は Mac では AVFoundation を使うだけ
- 推論（`OnnxSessionFactory`）: Mac は CoreML 実行プロバイダー（MLProgram 形式、CPU/GPU/Neural Engine 全部）を試し、
  失敗したら CPU。DirectML 用の `EnableMemoryPattern=false` は CoreML には付けない。CoreML への変換結果は
  `~/Library/Application Support/WebcamFbt/coreml-cache` に置き、2 回目以降の起動を速くする
- 画面: Windows 版と同じ並び・文言・色（右に 330px の設定パネルとタブ、左にプレビューと統計）。
  ViewModel は Windows 版を写して Avalonia 向けに直したもの（Windows 版に手を入れないため共通化はしていない）
- データの置き場所: `~/Library/Application Support/WebcamFbt`（.NET の LocalApplicationData。モデル・設定・error.log）
- バックグラウンド: Quest で遊ぶ間、Mac のアプリは裏に回って放置される。カメラ・推論・OSC は UI と別スレッドなので止まらないが、
  macOS の省電力で止まらないよう 2 段構えにしている
  - 実行中（「開始」〜「停止」）は `Platform/SleepBlocker` が `NSProcessInfo beginActivityWithOptions:reason:`
    （UserInitiated = App Nap 無効＋操作が無くてもスリープしない、LatencyCritical）を出す。画面が消えるのは止めない
  - 念のため Info.plist にも `NSAppSleepDisabled`（App Nap を止める古い設定）
  - ふたを閉じる・手動でスリープさせる・電池切れは防げない（そのときは止まる）

## 配布物: インストーラ（.pkg）— GitHub Actions の macOS ランナーで作る

`.github/workflows/mac-pkg.yml` が macOS 15（Apple Silicon）のランナーで `scripts/build-mac-pkg.sh` を動かす。
動かすタイミングは、手動（Actions タブ → 「Mac 版インストーラ」→ Run workflow）と `v*` タグの push。
`v*` タグのときは同じ名前の GitHub Release に .pkg を載せる（先に Release を作っておく。Windows 版の配布物と同じ Release に並ぶ）。
非公開リポジトリでは macOS の時間が 10 倍で数えられる（無料枠は実質 月 200 分ほど）ので、main への push では動かさない。
できた .pkg は、その実行の「Artifacts」の `WebcamFbt-mac-pkg`（zip で包まれている）からダウンロードする。

- `build-mac-pkg.sh`: osx-arm64 / osx-x64 を publish → `.app` を組み立て → **ad-hoc 署名**（`codesign --sign -`）→
  `pkgbuild`（/Applications に入れる。BundleIsRelocatable=NO で、別の場所の同じアプリを上書きしない）→
  `productbuild` で 1 つの `WebcamFbt-<版>-mac.pkg` に
  - 中身は Apple Silicon 用と Intel 用の 2 つ。`distribution.xml` の JavaScript（`hw.optional.arm64`）で Mac のチップに合う方だけ入れる
  - `hostArchitectures="x86_64,arm64"` が無いと、Apple Silicon で Rosetta のインストールを求められる
  - preinstall で前の版の .app を消す（設定・モデルは Application Support なので残る）。postinstall で quarantine を外す
  - 最低 macOS は 13.4（ONNX Runtime 1.23.2 の dylib の要求。.NET 10 の公式サポートは 15 以降）
- ワークフローの自動チェック: 各 .app の `--self-test`（OpenCV の AVFoundation 有無、モデルのダウンロード、CPU と CoreML で
  推論して結果の差を表示。Intel 用は Rosetta で）→ `sudo installer` で実際にインストールして署名を検証・自己診断 →
  画面つきで 20 秒起動して落ちないか。カメラは無いので確かめられない
- 自己診断で見つかって直したこと
  - CoreML: YOLOX の後段（人数ぶんの箱）は人がいないと 0 要素になり、CoreML では「0 要素は非対応」で推論が失敗した。
    `RequireStaticInputShapes=1` で、形が実行時に決まる演算は CPU に回す
  - 公式の Intel 版 OpenCV（`OpenCvSharp4.runtime.osx.x64`）は Homebrew の libavif（`/usr/local/opt/libavif/...`）を参照していて、
    Homebrew の無い Mac では読み込めない。`scripts/weaken-dylib-deps.py` で弱いリンク（無くても読み込める）に書き換えてから署名する。
    AVIF は使わないので影響なし。Apple Silicon 版にはこの依存は無い
  - ランナーは仮想マシンで GPU / Neural Engine が使えないため、CoreML でも速さは CPU とほぼ同じ。実機の速さは友達の Mac で見る
- Apple の署名・公証はしていないので、.pkg を最初に開くときに Gatekeeper の確認が出る（右クリック →「開く」、
  macOS 15 以降は システム設定 → プライバシーとセキュリティ →「このまま開く」）。インストール後のアプリは
  ad-hoc 署名済みで quarantine も無いので、ターミナルの操作は要らない

## ビルド（Windows 上でできる）

```powershell
dotnet build src\WebcamFbt.Mac -c Release          # ビルドだけ
pwsh -File scripts\build-mac.ps1                   # テスト → osx-arm64 / osx-x64 の .app を zip に（開発・予備用）
src\WebcamFbt.Mac\bin\Release\net10.0\WebcamFbt.exe --self-test   # 自己診断（Windows では CPU だけ）
```

zip 版は Windows で作れるが、署名が無いので Mac 側で `xattr -dr com.apple.quarantine` と `codesign --force --deep --sign -` を
打ってもらう必要がある（「はじめにお読みください.txt」に手順）。友達に渡すのは .pkg を優先する。
Windows の ZipArchive は Unix の実行権限を残せないので、スクリプトが「作成 OS = Unix」に書き換えてから権限を入れている。

開発用に、Windows で `dotnet run --project src\WebcamFbt.Mac -c Release` すると Mac 版の画面を Windows 上で動かせる
（このときだけ Windows 版の OpenCV を足す。推論は CPU、カメラは Media Foundation）。

## 友達の Mac での確認手順

### M1: 映像＋骨格の表示

用意: Mac 本体（内蔵カメラで可）と、`WebcamFbt-<版>-mac.pkg`（Apple Silicon / Intel 両方入り）。

1. アップルメニュー →「この Mac について」で **チップ**（Apple M… か Intel か）と **macOS のバージョン** を控える
2. .pkg をダブルクリックしてインストールする（「開けません」と出たら右クリック →「開く」、または システム設定 →
   プライバシーとセキュリティ →「このまま開く」）。終わったらアプリケーションフォルダの WebcamFbt を開く
   - 確認: インストーラが途中で Rosetta を求めたり、エラーで止まったりしない
   - 確認: ウィンドウが開き、右にタブ（カメラ／トラッキング／校正／VRChat）、左に「「開始」を押すとカメラ映像が表示されます」
3. 「開始」を押す（初回はモデルのダウンロードと CoreML の準備で 1 分ほどかかることがある）
   - 確認: カメラの許可を求めるダイアログが出る → 「許可」
   - 映像が出ないときは「停止」→「開始」をもう一度
4. カメラの前に全身（せめて上半身）が映るように立つ
   - 確認: 映像に骨格（線と点）が重なる。左上の「実行」が **CoreML** か **CPU** か、「処理」の fps、「合計」の ms を控える
5. 停止 →「実行デバイス」を **CPU** にして開始 → 同じく fps と ms を控える（CoreML との比較）
6. 「左右反転」「しきい値」のスライダーを動かして、表示が変わるか見る
7. バックグラウンドで止まらないか: 実行中にほかのアプリ（Safari など）を前に出して 1 分ほど待ち、WebcamFbt に戻る
   - 確認: 左上の「処理」の fps が、前に出していたときと大きく変わっていない
8. スリープしないか: 実行中のまま、Mac に触らずに 10 分以上放置する（画面は消えてかまわない）
   - 確認: 戻ったときに映像が動き続けている。ターミナルで `pmset -g assertions | grep -i webcamfbt` を打つと
     WebcamFbt の行が出る（実行中だけ。「停止」すると消える）

送ってほしいもの: 手順 1・4・5・7・8 の控え、画面のスクリーンショット（command + shift + 4 → スペース → ウィンドウをクリック）、
うまくいかなかったときは `~/Library/Application Support/WebcamFbt/error.log`。
