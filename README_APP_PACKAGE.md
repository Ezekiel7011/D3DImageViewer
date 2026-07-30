# AOI Image Viewer - app packaging and file association

## What is included

This version is ready to be packaged like an image viewer app:

- `CSharpViewer\Program.cs` accepts an image path from command line arguments.
- `MainForm` loads that initial image on startup.
- `installer\AOIImageViewer.iss` registers AOI Image Viewer as a Windows image-viewer candidate.
- `build_installer_x64.bat` builds the publish folder and, if Inno Setup 6 is installed, compiles a setup exe.

## Build publish folder

Run from a Visual Studio Developer Command Prompt:

```bat
build_publish_x64.bat
```

Output:

```text
publish\win-x64\AOIImageViewer.exe
```

## Build installer

Install Inno Setup 6 first, then run:

```bat
build_installer_x64.bat
```

Output:

```text
installer\Output\AOIImageViewerSetup_x64.exe
```

## File association behavior

The installer registers these extensions:

`.bmp`, `.dib`, `.jpg`, `.jpeg`, `.jfif`, `.png`, `.tif`, `.tiff`, `.gif`, `.webp`, `.jxr`, `.wdp`

After installation, Windows 10/11 may still require the user to select it once:

1. Right-click an image file.
2. Choose **Open with** > **Choose another app**.
3. Select **AOI Image Viewer**.
4. Enable **Always use this app**.

Or use:

**Settings > Apps > Default apps > AOI Image Viewer**

Windows intentionally blocks installers from silently forcing default-file associations, so this registration makes the app selectable like IrfanView, but the user still confirms the default app.
