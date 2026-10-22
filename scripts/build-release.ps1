# 配布物（単一 exe とインストーラ）を作る（フェーズ 5c）。
#
#   powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
#
# 出力: artifacts\release\WebcamFbt-<版>-win-x64.exe（単体で動く exe）
#       artifacts\release\WebcamFbt-<版>-setup.exe （インストーラ）
# 版は Directory.Build.props の <Version>。Inno Setup 6 が必要（ISCC.exe を自動で探す。-Iscc で指定も可）。
param(
    [string]$Iscc,
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Directory.Build.props に <Version> がありません。' }
Write-Host "WebcamFbt $version の配布物を作ります" -ForegroundColor Cyan

$publishDir = Join-Path $root 'artifacts\publish'
$releaseDir = Join-Path $root 'artifacts\release'

if (-not $SkipTests) {
    Write-Host '--- テスト ---' -ForegroundColor Cyan
    dotnet test tests\WebcamFbt.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'テストが失敗しました。' }
}

Write-Host '--- 単一 exe（self-contained） ---' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src\WebcamFbt.App -p:PublishProfile=SingleFile
if ($LASTEXITCODE -ne 0) { throw 'publish が失敗しました。' }

New-Item -ItemType Directory -Force $releaseDir | Out-Null
$exe = Join-Path $releaseDir "WebcamFbt-$version-win-x64.exe"
Copy-Item (Join-Path $publishDir 'WebcamFbt.exe') $exe -Force
Write-Host ("  {0}（{1:N0} MB）" -f $exe, ((Get-Item $exe).Length / 1MB))

if (-not $SkipInstaller) {
    Write-Host '--- インストーラ（Inno Setup） ---' -ForegroundColor Cyan
    if (-not $Iscc) {
        $Iscc = @(
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $Iscc) { throw 'Inno Setup 6（ISCC.exe）が見つかりません。インストールするか -Iscc で場所を指定してください。' }

    & $Iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$releaseDir" (Join-Path $root 'installer\WebcamFbt.iss')
    if ($LASTEXITCODE -ne 0) { throw 'インストーラの作成が失敗しました。' }
    $setup = Join-Path $releaseDir "WebcamFbt-$version-setup.exe"
    Write-Host ("  {0}（{1:N0} MB）" -f $setup, ((Get-Item $setup).Length / 1MB))
}

Write-Host '完了しました。' -ForegroundColor Green
