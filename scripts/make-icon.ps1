# アプリのアイコン（src\WebcamFbt.App\Assets\WebcamFbt.ico）を描いて作る（フェーズ 5c）。
#
#   pwsh -File scripts\make-icon.ps1 [-PreviewPng <確認用 PNG の保存先>]
#
# デザイン: 青→紫のグラデーションの角丸の四角に、白い T ポーズの人と、腰・両足の位置の緑の点（トラッカー）。
# 周りにカメラのファインダー風の角の印。小さいサイズ（32px 以下）では角の印を省き、線を太めにしてつぶれないようにする。
# ICO には 16〜256px を入れる（256px は PNG、それ以外は 32bit の DIB）。
param([string]$PreviewPng)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\WebcamFbt.App\Assets\WebcamFbt.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # 256 の座標で描いて縮める
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 32

    # 背景: 角丸の四角（青 → 紫）
    $r = 56; $x = 8; $y = 8; $w = 240; $h = 240
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $r, $r, 180, 90)
    $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(256, 256)),
        [System.Drawing.Color]::FromArgb(255, 0x1E, 0x8F, 0xF5), [System.Drawing.Color]::FromArgb(255, 0x6A, 0x3D, 0xE8))
    $g.FillPath($bg, $path)

    # ファインダーの角の印（小さいサイズでは省く）
    if (-not $small) {
        $frame = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 255, 255), 10)
        $frame.StartCap = $frame.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $a = 40; $b = 216; $l = 34
        foreach ($c in @(@($a, $a, 1, 1), @($b, $a, -1, 1), @($a, $b, 1, -1), @($b, $b, -1, -1))) {
            $g.DrawLine($frame, $c[0], $c[1], $c[0] + $l * $c[2], $c[1])
            $g.DrawLine($frame, $c[0], $c[1], $c[0], $c[1] + $l * $c[3])
        }
    }

    # T ポーズの人（白）
    $bodyWidth = if ($small) { 22 } else { 16 }
    $body = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $bodyWidth)
    $body.StartCap = $body.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $body.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $white = [System.Drawing.Brushes]::White
    $headR = if ($small) { 26 } else { 21 }
    $g.FillEllipse($white, 128 - $headR, 66 - $headR, 2 * $headR, 2 * $headR)
    $g.DrawLine($body, 128, 96, 128, 152)      # 背骨
    $g.DrawLine($body, 72, 108, 184, 108)      # 腕（T ポーズ）
    $g.DrawLine($body, 128, 152, 96, 206)      # 左脚
    $g.DrawLine($body, 128, 152, 160, 206)     # 右脚

    # トラッカーの点（腰・両足、緑に白い縁）
    $dotR = if ($small) { 20 } else { 15 }
    $green = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x4C, 0xD9, 0x6A))
    $rim = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $(if ($small) { 7 } else { 5 }))
    foreach ($p in @(@(128, 152), @(96, 206), @(160, 206))) {
        $g.FillEllipse($green, $p[0] - $dotR, $p[1] - $dotR, 2 * $dotR, 2 * $dotR)
        # 24px 以下では白い縁がにじんで脚と混ざるので描かない
        if ($size -gt 24) { $g.DrawEllipse($rim, $p[0] - $dotR, $p[1] - $dotR, 2 * $dotR, 2 * $dotR) }
    }

    $g.Dispose()
    return $bmp
}

# 32bit の DIB（ICO の中身。高さは XOR と AND の 2 枚分、下の行から並べる）
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $n, $n)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $n)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$n); $bw.Write([int]($n * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($row = $n - 1; $row -ge 0; $row--) { $bw.Write($pixels, $row * $data.Stride, $n * 4) }
    $maskStride = [int]([Math]::Ceiling($n / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $n)))  # AND マスクは使わない（透明度はアルファで表す）
    $bw.Flush()
    # 先頭のカンマ: PowerShell が配列を要素ごとに分解して返さないようにする
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    if ($size -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($PreviewPng) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
        , $ms.ToArray()
    } else {
        , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose()
Write-Host "アイコンを作りました: $out"
