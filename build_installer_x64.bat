@echo off
setlocal
cd /d "%~dp0"

set "ISCC="
for /f "delims=" %%I in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%I"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
  echo ERROR: Inno Setup 6 compiler was not found.
  echo Please install Inno Setup 6, then open installer\AOIImageViewer.iss and click Compile.
  exit /b 1
)

call "%~dp0build_publish_x64.bat"
if errorlevel 1 exit /b 1

"%ISCC%" "installer\AOIImageViewer.iss"
if errorlevel 1 exit /b 1

if not exist "%CD%\installer\Output\AOIImageViewerSetup_x64.exe" (
  echo ERROR: Installer compilation completed without the expected setup file.
  exit /b 1
)

echo.
echo Installer complete: %CD%\installer\Output\AOIImageViewerSetup_x64.exe
exit /b 0
