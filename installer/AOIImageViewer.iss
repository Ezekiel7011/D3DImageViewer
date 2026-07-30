; Inno Setup script for AOI Image Viewer
; Build steps:
;   1. Run build_publish_x64.bat from a Visual Studio Developer Command Prompt.
;   2. Compile this script with Inno Setup 6, or run build_installer_x64.bat.
;
; Windows 10/11 note:
;   Installers are allowed to register an app as an image viewer, but Windows does not
;   allow installers to silently force themselves as the default viewer. After install,
;   choose AOI Image Viewer from: Settings > Apps > Default apps, or right-click an
;   image > Open with > Choose another app.

#define MyAppName "AOI Image Viewer"
#define MyAppExeName "AOIImageViewer.exe"
#define MyAppPublisher "GALLANT MICRO. MACHINING CO., LTD."
#define MyAppVersion "1.0.0"
#define MyProgId "AOIImageViewer.Image"
#define MyAppIconFile "..\icon.ico"

[Setup]
AppId={{A9F96B8D-9C20-4697-A4C9-47F7D5C66A91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\AOI Image Viewer
DefaultGroupName={#MyAppName}
OutputDir=Output
OutputBaseFilename=AOIImageViewerSetup_x64
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
ChangesAssociations=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyAppIconFile}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked
Name: "assoc"; Description: "Register AOI Image Viewer for common image file types"; GroupDescription: "File associations:"; Flags: checkedonce

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Make AOIImageViewer.exe discoverable from Windows shell and command line.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

; Register as a Windows Default Apps candidate.
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "AOIImageViewer"; ValueData: "Software\AOIImageViewer\Capabilities"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "D3D11 AOI image viewer for large BMP, JPG, PNG, TIFF, GIF and WEBP images."; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc

; File association capabilities shown under Settings > Apps > Default apps.
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".bmp";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".dib";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpg";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jpeg"; ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jfif"; ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".png";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tif";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".tiff"; ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".gif";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webp"; ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jxr";  ValueData: "{#MyProgId}"; Tasks: assoc
Root: HKLM; Subkey: "Software\AOIImageViewer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wdp";  ValueData: "{#MyProgId}"; Tasks: assoc

; ProgID: defines icon and open command. %1 is the image path passed to Program.Main(args).
Root: HKCR; Subkey: "{#MyProgId}"; ValueType: string; ValueName: ""; ValueData: "AOI Image Viewer Image"; Flags: uninsdeletekey; Tasks: assoc
Root: HKCR; Subkey: "{#MyProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: assoc
Root: HKCR; Subkey: "{#MyProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: assoc

; Add AOI Image Viewer to right-click > Open with candidates for each extension.
Root: HKCR; Subkey: ".bmp\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".dib\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".jpg\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".jpeg\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".jfif\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".png\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".tif\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".tiff\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".gif\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".webp\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".jxr\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKCR; Subkey: ".wdp\OpenWithProgids";  ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
