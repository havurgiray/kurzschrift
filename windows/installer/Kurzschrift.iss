; Kurzschrift for Windows installer (Inno Setup 6). Built by build.ps1:
;   ISCC /DArch=x64|arm64 /DVersion=1.2.0 /DSource=<publish folder> installer\Kurzschrift.iss
; Installs for the current user only (no administrator rights needed).

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Version
  #define Version "1.2.0"
#endif

[Setup]
AppId={{6C1E3E7A-3F4B-4E2E-9B7D-4B5D2A8C9F11}
AppName=Kurzschrift
AppVersion={#Version}
AppVerName=Kurzschrift {#Version}
AppPublisher=Giray Havur
AppPublisherURL=https://github.com/havurgiray/kurzschrift
AppSupportURL=https://github.com/havurgiray/kurzschrift/issues
DefaultDirName={localappdata}\Programs\Kurzschrift
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=Kurzschrift-{#Version}-windows-{#Arch}-setup
SetupIconFile=..\Kurzschrift\Assets\Kurzschrift.ico
UninstallDisplayIcon={app}\Kurzschrift.exe
UninstallDisplayName=Kurzschrift
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\..\LICENSE
CloseApplications=yes
AppMutex=Local\Kurzschrift-single-instance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Kurzschrift when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Kurzschrift's license and the notices of everything it includes (notices\make.py builds them).
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\notices\windows.txt"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Kurzschrift"; Filename: "{app}\Kurzschrift.exe"
Name: "{userdesktop}\Kurzschrift"; Filename: "{app}\Kurzschrift.exe"; Tasks: desktopicon

[Registry]
; kurzschrift:// links (start, stop, toggle, bookmark, ...), like the Mac version's URL scheme.
Root: HKCU; Subkey: "Software\Classes\kurzschrift"; ValueType: string; ValueName: ""; ValueData: "URL:Kurzschrift"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\kurzschrift"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\kurzschrift\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Kurzschrift.exe"",0"
Root: HKCU; Subkey: "Software\Classes\kurzschrift\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Kurzschrift.exe"" ""%1"""
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Kurzschrift"; ValueData: """{app}\Kurzschrift.exe"" --background"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Kurzschrift.exe"; Description: "Start Kurzschrift"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, models and transcripts stay; only the program goes.
Type: dirifempty; Name: "{app}"
