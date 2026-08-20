#ifndef MyAppVersion
  #define MyAppVersion "0.1.2"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\release-staging\installer-payload"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif

#define MyAppName "ZCompare"
#define MyAppPublisher "Zcube7"
#define MyAppURL "https://github.com/Zcube7/ZCompare"
#define MyAppExeName "ZCompare.App.exe"

[Setup]
AppId={{F5E2B33C-C6B5-4A29-94D2-C68B71993936}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\ZCompare
DefaultGroupName=ZCompare
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ZCompare-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\assets\branding\zcompare.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=ZCompare installer
VersionInfoProductName={#MyAppName}
VersionInfoCopyright=Copyright (C) 2026 Zcube7
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.zh-CN.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ZCompare"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ZCompare"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 ZCompare"; Flags: nowait postinstall skipifsilent
