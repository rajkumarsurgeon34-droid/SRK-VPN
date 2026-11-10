; SRK VPN for Windows — Inno Setup script
; Bundles the official OpenVPN community installer (GPLv2) and installs it if OpenVPN is not present.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6B1F3C2E-8D47-4E5A-9C1B-5A2E7F0D9B31}
AppName=SRK VPN
AppVersion={#AppVersion}
AppVerName=SRK VPN {#AppVersion}
AppPublisher=ShazVerse Technologies
AppPublisherURL=https://shazverse.com
DefaultDirName={autopf}\SRK VPN
DefaultGroupName=SRK VPN
DisableProgramGroupPage=yes
OutputDir=..\out
OutputBaseFilename=SRK-VPN-Setup
SetupIconFile=srkvpn.ico
UninstallDisplayIcon={app}\SRKVPN.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\deps\openvpn.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\SRK VPN"; Filename: "{app}\SRKVPN.exe"; IconFilename: "{app}\srkvpn.ico"
Name: "{autodesktop}\SRK VPN"; Filename: "{app}\SRKVPN.exe"; IconFilename: "{app}\srkvpn.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\SRKVPN.exe"; Description: "Open SRK VPN"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im SRKVPN.exe /f"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
function NeedsOpenVpn: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\OpenVPN\bin\openvpn.exe'));
end;

procedure InstallOpenVpn;
var
  Code: Integer;
  Msi: String;
begin
  Msi := ExpandConstant('{tmp}\openvpn.msi');
  WizardForm.StatusLabel.Caption := 'Installing the OpenVPN engine and network driver...';
  { Engine + TAP-Windows driver only; if this OpenVPN version names its features differently,
    fall back to its standard install. }
  Exec('msiexec.exe', '/i "' + Msi + '" ADDLOCAL=OpenVPN,Drivers,Drivers.TAPWindows6 /passive /norestart',
       '', SW_SHOW, ewWaitUntilTerminated, Code);
  if (Code <> 0) and (Code <> 3010) and NeedsOpenVpn then
    Exec('msiexec.exe', '/i "' + Msi + '" /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code);
  if NeedsOpenVpn then
    MsgBox('The OpenVPN engine could not be installed (code ' + IntToStr(Code) + ').' + #13#10 +
           'Install OpenVPN from openvpn.net, then open SRK VPN again.', mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and NeedsOpenVpn then
    InstallOpenVpn;
end;
