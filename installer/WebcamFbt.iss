; WebcamFbt のインストーラ（Inno Setup 6、フェーズ 5c）
; 通常は scripts\build-release.ps1 から、AppVersion・PublishDir・OutputDir を渡してコンパイルする。
;
; - 管理者権限なしでユーザーごとにインストールする（既定は %LOCALAPPDATA%\Programs\WebcamFbt）。
;   「すべてのユーザー」に入れたいときは、最初の画面で選べる（管理者権限が必要）
; - 中身は単一 exe と README・ライセンス類だけ（DirectML.dll は exe の中）。モデルは同梱しない（初回起動時にダウンロード）
; - アンインストール時に、設定・校正結果・モデル（%LOCALAPPDATA%\WebcamFbt）も消すか尋ねる（既定は残す）
; - 新しい版のインストーラを起動すると、今入っているものに上書きする（更新）。AppId が同じなら前回の入れ先・
;   インストールの種類（ユーザーごと / すべてのユーザー）・タスクの選択をそのまま使い、入れ先と使用許諾の画面は飛ばす。
;   アプリが起動中なら閉じるか確認してから上書きする。古い版で上書きしようとしたときは確認する
; - 更新しても設定・カメラの校正の結果（%LOCALAPPDATA%\WebcamFbt）には触れない。念のため更新の前に backup\ へ写しを取る

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
; AppId は上書き更新・アンインストールでアプリを見分けるための値。一度配布したら変えないこと
; （試験で本物のインストールに触れないよう、/DAppIdGuid=... で別の値に差し替えられる）
#ifndef AppIdGuid
  #define AppIdGuid "04D48C89-F237-4828-A957-6ADA25CF3024"
#endif

[Setup]
AppId={{{#AppIdGuid}}
AppName=WebcamFbt
AppVersion={#AppVersion}
AppVerName=WebcamFbt {#AppVersion}
AppPublisher=WebcamFbt
DefaultDirName={autopf}\WebcamFbt
DefaultGroupName=WebcamFbt
DisableProgramGroupPage=yes
; 上書き更新: 前回の入れ先・インストールの種類・タスクを引き継ぎ、入れ先の画面は更新時には出さない
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
UsePreviousTasks=yes
DisableDirPage=auto
; 起動中のアプリが使っているファイルを見つけたら、閉じてよいか確認してから閉じる（設定は通常の終了と同じく保存される）
CloseApplications=yes
RestartApplications=no
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
Source: "..\LICENSE-EXCEPTION.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\licenses\*.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WebcamFbt"; Filename: "{app}\WebcamFbt.exe"
Name: "{autodesktop}\WebcamFbt"; Filename: "{app}\WebcamFbt.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WebcamFbt.exe"; Description: "{cm:LaunchProgram,WebcamFbt}"; Flags: nowait postinstall skipifsilent

[Code]
// ---- 上書き更新 ----

// 前に入れた版（入っていなければ空文字）。アンインストール情報の DisplayVersion を見る
function GetInstalledVersion(): String;
var
  Key: String;
begin
  Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppIdGuid}}_is1';
  if not RegQueryStringValue(HKCU, Key, 'DisplayVersion', Result) then
    if not RegQueryStringValue(HKLM, Key, 'DisplayVersion', Result) then
      Result := '';
end;

function IsUpgrade(): Boolean;
begin
  Result := GetInstalledVersion() <> '';
end;

// 今より新しい版が入っているとき（古い版で上書きしようとしたとき）は確認する
function InitializeSetup(): Boolean;
var
  Installed: String;
  InstalledPacked, NewPacked: Int64;
begin
  Result := True;
  Installed := GetInstalledVersion();
  if (Installed <> '') and StrToVersion(Installed, InstalledPacked) and StrToVersion('{#AppVersion}', NewPacked) then
    if ComparePackedVersion(InstalledPacked, NewPacked) > 0 then
      Result := SuppressibleMsgBox(
        '新しい版（' + Installed + '）がすでに入っています。' + #13#10 +
        '古い版（{#AppVersion}）で上書きしますか？',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

// 更新のときは使用許諾の画面を飛ばす（初回のインストールで同意済み）
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpLicense) and IsUpgrade();
end;

// 更新の前に、設定とカメラの校正の結果を残しておく（%LOCALAPPDATA%\WebcamFbt\backup\<更新前の版>\）。
// インストーラ自体はこれらのファイルに触れないが、新しい版がうまく読めなかったときに戻せるように
procedure BackupUserData();
var
  DataDir, BackupDir: String;
begin
  DataDir := ExpandConstant('{localappdata}\WebcamFbt');
  BackupDir := DataDir + '\backup\' + GetInstalledVersion();
  if not (FileExists(DataDir + '\settings.json') or FileExists(DataDir + '\calibration.json')) then Exit;
  if not ForceDirectories(BackupDir) then Exit;
  if FileExists(DataDir + '\settings.json') then
    FileCopy(DataDir + '\settings.json', BackupDir + '\settings.json', False);
  if FileExists(DataDir + '\calibration.json') then
    FileCopy(DataDir + '\calibration.json', BackupDir + '\calibration.json', False);
  Log('利用者データを残しました: ' + BackupDir);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and IsUpgrade() then BackupUserData();
end;

// 確認画面に「更新」であることを出す
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if IsUpgrade() then
    Result := '更新: ' + GetInstalledVersion() + ' → {#AppVersion}' + NewLine + NewLine;
  Result := Result + MemoDirInfo + NewLine + NewLine + MemoTasksInfo;
end;

// ---- アンインストール ----

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
