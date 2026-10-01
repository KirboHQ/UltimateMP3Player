; Inno Setup script. Built by ..\build.ps1 -Installer (or -Release), which first publishes the
; self-contained app into .\staging together with the engines: nothing else is needed on the target PC.

#define AppName "Ultimate MP3 Player"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "UltimateMP3Player.exe"

[Setup]
AppId={{3F6C2A71-9D4E-4B8A-A1C5-7E2D90B4F618}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Davide
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install: no admin rights, and the app can update itself and its engines.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=UltimateMP3Player-Setup-{#AppVersion}
SetupIconFile=..\src\UltimateMP3Player\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern dark windows11 includetitlebar
WizardSizePercent=110
DisableWelcomePage=no
; The language chosen here is also the app's language.
ShowLanguageDialog=yes
UsePreviousLanguage=yes
WizardImageFile=images\large-100.png,images\large-125.png,images\large-150.png,images\large-200.png,images\large-250.png
WizardSmallImageFile=images\small-100.png,images\small-125.png,images\small-150.png,images\small-200.png,images\small-250.png
; The app keeps running in the notification area: setup asks to close it first.
AppMutex=UltimateMP3Player.SingleInstance
CloseApplications=yes
RestartApplications=no
; .ump packs open with the app (Explorer refreshes their icon).
ChangesAssociations=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[Messages]
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%nYour offline, ad-free music player: download songs and whole playlists from YouTube Music, Spotify, SoundCloud, YouTube, TikTok, Instagram and hundreds of other sites.%n%nNo administrator rights or other programs needed: everything is included.
it.WelcomeLabel2=Verrà installato [name/ver] sul computer.%n%nIl tuo lettore musicale offline e senza pubblicità: scarica brani e playlist intere da YouTube Music, Spotify, SoundCloud, YouTube, TikTok, Instagram e centinaia di altri siti.%n%nNon servono diritti di amministratore né altri programmi: tutto il necessario è incluso.

[CustomMessages]
en.PackType=Ultimate MP3 Player pack
it.PackType=Pacchetto di Ultimate MP3 Player

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "staging\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; Icon of the .ump packs.
Source: "..\src\UltimateMP3Player\pack.ico"; DestDir: "{app}"; Flags: ignoreversion
; yt-dlp is compared by version: a newer one updated by the app is kept.
Source: "staging\engines\yt-dlp.exe"; DestDir: "{app}\engines"
Source: "staging\engines\*"; DestDir: "{app}\engines"; Excludes: "yt-dlp.exe"; Flags: ignoreversion

[Registry]
; Read by the app on its first start ("en" or "it").
Root: HKCU; Subkey: "Software\Ultimate MP3 Player"; ValueType: string; ValueName: "Language"; ValueData: "{language}"; Flags: uninsdeletekey
; .ump packs (the app writes the same keys at every start, see Services\FileAssociation.cs).
Root: HKCU; Subkey: "Software\Classes\.ump"; ValueType: string; ValueName: ""; ValueData: "UltimateMP3Player.Pack"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.ump\OpenWithProgids"; ValueType: string; ValueName: "UltimateMP3Player.Pack"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\UltimateMP3Player.Pack"; ValueType: string; ValueName: ""; ValueData: "{cm:PackType}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\UltimateMP3Player.Pack\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\pack.ico"",0"
Root: HKCU; Subkey: "Software\Classes\UltimateMP3Player.Pack\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Self-update (silent, /relaunch=1): start the app again.
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchArgs}"; Flags: nowait; Check: IsRelaunch

[Code]
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

function RelaunchArgs(Param: String): String;
begin
  Result := '';
  if ExpandConstant('{param:profile|}') <> '' then
    Result := '--profile ' + ExpandConstant('{param:profile|}');
  if ExpandConstant('{param:bg|0}') = '1' then
    Result := Result + ' --background';
end;

[UninstallDelete]
; Files the app updated itself. Music and the library (playlists, profiles) are never touched.
Type: filesandordirs; Name: "{app}\engines"
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.new"
Type: files; Name: "{localappdata}\Ultimate MP3 Player\pack.ico"
