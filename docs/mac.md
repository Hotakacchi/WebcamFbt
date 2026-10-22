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
- 画面: Windows 版と同じ並び・文言・色（右に 330px の設定パネルとタブ、左にプレビューと統計、手順ガイド、状態表示、上から見た図）
- ViewModel: 名前空間は Windows 版と同じ `WebcamFbt.App.ViewModels`。WPF に依存しない partial ファイルは
  **Windows 版のファイルをそのままコンパイルして共有**している（`WebcamFbt.Mac.csproj` の `<Compile Include=... Link=...>`）
  - 共有: `MainViewModel.Tracking.cs`（トラッキング・OSC）、`.CameraCalibration.cs`（チェッカーボード校正）、
    `.Guide.cs`（手順ガイド・状態表示）、`.Settings.cs`（設定の保存。タイマーだけ `#if WINDOWS` で WPF / Avalonia を切り替え）、
    `src/WebcamFbt.App/AppInfo.cs`（版の表示。タイトル・右パネルの下・メニューバーのアイコンの説明）
  - Mac 版に写したもの: `MainViewModel.cs`（カメラ・推論・プレビュー）、`.Camera2.cs`（プレビューの画像の型が違うため）。
    Windows 版のこの 2 つを変えたら Mac 版にも同じ変更を入れる
  - 共有ファイルに WPF の型を書くと Mac 版のビルドが壊れる（各ファイルの先頭に注意書きあり）
- 閉じるボタン: Windows 版の「タスクトレイに入る」に合わせ、画面だけ隠して動き続ける。メニューバー（右上）のアイコンから
  「表示」「開始 / 停止」「終了」、Dock のアイコンを押しても画面が出る。command + Q で終了
- アイコン: `src/WebcamFbt.Mac/Assets/WebcamFbt.icns`（`pwsh -File scripts\make-icon.ps1 -MacIcns src\WebcamFbt.Mac\Assets\WebcamFbt.icns`
  で Windows 版と同じ絵から作る。PNG を詰めただけの形式なので Windows で作れる）を `Contents/Resources` に入れる。
  メニューバーとウィンドウのアイコンは Windows 版の .ico
- 開発用: `WebcamFbt --screenshot <フォルダ>` で 4 つのタブの画面を PNG に保存して終了する（CI で Mac での見た目を撮る）
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
  - 新しい版を入れると、今までの /Applications/WebcamFbt.app を丸ごと置き換える（同じ版・古い版を入れ直しても同じ）
    - preinstall: 動いている WebcamFbt を終了させ（TERM → 5 秒後に KILL）、前の .app を消す。設定・モデルは Application Support なので残る
    - component plist: BundleIsRelocatable=NO（別の場所のコピーに入れない）、BundleIsVersionChecked=NO
      （入っている方が新しくても飛ばさない。飛ばすと preinstall で消した後に何も入らない）、BundleOverwriteAction=upgrade
    - postinstall で quarantine を外す
    - CI で「起動中＋前の版にだけあるファイルがある」状態から入れ直し、終了・置き換えを確かめている
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

### 全機能（Windows 版と同じ機能）

用意: Mac 本体（内蔵カメラで可）と、`WebcamFbt-<版>-mac.pkg`（Apple Silicon / Intel 両方入り）。

1. アップルメニュー →「この Mac について」で **チップ**（Apple M… か Intel か）と **macOS のバージョン** を控える
2. .pkg をダブルクリックしてインストールする（「開けません」と出たら右クリック →「開く」、または システム設定 →
   プライバシーとセキュリティ →「このまま開く」）。終わったらアプリケーションフォルダの WebcamFbt を開く
   - 確認: インストーラが途中で Rosetta を求めたり、エラーで止まったりしない
   - 確認: Dock とアプリケーションフォルダに WebcamFbt のアイコンが出る
   - 確認: ウィンドウが開き、右上に手順ガイド（1〜4）、その下にタブ（カメラ／トラッキング／校正／VRChat）
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

9. トラッキング: 開始して全身を映し、左下に腰・左足・右足・頭の状態（色付きの点）と座標、右下に上から見た図が出るか
10. 体のキャリブレーション: 「トラッキング」タブで身長を合わせ、T ポーズで「キャリブレーション開始」→「キャリブレーション済み」と出るか
11. Quest に送る: Quest と Mac を同じ Wi-Fi にし、Quest の IP（Quest の 設定 → Wi-Fi → つないでいるネットワーク → 詳細）を
    「VRChat」タブの送信先に入れて「反映」。Quest の VRChat で アクションメニュー → オプション → OSC → 有効、
    続けて トラッキング → OSC トラッカー（キャリブレーション）を行い、腰・足が動くか。「向きを合わせる」も試す
    - 確認: 左下の「OSC」が緑で、送信先が Quest の IP になっている
12. 閉じても動き続けるか: 実行中に赤い丸（閉じる）を押す → Quest の足が動き続ける → メニューバー（右上）の
    WebcamFbt のアイコン →「表示」で画面が戻る。「終了」または command + Q で終了する
13. 設定の保存: 送信先・身長・キャリブレーションをした後に終了 → もう一度開いて、同じ値が入っているか
14. （カメラが 2 台あれば）「カメラを2台使う」をオンにして 2 つの映像が出るか、「校正」タブでチェッカーボードの校正ができるか

送ってほしいもの: 手順 1・4・5・7・8・9〜13 の控え、画面のスクリーンショット（command + shift + 4 → スペース → ウィンドウをクリック）、
うまくいかなかったときは `~/Library/Application Support/WebcamFbt/error.log`。
