; DeviceHub installer (Inno Setup 6). Built by tools\pack.ps1, which passes /DAppVersion=... and /DSourceDir=...
; Per-user install (no admin). Plugin UI resources stay in %LocalAppData%\DeviceHub\plugins and are
; resolved at runtime, so nothing is written into the install directory.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif

[Setup]
AppId={{6F2B7C1E-4D7A-4B53-9C2E-1D6A8E0B5F31}
AppName=DeviceHub
AppVersion={#AppVersion}
AppVerName=DeviceHub {#AppVersion}
AppPublisher=DeviceHub
DefaultDirName={localappdata}\Programs\DeviceHub
DefaultGroupName=DeviceHub
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=DeviceHub-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\DeviceHub.App.exe
UninstallDisplayName=DeviceHub

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; App + Agent + PluginHost (self-contained) and the bundled GK65 plugin package (plugins\*.dhplugin),
; which the Agent installs into %LocalAppData%\DeviceHub\plugins on first start.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\DeviceHub"; Filename: "{app}\DeviceHub.App.exe"
Name: "{autodesktop}\DeviceHub"; Filename: "{app}\DeviceHub.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\DeviceHub.App.exe"; Description: "{cm:LaunchProgram,DeviceHub}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM DeviceHub.App.exe"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM DeviceHub.PluginHost.exe"; Flags: runhidden; RunOnceId: "KillHost"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM DeviceHub.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"

[Code]
procedure KillDeviceHub();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DeviceHub.App.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DeviceHub.PluginHost.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DeviceHub.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Upgrading over a running copy: stop App, plugin hosts and Agent so files can be replaced. }
  KillDeviceHub();
  Result := '';
end;
