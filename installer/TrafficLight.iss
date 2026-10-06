; Inno Setup script for Windows TrafficLight.
; Build with build-installer.ps1, which publishes the app and passes AppVersion.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Windows TrafficLight"
#define AppExe "TrafficLight.exe"
#define AppUrl "https://github.com/PedroRuedas/Windows-TrafficLight"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "WindowsTrafficLight"
#define SettingsKey "Software\WindowsTrafficLight"

[Setup]
AppId={{6F1C2B7E-3D4A-4E59-9B8F-7A2C5D1E0F43}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=PedroRuedas
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Windows TrafficLight
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist\installer
OutputBaseFilename=WindowsTrafficLight-Setup-{#AppVersion}
SetupIconFile=..\assets\TrafficLight.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.StartupTask=Iniciar automaticamente com o Windows
en.StartupTask=Start automatically with Windows
ptbr.LaunchApp=Abrir o Windows TrafficLight agora
en.LaunchApp=Launch Windows TrafficLight now

[Tasks]
Name: "startup"; Description: "{cm:StartupTask}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Registry]
; Start with Windows (the app reads StartupOptOut so it won't re-register itself if the user declined).
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValue}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup
Root: HKCU; Subkey: "{#SettingsKey}"; ValueType: dword; ValueName: "StartupOptOut"; ValueData: 0; Tasks: startup
Root: HKCU; Subkey: "{#RunKey}"; ValueType: none; ValueName: "{#RunValue}"; Flags: deletevalue; Tasks: not startup
Root: HKCU; Subkey: "{#SettingsKey}"; ValueType: dword; ValueName: "StartupOptOut"; ValueData: 1; Tasks: not startup
Root: HKCU; Subkey: "{#SettingsKey}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[Code]
// The app lives in the tray and has no window to close, so stop it explicitly
// before files are replaced or removed.
procedure StopApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopApp();
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');
end;
