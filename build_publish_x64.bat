@echo off
setlocal
cd /d "%~dp0"

where msbuild >nul 2>nul
if errorlevel 1 (
  echo MSBuild not found. Run from "Developer Command Prompt for VS 2022".
  exit /b 1
)

msbuild NativeD3D11Viewer\NativeD3D11Viewer.vcxproj /p:Configuration=Release /p:Platform=x64
if errorlevel 1 exit /b 1

dotnet publish CSharpViewer\CSharpViewer.csproj -c Release -r win-x64 --self-contained true /p:PublishProfile=win-x64-folder
if errorlevel 1 exit /b 1

if not exist publish\win-x64 mkdir publish\win-x64
copy /Y CSharpViewer\bin\x64\Release\net8.0-windows\NativeD3D11Viewer.dll publish\win-x64\NativeD3D11Viewer.dll

echo.
echo Publish complete: %CD%\publish\win-x64
echo You can run AOIImageViewer.exe directly or build the installer using installer\AOIImageViewer.iss
