#!/bin/bash
# Mac 版のインストーラ（.pkg）を作る。macOS 上で実行する（GitHub Actions の .github/workflows/mac-pkg.yml から呼ぶ）。
#
#   bash scripts/build-mac-pkg.sh
#
# 出力: artifacts/release/WebcamFbt-<版>-mac.pkg
#   Apple Silicon 用と Intel 用の .app を両方含み、インストールする Mac のチップに合う方だけが入る。
#   各 .app はここで ad-hoc 署名する（署名が無いと Apple Silicon では起動できない）。Apple の署名・公証はしない。
# 途中の .app は artifacts/mac-pkg/stage-<arm64|x64>/WebcamFbt.app に残る（自己診断に使う）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n 1)"
if [ -z "$VERSION" ]; then
    echo "Directory.Build.props に <Version> がありません。" >&2
    exit 1
fi
echo "WebcamFbt $VERSION の Mac 版インストーラを作ります"

PACKAGING="$ROOT/src/WebcamFbt.Mac/Packaging"
WORK="$ROOT/artifacts/mac-pkg"
RELEASE="$ROOT/artifacts/release"
rm -rf "$WORK"
mkdir -p "$WORK" "$RELEASE"

# pkg のスクリプトは実行権限と LF 改行が必要（Windows で編集しても動くように整える）
mkdir -p "$WORK/scripts"
for f in preinstall postinstall; do
    tr -d '\r' < "$PACKAGING/pkg-scripts/$f" > "$WORK/scripts/$f"
    chmod 755 "$WORK/scripts/$f"
done

for ARCH in arm64 x64; do
    RID="osx-$ARCH"
    echo "--- $RID ---"
    PUBLISH="$WORK/publish-$ARCH"
    dotnet publish src/WebcamFbt.Mac -c Release -r "$RID" --self-contained true -p:DebugType=None -o "$PUBLISH"

    # .app を組み立てる: Contents/Info.plist と Contents/MacOS/（publish の出力一式）
    STAGE="$WORK/stage-$ARCH"
    APP="$STAGE/WebcamFbt.app"
    mkdir -p "$APP/Contents/MacOS"
    cp -R "$PUBLISH/." "$APP/Contents/MacOS/"
    sed "s/\$(VERSION)/$VERSION/g" "$PACKAGING/Info.plist" > "$APP/Contents/Info.plist"
    plutil -lint "$APP/Contents/Info.plist"

    # Intel 版 OpenCV が参照する Homebrew の libavif（使わない）を、無くても読み込めるようにする
    python3 scripts/weaken-dylib-deps.py "$APP/Contents/MacOS/libOpenCvSharpExtern.dylib"
    if otool -L "$APP/Contents/MacOS/"*.dylib | grep -E '^\s+(/usr/local/|/opt/homebrew/)' ; then
        otool -l "$APP/Contents/MacOS/libOpenCvSharpExtern.dylib" | grep -B2 -A3 LC_LOAD_WEAK_DYLIB || true
    fi

    # ad-hoc 署名（この Mac に限らずどの Mac でも有効。中の dylib もまとめて署名する）
    codesign --force --deep --sign - "$APP"
    codesign --verify --deep --strict "$APP"

    # 既に別の場所に同じアプリがあっても、そちらを上書きせず /Applications に入れる
    pkgbuild --analyze --root "$STAGE" "$WORK/component-$ARCH.plist"
    plutil -replace 0.BundleIsRelocatable -bool NO "$WORK/component-$ARCH.plist"

    pkgbuild --root "$STAGE" \
        --component-plist "$WORK/component-$ARCH.plist" \
        --install-location /Applications \
        --identifier "io.github.hotakacchi.webcamfbt.$ARCH" \
        --version "$VERSION" \
        --scripts "$WORK/scripts" \
        "$WORK/WebcamFbt-$ARCH.pkg"
done

echo "--- インストーラ（両方入り） ---"
sed "s/@VERSION@/$VERSION/g" "$PACKAGING/distribution.xml" > "$WORK/distribution.xml"
PKG="$RELEASE/WebcamFbt-$VERSION-mac.pkg"
productbuild --distribution "$WORK/distribution.xml" \
    --resources "$PACKAGING/pkg-resources" \
    --package-path "$WORK" \
    "$PKG"

pkgutil --check-signature "$PKG" || true   # 署名していないので「no signature」と出るのが正常
ls -lh "$PKG"
echo "完了しました: $PKG"
