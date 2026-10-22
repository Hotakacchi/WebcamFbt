# Mac 版（src\WebcamFbt.Mac）の配布物を作る。Windows 上で作れる。
#
#   pwsh -File scripts\build-mac.ps1 [-SkipTests] [-Rids osx-arm64,osx-x64]
#
# 出力: artifacts\release\WebcamFbt-<版>-mac-arm64.zip（Apple Silicon: M1 以降）
#       artifacts\release\WebcamFbt-<版>-mac-x64.zip  （Intel Mac）
# 中身は WebcamFbt.app と「はじめにお読みください.txt」。署名・公証はしていないので、
# Mac 側で quarantine 属性を外して ad-hoc 署名する手順が要る（docs\mac.md）。
param(
    [switch]$SkipTests,
    [string[]]$Rids = @('osx-arm64', 'osx-x64')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Directory.Build.props に <Version> がありません。' }
Write-Host "WebcamFbt $version の Mac 版を作ります" -ForegroundColor Cyan

$publishRoot = Join-Path $root 'artifacts\publish-mac'
$releaseDir = Join-Path $root 'artifacts\release'
$macProject = Join-Path $root 'src\WebcamFbt.Mac'

if (-not $SkipTests) {
    Write-Host '--- テスト ---' -ForegroundColor Cyan
    dotnet test tests\WebcamFbt.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'テストが失敗しました。' }
}

# zip に Unix のファイル属性（実行権限）を入れる。
# .NET の ZipArchive は Windows 上だと「作成 OS = Windows」で書くため、Mac で展開すると属性が無視される。
# 書き終えた後に中央ディレクトリの「作成 OS」を Unix（3）に書き換える。
function Set-ZipUnixHost([string]$zipPath) {
    $bytes = [System.IO.File]::ReadAllBytes($zipPath)
    # 終端レコード（EOCD）を後ろから探す
    $eocd = -1
    for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i + 1] -eq 0x4b -and $bytes[$i + 2] -eq 0x05 -and $bytes[$i + 3] -eq 0x06) { $eocd = $i; break }
    }
    if ($eocd -lt 0) { throw "zip の終端が見つかりません: $zipPath" }
    $count = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $p = [int][BitConverter]::ToUInt32($bytes, $eocd + 16)
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($bytes, $p) -ne 0x02014b50) { throw "zip の中央ディレクトリが壊れています: $zipPath" }
        $bytes[$p + 5] = 3
        $nameLen = [BitConverter]::ToUInt16($bytes, $p + 28)
        $extraLen = [BitConverter]::ToUInt16($bytes, $p + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $p + 32)
        $p += 46 + $nameLen + $extraLen + $commentLen
    }
    [System.IO.File]::WriteAllBytes($zipPath, $bytes)
}

function Add-ZipFile($zip, [string]$source, [string]$entryName, [int]$mode) {
    $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $source, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    # 上位 16 ビットが Unix のモード（通常ファイル 0x8000 + 権限）
    $entry.ExternalAttributes = (0x8000 -bor $mode) -shl 16
}

New-Item -ItemType Directory -Force $releaseDir | Out-Null
$plistTemplate = Get-Content (Join-Path $macProject 'Packaging\Info.plist') -Raw -Encoding UTF8
$readme = Join-Path $macProject 'Packaging\はじめにお読みください.txt'

foreach ($rid in $Rids) {
    $arch = $rid.Substring(4)  # arm64 / x64
    Write-Host "--- $rid ---" -ForegroundColor Cyan
    $publishDir = Join-Path $publishRoot $rid
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $macProject -c Release -r $rid --self-contained true -p:DebugType=None -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish が失敗しました（$rid）。" }
    # Intel 版 OpenCV が参照する Homebrew の libavif（使わない）を、無くても読み込めるようにする（Python が必要）
    python (Join-Path $root 'scripts\weaken-dylib-deps.py') (Join-Path $publishDir 'libOpenCvSharpExtern.dylib')
    if ($LASTEXITCODE -ne 0) { throw "dylib の書き換えが失敗しました（$rid）。" }

    $zipPath = Join-Path $releaseDir "WebcamFbt-$version-mac-$arch.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # .app の中身: Contents/Info.plist と Contents/MacOS/（publish の出力をそのまま）
        $entry = $zip.CreateEntry('WebcamFbt.app/Contents/Info.plist')
        $entry.ExternalAttributes = (0x8000 -bor 0x1A4) -shl 16  # 0644
        $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.Write($plistTemplate.Replace('$(VERSION)', $version))
        $writer.Dispose()

        Get-ChildItem $publishDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
            # 実行ファイル（拡張子なし: WebcamFbt, createdump）と dylib は 0755、ほかは 0644
            $mode = if ($_.Extension -eq '' -or $_.Extension -eq '.dylib') { 0x1ED } else { 0x1A4 }
            Add-ZipFile $zip $_.FullName "WebcamFbt.app/Contents/MacOS/$rel" $mode
        }
        Add-ZipFile $zip $readme 'はじめにお読みください.txt' 0x1A4
    }
    finally {
        $zip.Dispose()
    }
    Set-ZipUnixHost $zipPath
    Write-Host ("  {0}（{1:N0} MB）" -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
}

Write-Host '完了しました。' -ForegroundColor Green
