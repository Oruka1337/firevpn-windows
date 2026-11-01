; FireVPN для Windows: установщик одним файлом (Inno Setup 6).
; Собирает scripts\firevpn-build.ps1 после публикации программы в папку FireVPN:
;   ISCC.exe /DAppVersion=1.0.1 /DSourceDir=<папка FireVPN> /DOutputDir=<куда> scripts\firevpn-setup.iss
; Ставит без прав администратора в %LOCALAPPDATA%\Programs\FireVPN — туда же пишет программа
; обновления (AmazTool) и настройки (guiConfigs).

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "D:\firevpn\win-release\FireVPN"
#endif
#ifndef OutputDir
  #define OutputDir "D:\firevpn\win-release"
#endif

[Setup]
AppId={{6E3C2B5A-91F4-4D3B-9C1E-F17EF1FE0A57}
AppName=FireVPN
AppVersion={#AppVersion}
AppVerName=FireVPN {#AppVersion}
AppPublisher=FireVPN
AppPublisherURL=https://fire-vpn.online
AppSupportURL=https://t.me/VpnFire57_bot
AppUpdatesURL=https://fire-vpn.online/download/#windows
DefaultDirName={localappdata}\Programs\FireVPN
DefaultGroupName=FireVPN
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=FireVPN-{#AppVersion}-setup
SetupIconFile=..\v2rayN\v2rayN\Resources\firevpn.ico
UninstallDisplayIcon={app}\FireVPN.exe
UninstallDisplayName=FireVPN
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "guiConfigs\*,guiLogs\*,guiTemps\*,binConfigs\*"

[Icons]
Name: "{autoprograms}\FireVPN"; Filename: "{app}\FireVPN.exe"
Name: "{autodesktop}\FireVPN"; Filename: "{app}\FireVPN.exe"; Tasks: desktopicon

[Registry]
; ссылки firevpn://install-sub?url=... из бота и личного кабинета (программа потом обновляет путь сама)
Root: HKCU; Subkey: "Software\Classes\firevpn"; ValueType: string; ValueName: ""; ValueData: "URL:FireVPN"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\firevpn"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\firevpn\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\FireVPN.exe"",0"
Root: HKCU; Subkey: "Software\Classes\firevpn\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\FireVPN.exe"" ""%1"""

[Run]
Filename: "{app}\FireVPN.exe"; Description: "Запустить FireVPN"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; настройки, подписка и журналы — программа создаёт их сама, установщик о них не знает
Type: filesandordirs; Name: "{app}\guiConfigs"
Type: filesandordirs; Name: "{app}\guiLogs"
Type: filesandordirs; Name: "{app}\guiTemps"
Type: filesandordirs; Name: "{app}\binConfigs"

[Code]
const
  InetKey = 'Software\Microsoft\Windows\CurrentVersion\Internet Settings';

// Закрыть FireVPN и его ядра (xray, sing-box), запущенные из папки программы
procedure StopFireVpn();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM FireVPN.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -Command "Get-Process xray,sing-box -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ''' +
    ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

// FireVPN закрыли принудительно — системный прокси Windows мог остаться указывать на него; выключаем, чтобы не пропал интернет
procedure ClearFireVpnProxy();
var
  Server: String;
begin
  if RegQueryStringValue(HKCU, InetKey, 'ProxyServer', Server) and (Pos('127.0.0.1:10808', Server) > 0) then
    RegWriteDWordValue(HKCU, InetKey, 'ProxyEnable', 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopFireVpn();
  ClearFireVpnProxy();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopFireVpn();
    ClearFireVpnProxy();
  end;
end;
