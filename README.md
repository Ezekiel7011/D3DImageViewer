# AOI Image Viewer

> A high-performance AOI image review, annotation, analysis, and export application built with Native C++, Direct3D 11, and .NET 8 WinForms.

## Overview

AOI Image Viewer is a Windows desktop application designed for Automated Optical Inspection (AOI) image review and defect inspection workflows.

The application uses:

- **C# / .NET 8 WinForms** for the desktop UI, metadata management, navigation, and export workflows.
- **Native C++ / Direct3D 11** for image rendering, interaction, graphical overlays, GPU image processing, and large-image tile rendering.
- **P/Invoke** as the interface between the managed UI and native rendering engine.

The viewer is designed to support responsive interaction with large inspection images while providing pixel-level inspection, annotation, ROI analysis, metadata visualization, and batch export capabilities.

---

## Features

### Image Viewing

- Mouse-wheel zoom
- Image panning
- Zoom-to-region using Zoom ROI
- Fit-to-window
- 1:1 pixel view
- Previous / next image navigation within the current folder
- Drag-and-drop image loading
- Command-line image opening
- Real-time image coordinates under the mouse cursor
- Real-time grayscale value display
- Optional center crosshair
- High-magnification pixel inspection
- Image information display

### Large Image Rendering

Large BMP images use a dedicated tiled rendering path instead of loading the complete full-resolution image into a single GPU texture.

The implementation includes:

- Preview image rendering
- Full-resolution tile streaming
- Background tile loading
- Visible-region tile prioritization
- Controlled tile uploads per frame
- CPU tile cache
- GPU tile cache
- Cache cleanup based on recent usage
- Duplicate tile-request prevention
- Stale tile-result rejection after image switching
- Background worker shutdown during image replacement
- Dirty rendering to avoid unnecessary continuous redraws while idle

This allows the viewer to remain responsive while navigating large AOI images.

---

## Annotation and Measurement

The viewer supports manually created graphical objects including:

- Rectangle ROI
- Line measurement
- Circle
- Fixed-center rectangle

Created objects can be:

- Selected
- Moved or edited
- Shown or hidden individually
- Deleted
- Cleared
- Modified through geometry parameters

The geometry editor exposes values such as:

- X
- Y
- Width / Length
- Height
- Angle

The fixed-center rectangle tool allows a rectangle of a specified width and height to remain centered on the image.

Metadata-loaded AOI objects are managed separately from manually created objects.

---

## ROI Analysis

A selected ROI can be analyzed without processing the entire image on the CPU.

Available ROI information includes:

- ROI position
- ROI width and height
- Sampled pixel count
- Mean grayscale value
- Standard deviation
- Minimum grayscale value
- Maximum grayscale value
- Blob count
- Largest blob area

CPU-side analysis is performed only for the selected ROI.

---

## Image Processing

Image processing is applied to visible image tiles through the Direct3D rendering pipeline.

Available operations include:

### Enhancement

- Original
- Linear Stretch
- Gamma
- Invert

### Thresholding

- Manual Threshold
- Otsu Threshold
- Adaptive Threshold

### Filtering and Edge Detection

- Blur
- Median
- Sharpen
- Sobel

### Morphology

- Erode
- Dilate
- Open
- Close

The viewer provides three display modes:

- Base
- Processed Result
- Blend

Processing parameters such as threshold, gamma, and adaptive radius can be adjusted from the UI.

---

## AOI Metadata

The viewer can automatically load metadata sidecar files associated with an image.

Supported sidecar naming patterns include:

```text
<image>.meta.json
<image>.overlay.json
<image>.json
<image>.overlay.csv
<image>.csv
<image>.txt
<image>.aoi
```

Parsed metadata is converted into a shared internal representation and displayed as native Direct3D overlays.

### Metadata Objects

Supported metadata geometry includes:

- Rectangle objects
- Defect rectangles
- Polygon objects
- Die position
- Die center

Metadata objects can be individually shown or hidden from the Defects panel.

The metadata panel displays information such as:

- Source
- Created time
- Result
- Object count
- Defect count
- Polygon count
- Warnings
- Error
- Image path
- Recipe

---

## JSON Metadata Example

```json
{
  "properties": {
    "created": "2026-06-13 12:00:00",
    "result": "NG",
    "recipe": "sample_recipe"
  },
  "overlays": [
    {
      "type": "rectangle",
      "name": "Defect 1",
      "category": "defect",
      "x": 100,
      "y": 200,
      "width": 50,
      "height": 30,
      "attributes": {
        "bin": "A",
        "score": "0.98"
      }
    },
    {
      "type": "polygon",
      "name": "ROI 1",
      "category": "roi",
      "points": [
        [100, 100],
        [200, 100],
        [180, 180]
      ]
    }
  ]
}
```

Rectangle geometry can be read from several common field conventions:

```text
x / y / width / height
x / y / w / h
left / top / right / bottom
rect
Rect
bbox
```

Polygon vertices can be provided through fields such as:

```text
points
vertices
nodes
```

---

## CSV Metadata Example

```csv
type,name,category,x,y,width,height,label
rectangle,Defect 1,defect,100,200,50,30,NG
rectangle,Defect 2,defect,300,400,20,10,NG
```

Polygon coordinates can also be represented using semicolon-separated points:

```csv
type,name,category,points
polygon,ROI 1,roi,"100,100;200,100;180,180"
```

---

## Legacy AOI Metadata Compatibility

The metadata loader includes compatibility handling for existing AOI fields such as:

```text
defectList
Rect
drawPolygon
dieCenterX
dieCenterY
diePos
recipe_path
```

These fields are converted into the same internal metadata and overlay representation used by the viewer.

---

## Export

### Export View

The current image can be exported together with its visible overlay layers.

The export path reconstructs the output from the source image and overlay geometry rather than relying on a screenshot of the viewport.

Supported export formats include:

- JPEG
- PNG

### Batch Export

The application also supports folder-based batch export.

The batch exporter:

- Processes images from a source folder
- Loads matching metadata
- Renders AOI report images when metadata is available
- Copies images directly when metadata rendering is not required
- Generates a batch export log
- Records failed and missing-metadata items
- Produces export summary information

---

## AoiBatchExportApi

`AoiBatchExportApi` is separated from the WinForms viewer and can be referenced by another .NET application.

Basic usage:

```csharp
using AoiBatchExportApi;

string sourceFolder = @"D:\AOI\Source";
string destinationFolder = @"D:\AOI\Export";

AoiBatchExporter.ExportFolder(
    sourceFolder,
    destinationFolder);
```

The destination folder is created automatically when it does not already exist.

---

## Supported Image Formats

The viewer accepts the following image formats:

- BMP / DIB
- JPEG / JPG / JFIF
- PNG
- TIFF / TIF
- GIF
- WebP
- JPEG XR / JXR / WDP

Large BMP images use the dedicated tile-streaming rendering path.

---

## Application Architecture

```text
┌───────────────────────────────────────────────┐
│ CSharpViewer                                  │
│ .NET 8 / WinForms                             │
│                                               │
│ UI                                            │
│ Image navigation                              │
│ Metadata management                           │
│ ROI information                               │
│ Export control                                │
└───────────────────────┬───────────────────────┘
                        │
                     P/Invoke
                        │
┌───────────────────────▼───────────────────────┐
│ NativeD3D11Viewer                             │
│ Native C++ / Direct3D 11                      │
│                                               │
│ Image rendering                               │
│ Tile streaming                                │
│ GPU processing                                │
│ Overlay rendering                             │
│ Zoom / pan / interaction                      │
│ CPU / GPU tile cache                          │
└───────────────────────────────────────────────┘

┌───────────────────────────────────────────────┐
│ AoiBatchExportApi                             │
│                                               │
│ Metadata parsing                              │
│ Shared AOI data model                         │
│ Overlay export                                │
│ Batch export                                  │
└───────────────────────────────────────────────┘
```

---

## Project Structure

```text
D3DImageViewer/
├─ CSharpViewer/
│  └─ .NET 8 WinForms application
│
├─ NativeD3D11Viewer/
│  └─ Native C++ Direct3D 11 rendering engine
│
├─ AoiBatchExportApi/
│  └─ Metadata and batch-export library
│
├─ installer/
│  └─ Inno Setup installer configuration
│
├─ build_publish_x64.bat
├─ build_installer_x64.bat
├─ icon.ico
└─ README.md
```

---

## Rendering and Stability Design

The native viewer contains several mechanisms for maintaining responsiveness during image loading and navigation:

- Native API synchronization for shared rendering state
- Background tile workers
- Worker shutdown before replacing an image
- Generation-based stale tile rejection
- Duplicate tile-request prevention
- Controlled GPU tile uploads per frame
- CPU and GPU cache limits
- Cache cleanup based on usage
- Visible-region prioritization
- Deferred folder scanning
- Dirty-frame rendering
- Safe rapid previous / next image switching

The UI can also display internal rendering diagnostics including:

- FPS
- Render time
- Current scale
- Pending worker requests
- Ready tile count
- Tile uploads per frame
- CPU cache usage
- GPU cache usage
- Duplicate requests
- Stale tile discards
- Overlay count

---

## Installation

The project includes an Inno Setup configuration for creating a Windows x64 installer.

Build the installer with:

```bat
build_installer_x64.bat
```

Output:

```text
installer\Output\AOIImageViewerSetup_x64.exe
```

The installer supports:

- Application installation
- Start Menu shortcut
- Optional desktop shortcut
- Registration as a Windows image-viewer candidate
- Registration in the Windows **Open with** list

Windows requires the user to confirm default application associations.

---

## Build

### Requirements

- Windows 10 or Windows 11 x64
- Direct3D 11-compatible graphics device
- Visual Studio 2022
- Desktop development with C++ workload
- .NET 8 SDK
- MSBuild
- Inno Setup 6 for installer generation

### Publish

Run:

```bat
build_publish_x64.bat
```

The build script:

1. Builds `NativeD3D11Viewer` using Release / x64.
2. Publishes the .NET 8 WinForms application for `win-x64`.
3. Copies the native Direct3D DLL into the publish directory.

Output:

```text
publish\win-x64\AOIImageViewer.exe
```

---

## Usage

Run directly:

```bat
AOIImageViewer.exe
```

Open an image from the command line:

```bat
AOIImageViewer.exe "D:\Images\sample.bmp"
```

The command-line image path can also be used by Windows file associations.

---

## Technology Stack

- C#
- .NET 8
- WinForms
- C++
- Direct3D 11
- HLSL
- P/Invoke
- Windows Imaging Component
- Inno Setup

---

## License

This repository currently does not include a public software license.

Unless explicit permission is granted by the repository owner, the source code should not be considered freely licensed for copying, modification, redistribution, or external commercial use.
