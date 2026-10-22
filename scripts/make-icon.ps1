# アプリのアイコン（src\WebcamFbt.App\Assets\WebcamFbt.ico）を、元画像（Assets\icon-source.png）から作る（フェーズ 5c）。
#
#   pwsh -File scripts\make-icon.ps1 [-PreviewPng <確認用 PNG の保存先>]
#   pwsh -File scripts\make-icon.ps1 -MacIcns src\WebcamFbt.Mac\Assets\WebcamFbt.icns   （Mac 版の .app のアイコンも作る）
#
# 元画像は白い背景の上に角丸の四角が描かれたもの。白い余白を透明にするため、
#  1. 白でない画素の範囲（外枠）と、左上の角の丸み（対角線上で絵が始まる位置）から角丸の半径を測る
#  2. 角丸の四角の形で切り抜く（縁はアンチエイリアス。白がにじまないよう少し内側で切る）
#  3. 16〜256px に縮小して ICO にする（256px は PNG、それ以外は 32bit の DIB）
param([string]$PreviewPng, [string]$MacIcns)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\WebcamFbt.App\Assets'
$sourcePath = Join-Path $assets 'icon-source.png'
$out = Join-Path $assets 'WebcamFbt.ico'

# ---- 1. 外枠と角丸の半径を測る ----
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
$sw = $source.Width; $sh = $source.Height
function IsBackground([int]$x, [int]$y) {
    $c = $source.GetPixel($x, $y)
    $c.A -lt 16 -or ($c.R -gt 225 -and $c.G -gt 225 -and $c.B -gt 225)
}
$cx = [int]($sw / 2); $cy = [int]($sh / 2)
$left = 0; while (IsBackground $left $cy) { $left++ }
$right = $sw - 1; while (IsBackground $right $cy) { $right-- }
$top = 0; while (IsBackground $cx $top) { $top++ }
$bottom = $sh - 1; while (IsBackground $cx $bottom) { $bottom-- }
$d = 0; while (IsBackground ($left + $d) ($top + $d)) { $d++ }
# 半径 r の円弧は、角から対角線上に r(1 - 1/√2) 入ったところを通る
$radius = $d / (1 - 1 / [Math]::Sqrt(2))
Write-Host ("外枠: ({0},{1})-({2},{3})  角丸の半径: {4:F0}px" -f $left, $top, $right, $bottom, $radius)

# ---- 2. 角丸の四角で切り抜いた 1024px の元絵を作る ----
$master = 1024
$inset = 3  # 縁の白がにじまないよう、少し内側で切る
$boxW = $right - $left + 1; $boxH = $bottom - $top + 1
$scale = $master / [Math]::Max($boxW, $boxH)

$masterBmp = New-Object System.Drawing.Bitmap($master, $master, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($masterBmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

# 元画像を、外枠が 1024px の正方形に収まるよう置く（縦横の差は中央に寄せる）
$offX = ($master - $boxW * $scale) / 2 - $left * $scale
$offY = ($master - $boxH * $scale) / 2 - $top * $scale
$brush = New-Object System.Drawing.TextureBrush($source, [System.Drawing.Drawing2D.WrapMode]::Clamp)
$brush.TranslateTransform($offX, $offY)
$brush.ScaleTransform($scale, $scale)

$x0 = ($master - $boxW * $scale) / 2 + $inset; $y0 = ($master - $boxH * $scale) / 2 + $inset
$x1 = $x0 + $boxW * $scale - 2 * $inset; $y1 = $y0 + $boxH * $scale - 2 * $inset
$r2 = 2 * ($radius * $scale - $inset)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($x0, $y0, $r2, $r2, 180, 90)
$path.AddArc($x1 - $r2, $y0, $r2, $r2, 270, 90)
$path.AddArc($x1 - $r2, $y1 - $r2, $r2, $r2, 0, 90)
$path.AddArc($x0, $y1 - $r2, $r2, $r2, 90, 90)
$path.CloseFigure()
$g.FillPath($brush, $path)
$g.Dispose()
$source.Dispose()

# ---- Mac 版のアイコン（.icns） ----
# macOS のアイコンの決まりに合わせ、1024px の中央 824px に絵を収めて周りを透明にし、各サイズの PNG を .icns に詰める。
# .icns は「'icns' + 全体の長さ」の後に「種類（4 文字）+ 長さ + PNG」が並ぶだけの形式（長さはビッグエンディアン）
if ($MacIcns) {
    $macBmp = New-Object System.Drawing.Bitmap(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mg = [System.Drawing.Graphics]::FromImage($macBmp)
    $mg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $mg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $mg.Clear([System.Drawing.Color]::Transparent)
    $mg.DrawImage($masterBmp, 100, 100, 824, 824)
    $mg.Dispose()

    # 種類と大きさ: ic11=16@2x, ic12=32@2x, ic07=128, ic13=128@2x, ic08=256, ic14=256@2x, ic09=512, ic10=512@2x
    $entries = @(('ic11', 32), ('ic12', 64), ('ic07', 128), ('ic13', 256), ('ic08', 256), ('ic14', 512), ('ic09', 512), ('ic10', 1024))
    $chunks = foreach ($e in $entries) {
        $size = $e[1]
        $small = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $sg = [System.Drawing.Graphics]::FromImage($small)
        $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $sg.Clear([System.Drawing.Color]::Transparent)
        $sg.DrawImage($macBmp, 0, 0, $size, $size)
        $sg.Dispose()
        $pngStream = New-Object System.IO.MemoryStream
        $small.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
        $small.Dispose()
        , @($e[0], $pngStream.ToArray())
    }
    $macBmp.Dispose()

    function BigEndian([int]$v) { $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); , $b }
    $total = 8
    foreach ($c in $chunks) { $total += 8 + $c[1].Length }
    $fs = [System.IO.File]::Create((Join-Path (Get-Location) $MacIcns))
    $fs.Write([System.Text.Encoding]::ASCII.GetBytes('icns'), 0, 4)
    $fs.Write((BigEndian $total), 0, 4)
    foreach ($c in $chunks) {
        $fs.Write([System.Text.Encoding]::ASCII.GetBytes($c[0]), 0, 4)
        $fs.Write((BigEndian (8 + $c[1].Length)), 0, 4)
        $fs.Write($c[1], 0, $c[1].Length)
    }
    $fs.Dispose()
    Write-Host "Mac 版のアイコンを作りました: $MacIcns（$total バイト）"
}

# ---- 3. 各サイズに縮小して ICO にする ----
function Resize([System.Drawing.Bitmap]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
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
    $bmp = Resize $masterBmp $size
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
$masterBmp.Dispose()

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
