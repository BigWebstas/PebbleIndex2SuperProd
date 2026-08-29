; Inno Setup script for Index2SP — https://jrsoftware.org/isinfo.php
; Build:  iscc installer\Index2SP.iss   (after `dotnet publish` — see build.ps1)

#define AppName "Index2SP"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "Index2SP"
#define AppExeName "Index2SP.exe"
#define AppUrl "https://github.com/jwebstas/Index2SP"

; Where `dotnet publish -c Release -r win-x64 --self-contained true` drops the output.
#ifndef PublishDir
  #define PublishDir "..\src\Index2SP\bin\Release\net8.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{7BA73844-2059-45D8-9DFE-2CF6064C9D56}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=Index2SP-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user install: no UAC prompt, installs under %LOCALAPPDATA%\Programs when not admin.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Detects a running instance via the app's single-instance mutex and offers to close it.
AppMutex=Index2SP.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} automatically when I sign in to Windows"; GroupDescription: "Startup:"
Name: "runafterinstall"; Description: "Run {#AppName} now"; GroupDescription: "After installation:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.example.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Registry]
; Same value name the tray app's "Start at login" toggle manages, so the two stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Index2SP"; ValueData: """{app}\{#AppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Run {#AppName}"; \
  Flags: nowait postinstall skipifsilent; Tasks: runafterinstall

[UninstallRun]
; Best-effort: stop a running tray instance before removing files.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExeName} /F"; Flags: runhidden; RunOnceId: "KillIndex2SP"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

; Note: user data in %APPDATA%\Index2SP (config.json, logs) is intentionally left in place.
