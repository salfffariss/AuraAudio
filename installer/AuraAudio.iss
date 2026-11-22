; =============================================================================
; AuraAudio Windows Setup Wizard (Inno Setup 6)
; =============================================================================
; Build via:
;   powershell -ExecutionPolicy Bypass -File tools\release\build_release.ps1
; Or directly with Inno Setup Compiler (ISCC.exe):
;   ISCC.exe /DMyAppVersion=2.1.0 /DPublishDir=..\artifacts\publish\staging /DOutputDir=..\artifacts\releases installer\AuraAudio.iss
;
; Installs per-user by default (%LocalAppData%\Programs\AuraAudio, no UAC)
; with optional elevation dialog for all-users (C:\Program Files\AuraAudio).
; Runtime data (library.db, thumbnail cache, logs) lives under %LocalAppData%\AuraAudio,
; keeping the install directory strictly read-only safe in both modes.
; =============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "2.1.0"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\staging"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\releases"
#endif

#define MyAppName "AuraAudio"
#define MyAppPublisher "AuraAudio SoundLab"
#define MyAppURL "https://github.com/salfffariss/AuraAudio"
#define MyAppExeName "AuraAudio.exe"

[Setup]
; Fixed GUID so upgrades cleanly replace previous versions in Windows Installed Apps
AppId={{7B6D9A3F-4E21-4C8B-9F1A-3D8E6A2C5109}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
; Per-user install by default (no admin/UAC prompt), with optional all-users dialog
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=AuraAudio-v{#MyAppVersion}-Setup-win-x64
SetupIconFile=..\Assets\AuraAudio.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
ChangesEnvironment=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\AuraAudio.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Bit-Perfect Desktop Audio Player & Forensic Inspector"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Comment: "Bit-Perfect Desktop Audio Player & Forensic Inspector"

[Registry]
; Enable launching via Win+R -> auraaudio and standard Windows App Paths discovery
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[UninstallDelete]
; Clean regenerable disk cache & logs on uninstall while preserving library.db & playlists
Type: filesandordirs; Name: "{localappdata}\AuraAudio\Cache"
Type: filesandordirs; Name: "{localappdata}\AuraAudio\Logs"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
