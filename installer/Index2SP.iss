; Inno Setup script for Index2SP — https://jrsoftware.org/isinfo.php
; Build:  iscc installer\Index2SP.iss                    (self-contained variant)
;         iscc /DFrameworkDependent installer\Index2SP.iss  (needs .NET 8 runtimes)
; Normally driven by build.ps1, which passes /DAppVersion and /DPublishDir.

#define AppName "Index2SP"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "Index2SP"
#define AppExeName "Index2SP.exe"
#define AppUrl "https://github.com/BigWebstas/Index2SP"

#ifndef PublishDir
  #ifdef FrameworkDependent
    #define PublishDir "..\artifacts\publish\framework-dependent"
  #else
    #define PublishDir "..\artifacts\publish\self-contained"
  #endif
#endif

#ifdef FrameworkDependent
  #define VariantSuffix "-fd"
  #define VariantLabel " (requires .NET 8 runtime)"
#else
  #define VariantSuffix ""
  #define VariantLabel ""
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
UninstallDisplayName={#AppName}{#VariantLabel}
OutputDir=..\dist
OutputBaseFilename=Index2SP-Setup{#VariantSuffix}-{#AppVersion}
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

#ifdef FrameworkDependent
[Code]
const
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime';

function DotNetRuntimesPresent(): Boolean;
var
  ResultCode: Integer;
  TmpFile: String;
  Contents: AnsiString;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\index2sp-runtimes.txt');
  if Exec(ExpandConstant('{cmd}'), '/C dotnet --list-runtimes > "' + TmpFile + '" 2>&1',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringFromFile(TmpFile, Contents) then
      Result := (Pos('Microsoft.WindowsDesktop.App 8.', Contents) > 0) and
                (Pos('Microsoft.AspNetCore.App 8.', Contents) > 0);
  end;
end;

function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not DotNetRuntimesPresent() then
  begin
    if MsgBox('This is the framework-dependent build of Index2SP. It needs both:' #13#10
            + '  •  .NET Desktop Runtime 8' #13#10
            + '  •  ASP.NET Core Runtime 8' #13#10 #13#10
            + 'They were not detected on this PC. Open the download page now?' #13#10
            + '(Install the "ASP.NET Core Runtime" and ".NET Desktop Runtime" x64 packages, '
            + 'then run this installer again. Or use the self-contained installer instead.)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOW, ewNoWait, ResultCode);
    Result := False;
  end;
end;
#endif
