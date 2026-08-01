# AOI Image Viewer

> A high-performance AOI image review and annotation tool built with **Direct3D 11, Native C++, and .NET 8 WinForms**.

[![Version](https://img.shields.io/badge/version-1.0.0-blue.svg)](#version-information)
[![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey.svg)](#system-requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](#build-instructions)
[![Renderer](https://img.shields.io/badge/renderer-Direct3D%2011-green.svg)](#technical-architecture)

## Overview

AOI Image Viewer is a Windows desktop application designed for Automated Optical Inspection (AOI) image review, defect verification, annotation, measurement, and export workflows.

The application uses C# WinForms for the user interface and a Native C++ Direct3D 11 rendering engine for image display, zooming, panning, graphical overlays, and tiled loading of large BMP images. Compared with traditional GDI-based rendering, the application provides smoother interaction and clear pixel-level inspection at high zoom levels.

The current production release is **v1.0.0**. The software is ready for deployment and real-world use. Future releases will be improved based on user feedback, performance monitoring, and actual production workflows.

---

## Key Features

### Image Viewing

- Mouse-wheel zooming and image panning
- Region-based zoom using Zoom ROI
- Quick navigation to the previous or next image
- Real-time image coordinates and grayscale value at the cursor position
- Point sampling at high zoom levels to prevent blur caused by bilinear interpolation
- Pixel-grid alignment at high magnification for accurate pixel-level inspection
- Command-line image path support for integration with Windows file associations

### Large Image Handling

- Hybrid preview and full-resolution tile rendering for large BMP images
- Asynchronous background tile loading to keep the user interface responsive
- Visible-region prioritization with a controlled GPU upload budget per frame
- CPU and GPU tile caches with LRU-based cleanup
- Tile worker shutdown and recreation during image switching to prevent stale data and fast-switching crashes
- Dirty-rendering mechanism that avoids unnecessary full-speed redraws while the view is idle

### Annotation and Measurement

- Rectangle ROI
- Line measurement
- Circle and ellipse annotations
- Polygon annotations
- Fixed-center rectangles
- Annotation selection, visibility control, and geometry editing
- Real-time line length display during creation
- Real-time width and height display while creating circles or ellipses
- Separate management of manually created objects and metadata-loaded objects

### ROI Analysis

- ROI coordinates and dimensions
- Number of sampled pixels
- Mean and standard deviation
- Minimum and maximum values
- Blob count and largest blob area

### Image Processing

The following real-time image-processing operations are currently available:

- Linear Stretch
- Gamma
- Invert
- Threshold
- Otsu Threshold
- Adaptive Threshold
- Blur
- Median
- Sharpen
- Sobel
- Erode
- Dilate
- Morphology Open
- Morphology Close

Visible-tile processing is executed through iGPU/GPU shaders. CPU-based statistical analysis is deferred and limited to the selected ROI.

### Export

- **Export View** composites the original image with the currently visible annotation layers, avoiding ghosting or synchronization issues caused by screen capture
- **Batch Export** processes images and metadata from a source folder
- Images with metadata are exported as AOI report images
- Images without metadata are copied directly without unnecessary re-rendering
- Batch operations generate `BatchExportLog.txt`, including missing metadata, failed items, and summary statistics
- Reusable `AoiBatchExportApi` for integration with other .NET applications

---

## Supported Formats

### Image Formats

- BMP / DIB
- JPEG / JPG / JFIF
- PNG
- TIFF / TIF
- GIF
- WebP
- JPEG XR / JXR / WDP

> Large BMP files use a dedicated background tile streamer. The practical size limit for other formats depends on the decoder, available system memory, and image content.

### Metadata Sidecar Files

The application searches for sidecar files in the same folder as the image in the following order:

```text
<image>.meta.json
<image>.overlay.json
<image>.json
<image>.overlay.csv
<image>.csv
<image>.txt
<image>.aoi
```

Parsed metadata is converted into the shared `ViewerMetadata` and `ViewerOverlay` data models used by both single-image review and batch export.

---

## Metadata Formats

### JSON Example

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

Rectangle geometry supports the following common field formats:

- `x / y / width / height`
- `x / y / w / h`
- `left / top / right / bottom`
- `rect`
- `Rect`
- `bbox`

Polygon vertices may be provided through:

- `points`
- `vertices`
- `nodes`

### CSV Example

```csv
type,name,category,x,y,width,height,label
rectangle,Defect 1,defect,100,200,50,30,NG
rectangle,Defect 2,defect,300,400,20,10,NG
```

Polygon coordinates may be separated with semicolons:

```csv
type,name,category,points
polygon,ROI 1,roi,"100,100;200,100;180,180"
```

### Legacy AOI Compatibility

The current version remains compatible with selected legacy AOI fields, including:

- `defectList`
- `Rect`
- `drawPolygon`
- `dieCenterX`
- `dieCenterY`
- `diePos`
- `recipe_path`

These fields are converted into the unified metadata model through a compatibility layer, reducing the effort required to migrate existing data.

---

## Installation and Usage

### Installer

After the installer has been built, the default output path is:

```text
installer\Output\AOIImageViewerSetup_x64.exe
```

The installer can:

- Install AOI Image Viewer
- Create a Start menu shortcut
- Optionally create a desktop shortcut
- Register the application as a Windows image-viewer candidate
- Add the application to the **Open with** list for common image formats

Windows 10 and Windows 11 do not allow installers to forcefully change default applications without user confirmation. After installation, configure the default application using either of the following methods:

1. Right-click an image file and select **Open with → Choose another app**.
2. Select **AOI Image Viewer**.
3. Enable the option to always use this application for the selected file type.

Alternatively, open:

```text
Settings → Apps → Default apps → AOI Image Viewer
```

### Run Directly

```text
AOIImageViewer.exe
```

### Open an Image from the Command Line

```bat
AOIImageViewer.exe "D:\Images\sample.bmp"
```

---

## Build Instructions

### System Requirements

- Windows 10 or Windows 11, 64-bit
- Direct3D 11-compatible graphics device
- Visual Studio 2022
- Desktop development with C++ workload
- .NET 8 SDK
- MSBuild
- Inno Setup 6, required only when building the installer

### Build the Publish Package

Open a **Developer Command Prompt for Visual Studio 2022**, navigate to the project root, and run:

```bat
build_publish_x64.bat
```

The script will:

1. Build the Native C++ Direct3D 11 DLL using `Release / x64`.
2. Publish the .NET 8 WinForms application as a self-contained `win-x64` package.
3. Copy `NativeD3D11Viewer.dll` into the publish directory.

Output:

```text
publish\win-x64\AOIImageViewer.exe
```

### Build the Installer

After installing Inno Setup 6, run:

```bat
build_installer_x64.bat
```

Output:

```text
installer\Output\AOIImageViewerSetup_x64.exe
```

---

## AoiBatchExportApi

`AoiBatchExportApi` can be referenced independently by other .NET projects to batch-export AOI images and metadata.

Main entry point:

```csharp
AoiBatchExportApi.AoiBatchExporter.ExportFolder(
    sourceFolder,
    destinationFolder);
```

Basic example:

```csharp
using AoiBatchExportApi;

string sourceFolder = @"D:\AOI\Source";
string destinationFolder = @"D:\AOI\Export";

AoiBatchExporter.ExportFolder(sourceFolder, destinationFolder);
```

The destination folder is created automatically when it does not exist.

---

## Technical Architecture

```text
┌───────────────────────────────────────────────┐
│ CSharpViewer                                  │
│ .NET 8 / WinForms UI                          │
│ UI, metadata management, file browsing,       │
│ and export control                            │
└───────────────────────┬───────────────────────┘
                        │ P/Invoke
┌───────────────────────▼───────────────────────┐
│ NativeD3D11Viewer                             │
│ Native C++ / Direct3D 11                      │
│ Rendering, tile streaming, shaders, overlays, │
│ and user interaction                         │
└───────────────────────────────────────────────┘

┌───────────────────────────────────────────────┐
│ AoiBatchExportApi                             │
│ Metadata Parser / Batch Export                │
│ Shared data model for image review and export │
└───────────────────────────────────────────────┘
```

### Project Structure

```text
D3DImageViewer/
├─ CSharpViewer/                  # Main WinForms application
├─ NativeD3D11Viewer/             # Native C++ / D3D11 rendering engine
├─ AoiBatchExportApi/             # Metadata and batch-export API
├─ installer/                     # Inno Setup scripts
├─ build_publish_x64.bat          # Publish build script
├─ build_installer_x64.bat        # Installer build script
├─ icon.ico
└─ README.md
```

---

## Stability and Performance Design

Version 1.0.0 includes the following stability and performance measures:

- Native API entry points are serialized through mutual exclusion to prevent image loading, rendering, and mouse input from modifying shared state simultaneously
- Background tile workers are stopped and joined before a new image is opened
- Stale tile results from the previous image are discarded after image switching
- Duplicate tile requests are prevented during rapid panning and zooming
- Tile uploads per frame are limited to balance visual updates and input responsiveness
- CPU and GPU tile caches are cleaned according to recent usage
- Rapid use of Previous and Next avoids background access to replaced image state
- Metadata objects and manually created objects are managed separately to reduce accidental modification

---

## Recommended v1.0.0 Validation

Before production deployment, complete at least the following tests:

1. Open standard-size and large BMP images and confirm that the preview appears quickly.
2. Continuously pan and zoom while confirming that the interface remains responsive.
3. Zoom to defect-review magnification and confirm that full-resolution tiles progressively replace the preview.
4. Repeatedly use Zoom ROI and confirm that all required detail eventually loads.
5. Rapidly switch between multiple large images and confirm that tiles from the previous image are not displayed.
6. Validate JSON, CSV, and legacy AOI metadata rendering.
7. Confirm that exported images and annotation positions match the viewer.
8. Validate batch-export output files, copied files, and log statistics.
9. Validate installation, uninstallation, and Windows file associations.
10. Perform long-duration stability testing on production hardware using a representative image dataset.

---

## Known Limitations

- Only Windows x64 is currently supported.
- Windows default applications must be confirmed by the user and cannot be forced by the installer.
- Large BMP files use the complete tile-streaming pipeline; other compressed formats do not yet use the same tiled decoding architecture.
- Memory-mapped files and multi-resolution image pyramids have not yet been implemented and will be evaluated based on real performance requirements.
- Metadata fields may vary between data sources. Compatibility should be validated using actual production data before deployment.

---

## Version Information

### v1.0.0

Initial production release, including:

- Native Direct3D 11 image-rendering engine
- Background tile streamer for large BMP images
- Image zooming, panning, quick navigation, and pixel-level inspection
- Rectangle, line, circle, ellipse, and polygon annotations
- ROI statistics and basic image processing
- Generic metadata parser and legacy AOI compatibility layer
- Export View and Batch Export
- Self-contained Windows x64 deployment package
- Inno Setup installer and image file association support
- Stability fixes for rapid image switching and background worker operations

---

## Roadmap

Following the v1.0.0 production rollout, future development will be driven primarily by real-world user feedback and operational data.

Planned areas of evaluation and improvement include:

- Large-image loading speed and memory consumption
- Tile-cache behavior, GPU upload budgets, and background loading strategies
- Metadata format compatibility and field mapping
- Annotation workflows and user-interface usability
- Batch-export performance, layout, and error reporting
- Long-duration stability and high-volume image switching
- Multi-resolution image pyramids and memory-mapped I/O
- SDK and API capabilities for integration with other AOI systems

When reporting an issue, include the following information whenever possible:

- Software version
- Windows version
- CPU, GPU, and memory specifications
- Image format, dimensions, and file size
- Metadata sample
- Steps required to reproduce the issue
- Error messages, logs, or screenshots

---

## License and Usage

This project currently does not include a public software license. Unless explicit permission is granted by the project owner, the project must not be considered open-source software that may be freely copied, modified, distributed, or used for external commercial purposes.

---

**AOI Image Viewer v1.0.0**  
Designed for efficient AOI image review, annotation, analysis, and export on Windows x64.
