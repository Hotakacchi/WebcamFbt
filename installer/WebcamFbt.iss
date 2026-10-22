; WebcamFbt のインストーラ（Inno Setup 6、フェーズ 5c）
; 通常は scripts\build-release.ps1 から、AppVersion・PublishDir・OutputDir を渡してコンパイルする。
;
; - 管理者権限なしでユーザーごとにインストールする（既定は %LOCALAPPDATA%\Programs\WebcamFbt）。
;   「すべてのユーザー」に入れたいときは、最初の画面で選べる（管理者権限が必要）
; - 中身は単一 exe と README・ライセンス類だけ（DirectML.dll は exe の中）。モデルは同梱しない（初回起動時にダウンロード）
; - アンインストール時に、設定・校正結果・モデル（%LOCALAPPDATA%\WebcamFbt）も消すか尋ねる（既定は残す）

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

[Setup]
; AppId はアップグレード・アンインストールでアプリを見分けるための値。一度配布したら変えないこと
AppId={{04D48C89-F237-4828-A957-6ADA25CF3024}
AppName=WebcamFbt
AppVersion={#AppVersion}
AppVerName=WebcamFbt {#AppVersion}
AppPublisher=WebcamFbt
DefaultDirName={autopf}\WebcamFbt
DefaultGroupName=WebcamFbt
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; DirectML は Windows 10 1903（ビルド 18362）以降が必要
MinVersion=10.0.18362
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=WebcamFbt-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\WebcamFbt.App\Assets\WebcamFbt.ico
UninstallDisplayIcon={app}\WebcamFbt.exe
UninstallDisplayName=WebcamFbt

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\WebcamFbt.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WebcamFbt"; Filename: "{app}\WebcamFbt.exe"
Name: "{autodesktop}\WebcamFbt"; Filename: "{app}\WebcamFbt.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WebcamFbt.exe"; Description: "{cm:LaunchProgram,WebcamFbt}"; Flags: nowait postinstall skipifsilent

[Code]
// アンインストールの最後に、利用者のデータ（設定・カメラの校正結果・ダウンロードしたモデル）も消すか尋ねる。
// 再インストールして使い続ける人が多いはずなので、既定は「いいえ」（残す）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if UninstallSilent then Exit;
  DataDir := ExpandConstant('{localappdata}\WebcamFbt');
  if not DirExists(DataDir) then Exit;
  if MsgBox('設定・カメラの校正結果・ダウンロードしたモデルも削除しますか？' + #13#10 +
            '（' + DataDir + '）' + #13#10 + #13#10 +
            'また使う予定がある場合は「いいえ」を選んでください。',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
