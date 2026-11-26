#define AppPublisher "Chris Field"
#define AppCopyright "Copyright (c) 2026 Chris Field"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\FolderSize-installer-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{E019DF81-392E-458D-A7B3-40F416DC1959}
AppName=FolderSize
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright={#AppCopyright}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright={#AppCopyright}
DefaultDirName={localappdata}\Programs\FolderSize
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=FolderSize-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\FolderSize.App\Assets\FolderSize.ico
UninstallDisplayIcon={app}\FolderSize.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\FolderSize"; Filename: "{app}\FolderSize.exe"

[Registry]
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FolderSize"; ValueType: string; ValueName: ""; ValueData: "Scan with FolderSize"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FolderSize"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\FolderSize.exe,0"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FolderSize"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FolderSize\command"; ValueType: string; ValueName: ""; ValueData: """{app}\FolderSize.exe"" ""%1\."""
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\FolderSize"; ValueType: string; ValueName: ""; ValueData: "Scan with FolderSize"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\FolderSize"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\FolderSize.exe,0"
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\FolderSize\command"; ValueType: string; ValueName: ""; ValueData: """{app}\FolderSize.exe"" ""%V\."""

[Run]
Filename: "{app}\FolderSize.exe"; Description: "Launch FolderSize"; Flags: nowait postinstall skipifsilent
