; Mad Mod Studio installer (Inno Setup 6).
; Packages the self-contained publish output (the .NET runtime is included, nothing else to install).
;   iscc /DAppVersion=0.1.0 /DSourceDir=..\..\publish\MadModStudio /DOutputDir=..\..\publish build\installer\MadModStudio.iss

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\MadModStudio"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\publish"
#endif

[Setup]
AppId={{6B0E4C1A-9F3D-4E62-8C57-2D7A1F0B5E93}
AppName=Mad Mod Studio
AppVersion={#AppVersion}
AppVerName=Mad Mod Studio {#AppVersion}
AppPublisher=Mad Studios
AppSupportURL=https://github.com/MadGenius9/Mad-Studios
DefaultDirName={autopf}\Mad Mod Studio
DefaultGroupName=Mad Mod Studio
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the user may choose "all users" (asks for admin).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=MadModStudio-Setup-{#AppVersion}-x64
SetupIconFile=..\..\src\MadModStudio.App\Assets\MadModStudio.ico
UninstallDisplayIcon={app}\MadModStudio.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mad Mod Studio"; Filename: "{app}\MadModStudio.exe"
Name: "{autodesktop}\Mad Mod Studio"; Filename: "{app}\MadModStudio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MadModStudio.exe"; Description: "{cm:LaunchProgram,Mad Mod Studio}"; Flags: nowait postinstall skipifsilent

[Messages]
; Projects, history, settings and keys live in %LOCALAPPDATA%\MadModStudio and are kept on uninstall.
ConfirmUninstall=Remove Mad Mod Studio?%n%nYour projects, revision history, settings and stored API keys in %LOCALAPPDATA%\MadModStudio are kept. Delete that folder yourself if you no longer need them.
