; VariLab Windows installer (Inno Setup).
;
; Per-user install (no admin/UAC needed) into {localappdata}\VariLab — deliberately separate
; from the app's own user-data folder (%AppData%\VariLab, i.e. Roaming — config.json, session
; logs, the PSF-engine Python venv), which lives elsewhere and is never touched by install or
; uninstall. AppId is a fixed GUID so future versions upgrade in place instead of installing
; side-by-side — this also underpins the self-update feature (a downloaded installer run with
; /VERYSILENT reinstalls over the existing install rather than creating a duplicate).
;
; This is an additional distribution option alongside the existing portable win-x64 zip, not a
; replacement for it — both are built from the same publish\win-x64 output.
;
; Build: requires publish\win-x64\ to already exist (dotnet publish -c Release -r win-x64
; --self-contained true -o publish\win-x64), then run from the installer\ directory:
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" VariLab.iss
;
; MyAppVersion is NOT read from AppVersion.cs automatically — bump it here by hand alongside
; AppVersion.Version on every release, same as every other version-stamped location in this repo.

#define MyAppName "VariLab"
#define MyAppVersion "1.5.1"
#define MyAppPublisher "Art Trail"
#define MyAppURL "https://github.com/ArtTrail/VariLab"
#define MyAppExeName "VariLab.exe"

[Setup]
; Self-update relies on Inno's Restart Manager-based CloseApplications (default "yes" — not set
; explicitly here), which detects the file lock VariLab.exe holds on itself while running and
; closes it. Deliberately NOT using AppMutex: it triggers a different, older "please close it
; manually" prompt that /SUPPRESSMSGBOXES answers as Cancel, silently aborting the whole install
; before Restart Manager ever gets a chance (the two mechanisms don't compose). Also deliberately
; NOT relying on RestartApplications/ /RESTARTAPPLICATIONS to reopen the app afterward — it's
; best-effort and unreliable in practice; the [Run] section below (with skipifsilent removed)
; handles the relaunch instead. All matching TransitLab's proven installer configuration.
AppId={{5C51BDAC-BBAC-49F9-830F-920AA743B664}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=VariLab-Setup-v{#MyAppVersion}
SetupIconFile=..\Assets\VariLab.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; No skipifsilent: this also fires on a silent self-update install, which is what actually
; reopens VariLab afterward. A normal interactive install still shows this as the usual
; "Launch VariLab" wizard checkbox.
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall
