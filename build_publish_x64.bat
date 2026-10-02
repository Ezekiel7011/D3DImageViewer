@echo off
setlocal

rem Some launchers provide both PATH and Path. MSBuild's native tool tasks reject
rem that duplicate environment, so collapse it to the normal Windows spelling.
set "NORMALIZED_PATH=%PATH%"
set "PATH="
set "Path=%NORMALIZED_PATH%"
set "NORMALIZED_PATH="

cd /d "%~dp0"

set "MSBUILD="
for /f "delims=" %%I in ('where msbuild.exe 2^>nul') do if not defined MSBUILD set "MSBUILD=%%I"

if not defined MSBUILD (
  if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
    for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%I"
  )
)

if not defined MSBUILD (
  echo ERROR: MSBuild was not found.
  echo Install Visual Studio with Desktop development with C++, then try again.
  exit /b 1
)

set "PUBLISH_DIR=%CD%\publish\win-x64"

echo Using MSBuild: %MSBUILD%
"%MSBUILD%" "NativeD3D11Viewer\NativeD3D11Viewer.vcxproj" /m /p:Configuration=Release /p:Platform=x64
if errorlevel 1 exit /b 1

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"

dotnet publish "CSharpViewer\CSharpViewer.csproj" -c Release -r win-x64 --self-contained true --output "%PUBLISH_DIR%"
if errorlevel 1 exit /b 1

copy /Y "CSharpViewer\bin\x64\Release\net8.0-windows\NativeD3D11Viewer.dll" "%PUBLISH_DIR%\NativeD3D11Viewer.dll" >nul
if errorlevel 1 exit /b 1

if not exist "%PUBLISH_DIR%\AOIImageViewer.exe" (
  echo ERROR: Publish completed without AOIImageViewer.exe.
  exit /b 1
)

echo.
echo Publish complete: %PUBLISH_DIR%
echo You can run AOIImageViewer.exe directly or build the installer using installer\AOIImageViewer.iss
exit /b 0
