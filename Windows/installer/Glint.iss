[Setup]
AppId=Glint.Windows
AppName=Glint Windows
AppVersion={#GlintVersion}
AppPublisher=Glint contributors
AppPublisherURL=https://github.com/3171248381kerry-cell/Glint-Windows
VersionInfoVersion={#GlintVersion}
DefaultDirName={localappdata}\Programs\Glint
DisableProgramGroupPage=yes
ShowLanguageDialog=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
UninstallDisplayIcon={app}\Glint.exe
SetupIconFile=..\Glint.Windows\Assets\glint.ico
OutputBaseFilename=Glint-{#GlintVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\Glint.Windows.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#GlintPayload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Glint"; Filename: "{app}\Glint.exe"

[Run]
Filename: "{app}\Glint.exe"; Description: "{cm:LaunchProgram,Glint}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Glint', StartupCommand)
     and (CompareText(StartupCommand, '"' + ExpandConstant('{app}\Glint.exe') + '" --background') = 0) then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Glint');
end;
