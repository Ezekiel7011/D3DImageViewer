@echo off
setlocal
cd /d "%~dp0"

call build_publish_x64.bat
if errorlevel 1 exit /b 1

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (
  echo Inno Setup 6 compiler was not found.
  echo Please install Inno Setup 6, then open installer\AOIImageViewer.iss and click Compile.
  exit /b 1
)

"%ISCC%" installer\AOIImageViewer.iss
if errorlevel 1 exit /b 1

echo.
echo Installer complete: %CD%\installer\Output\AOIImageViewerSetup_x64.exe
