[Setup]
AppName=Blue Jay Player
AppVersion=2.0.0
AppPublisher=Blue Jay Digital
DefaultDirName={autopf}\BlueJayPlayer
DefaultGroupName=Blue Jay Player
UninstallDisplayIcon={app}\BlueJayPlayer.exe
OutputDir=.
OutputBaseFilename=BlueJayPlayer_v2.0.0_Setup
SetupIconFile=Assets\square_one_app_logo.ico
LicenseFile=Terms.txt
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "bin\Release\net9.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Icons]
Name: "{group}\Blue Jay Player"; Filename: "{app}\BlueJayPlayer.exe"; IconFilename: "{app}\BlueJayPlayer.exe"
Name: "{autodesktop}\Blue Jay Player"; Filename: "{app}\BlueJayPlayer.exe"; IconFilename: "{app}\BlueJayPlayer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\BlueJayPlayer.exe"; Description: "{cm:LaunchProgram,Blue Jay Player}"; Flags: nowait postinstall skipifsilent
