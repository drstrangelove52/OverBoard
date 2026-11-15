; Inno-Setup-Skript für den OverBoard-Installer (wird vom Release-Ablauf aufgerufen: iscc /DAppVersion=1.2.3 installer\OverBoard.iss)
; Erwartet das veröffentlichte Programm samt Firmware in publish\ (Ausgabe von dotnet publish plus Ordner firmware\).
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6F1B7E52-3C0A-4D2E-9B7A-0A3B04D00001}
AppName=OverBoard
AppVersion={#AppVersion}
AppPublisher=OverBoard
DefaultDirName={autopf}\OverBoard
DefaultGroupName=OverBoard
PrivilegesRequired=lowest
OutputDir=..\out
OutputBaseFilename=OverBoard-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\OverBoardAgent.exe
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\OverBoard"; Filename: "{app}\OverBoardAgent.exe"

[Run]
Filename: "{app}\OverBoardAgent.exe"; Description: "{cm:LaunchProgram,OverBoard}"; Flags: nowait postinstall skipifsilent
