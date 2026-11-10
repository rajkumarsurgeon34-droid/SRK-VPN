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
Filename: "msiexec.exe"; \
  Parameters: "/i ""{tmp}\openvpn.msi"" ADDLOCAL=OpenVPN,Drivers,Drivers.TAPWindows6 /passive /norestart"; \
  StatusMsg: "Installing the OpenVPN engine and network driver..."; \
  Flags: waituntilterminated; Check: NeedsOpenVpn
Filename: "{app}\SRKVPN.exe"; Description: "Open SRK VPN"; Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im SRKVPN.exe /f"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
function NeedsOpenVpn: Boolean;
begin
  Result := not FileExists(ExpandConstant('{commonpf64}\OpenVPN\bin\openvpn.exe'));
end;
