"""
Mac 版の dylib が参照している「使わないライブラリ」を弱いリンク（無くても読み込める）に書き換える。

OpenCvSharp4.runtime.osx.x64（公式の Intel Mac 版）の libOpenCvSharpExtern.dylib は、
Homebrew の libavif（/usr/local/opt/libavif/lib/libavif.16.dylib、AVIF 画像の読み書き用）を参照していて、
Homebrew で libavif を入れていない Mac では読み込めない。WebcamFbt は AVIF を使わないので、
読み込み命令 LC_LOAD_DYLIB を LC_LOAD_WEAK_DYLIB に変える。見つからなければ、その中の関数は NULL になるだけ。
書き換えると署名が壊れるので、この後に codesign し直すこと（build-mac-pkg.sh がする）。

  python3 scripts/weaken-dylib-deps.py <dylib> [<dylib> ...]

対象は WEAKEN_PREFIXES に当てはまる参照だけ。当てはまるものが無ければ何もしない。
"""
import struct
import sys

LC_LOAD_DYLIB = 0xC
LC_LOAD_WEAK_DYLIB = 0x80000018
MH_MAGIC_64 = 0xFEEDFACF
FAT_MAGIC = 0xCAFEBABE

# Homebrew の場所（Intel: /usr/local、Apple Silicon: /opt/homebrew）
WEAKEN_PREFIXES = ("/usr/local/", "/opt/homebrew/")


def patch_slice(data: bytearray, offset: int, path: str) -> int:
    magic = struct.unpack_from("<I", data, offset)[0]
    if magic != MH_MAGIC_64:
        raise ValueError(f"64 ビットの Mach-O ではありません: {path}")
    ncmds = struct.unpack_from("<I", data, offset + 16)[0]
    p = offset + 32
    changed = 0
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, p)
        if cmd == LC_LOAD_DYLIB:
            name_off = struct.unpack_from("<I", data, p + 8)[0]
            name = bytes(data[p + name_off:p + size]).split(b"\0")[0].decode()
            if name.startswith(WEAKEN_PREFIXES):
                struct.pack_into("<I", data, p, LC_LOAD_WEAK_DYLIB)
                print(f"  弱いリンクに変更: {name}")
                changed += 1
        p += size
    return changed


def main() -> int:
    for path in sys.argv[1:]:
        data = bytearray(open(path, "rb").read())
        if struct.unpack_from(">I", data, 0)[0] == FAT_MAGIC:
            count = struct.unpack_from(">I", data, 4)[0]
            offsets = [struct.unpack_from(">5I", data, 8 + 20 * i)[2] for i in range(count)]
        else:
            offsets = [0]
        print(f"{path}:")
        changed = sum(patch_slice(data, o, path) for o in offsets)
        if changed:
            open(path, "wb").write(data)
        else:
            print("  変更なし")
    return 0


if __name__ == "__main__":
    sys.exit(main())
