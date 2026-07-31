; BandwidthDesk - Inno Setup installer script
;
; Invoked from build.bat with:
;   iscc /DAppVersion=<version> /DSourceDir=<publish dir> /DOutputDir=<build dir> BandwidthDesk.iss
;
; All three /D defines are required; defaults below are only for IDE usage.

#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\build\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\build"
#endif

#define AppName        "BandwidthDesk"
#define AppPublisher   "BandwidthDesk"
#define AppExeName     "BandwidthDesk.exe"
#define AppId          "{{B4D3F1C2-1A2B-4E5F-9A1D-BANDWIDTHDESK}}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=
CloseApplications=force
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=BandwidthDesk-{#AppVersion}-setup-x64
SetupIconFile=..\src\BandwidthDesk.App\Resources\icon.ico
LicenseFile=..\LICENSE
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Application binaries (everything dotnet publish produced except the kernel driver).
Source: "{#SourceDir}\*"; Excludes: "WinDivert64.sys"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Do not force an identical loaded kernel driver to be overwritten. If a newer or
; genuinely different driver is packaged and Windows still has the old image locked,
; schedule the replacement for reboot instead of showing an access-denied retry dialog.
Source: "{#SourceDir}\WinDivert64.sys"; DestDir: "{app}"; Flags: replacesameversion restartreplace skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
; Per-user runtime data is intentionally left in %LOCALAPPDATA%\BandwidthDesk so
; reinstalls preserve the user's rules and profiles. Delete it manually if you
; want a clean uninstall.
Type: filesandordirs; Name: "{app}\logs"
