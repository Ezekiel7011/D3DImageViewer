# D3D11NativeHybridFull phase15

Fixes:
- Line and Circle creation now show live size labels while dragging.
  - Line: live length label.
  - Circle/Ellipse: live W/H label and temporary corner handles.
- Overlay Items and AOI Objects visibility changes only when the checkbox square is clicked.
  - Row click selects only.
  - Double click / row click will not toggle visibility.
- Keeps phase14 geometry synchronization and resize-anchor behavior.


Phase16 updates:
- Tool buttons now show active mode state. Zoom/Edit/Rect/Line/Circle will turn accent blue when selected.
- Works for mouse clicks and keyboard shortcuts.

## Phase17 note
This build changes the BMP loader quality policy: BMP files up to 8192 x 8192 and 128MP are now uploaded as full-resolution R8 textures instead of being forced into the 2048 preview path. Larger BMPs still use the preview path until the full tile renderer is completed.


## Phase 19 - Export / Batch Export
- Added **Export View** button to save the current viewport board as PNG.
- Added **Batch Export** button to export all images with AOI metadata in a source folder into a fixed-layout AOI report image.
- Added **AoiBatchExportApi** class library for reuse from other .NET projects.
- API entry point: `AoiBatchExportApi.AoiBatchExporter.ExportFolder(sourceFolder, destinationFolder)`
- Destination folder is created automatically when missing.
- Images without AOI metadata are skipped and are not re-rendered.


## Phase 19 compile fix
- AoiBatchExportApi now enables UseWindowsForms so System.Drawing types (Bitmap, Graphics, Font, Brush) resolve through the Windows Desktop SDK references.

## Phase 20 - Export fixes
- Batch Export no longer writes `Source` into the AOI Export right-side panel.
- Metadata images are exported as JPG for faster batch rendering.
- Images without metadata are still exported by copying the original file unchanged.
- `BatchExportLog.txt` is written to the destination folder and records only no-metadata files and failed files, plus summary counts.
- Single-image `Export View` was changed from screen capture to layer combine: it loads the original image and draws visible native overlay objects onto the image layer before saving. This avoids stale frame / screenshot residue.


## Phase 21 - Sharp Zoom Sampling
- Changed D3D11 texture sampler from `D3D11_FILTER_MIN_MAG_MIP_LINEAR` to `D3D11_FILTER_MIN_MAG_MIP_POINT`.
- This prevents high-zoom inspection from being blurred by bilinear filtering.
- Full-resolution BMP tiles and preview texture now use point sampling, matching IrfanView-style pixel inspection more closely.

## Phase 22 - Gray label visibility tune
- Reduced gray-level label box size.
- Reduced gray-level label background opacity.
- Reduced gray-level font size to make the image content less obstructed.

## Phase 23 - Dirty Render Loop
- Replaced continuous full-speed redraw with dirty rendering.
- Static image idle state no longer calls `NV_Render()` every 16 ms.
- Render is requested by load, resize, pan, zoom, overlay edit, processing changes, options changes, and tile-update interaction windows.
- During pan/zoom/edit the viewer keeps short continuous rendering windows to preserve smooth interaction and allow full-resolution tiles to settle.
- Debug overlay refreshes at low frequency when idle, instead of forcing full-speed redraw.

## Phase 24 - Pixel-grid zoom and tile completion
- High zoom now snaps the view to an integer pixel grid so large BMP full-resolution tiles do not display with non-grid sampling after wheel zoom or zoom-ROI.
- Tile/preview drawing snaps screen-space region coordinates at pixel inspection zoom to keep tile borders aligned with the image pixel grid.
- Dirty render mode now keeps rendering while native full-resolution tile queue still has pending tiles, preventing the viewer from stopping on preview-resolution data after zooming.
- Increased per-frame full-res tile uploads from 2 to 6 to fill the visible viewport faster while staying responsive on iGPU.
- Added `NV_GetPendingTileCount()` for UI/debug and render scheduling.

## Phase 25 - Background tile streamer
- Changed large BMP full-resolution tile loading from render-thread synchronous IO to a background worker streamer.
- Render thread now only plans visible tiles, uploads CPU-ready tiles under a per-frame budget, and draws resident tiles.
- Background worker pool reads BMP tile rows and places CPU-ready tiles into a ready queue.
- Tile queue now reports both pending worker requests and ready-to-upload tiles so dirty render continues until detail tiles are filled.
- Added simple CPU/GPU LRU trimming based on frame touch age.
- This is the first version of the professional-style streamer; a later pass can replace fread row IO with memory-mapped file reads and add multi-resolution pyramid layers.

## Phase 26 - Stabilization / Diagnostics
- Added tile diagnostics to the native debug overlay and C# status panel:
  - pending worker requests
  - ready-to-upload tiles
  - CPU cached tiles
  - GPU resident tiles
  - uploaded tiles per frame
  - duplicate tile request count
  - stale tile discard count
  - current render reason
- Added stale tile guards so background worker results from previous images are discarded after image switch.
- Added duplicate tile request guard using a queued-key set to prevent queue inflation during rapid pan/zoom.
- Added configurable tile upload budget and CPU/GPU tile cache limits via `NV_SetTileSettings`.
- Added queue trim cleanup so dropped queued tiles can be requested again later.
- Added render-reason handoff from C# dirty-render loop to native debug overlay via `NV_SetRenderReason`.

### Stress test checklist
1. Open a large 1GB BMP and confirm preview appears quickly.
2. Pan continuously for 20 seconds and confirm the UI remains interactive.
3. Zoom with mouse wheel into defect-level detail and confirm full-res tiles replace preview tiles.
4. Use Zoom ROI repeatedly and confirm TILE WORK / READY eventually returns near zero.
5. Stop interacting and confirm idle dirty-render recovery; FPS should no longer run at full rate forever.
6. Switch between multiple large images and confirm stale tile count may increase but old-image tiles never appear.
7. Rapidly pan/zoom and confirm duplicate request count may increase but queue size stays bounded.

Memory-mapped IO and a multi-resolution pyramid layer are still deferred until profiling shows a real bottleneck.


## Phase 27 - App packaging and Windows file association

This project can now be packaged like a normal Windows image viewer app.

### Command line open
`AOIImageViewer.exe "D:\Images\sample.bmp"` opens that image on startup. This is required for Windows file association.

### Publish folder
Run from a **Developer Command Prompt for VS 2022**:

```bat
build_publish_x64.bat
```

Output folder:

```text
publish\win-x64
```

Run directly:

```text
publish\win-x64\AOIImageViewer.exe
```

### Installer / file association
The installer script is:

```text
installer\AOIImageViewer.iss
```

Open it with Inno Setup and compile it after `build_publish_x64.bat` completes.

The installer registers the app under Windows Open With / Default Apps for:

```text
.bmp .jpg .jpeg .png .tif .tiff .gif .webp
```

Windows 10/11 may still require the user to choose the app once through:

```text
Settings -> Apps -> Default apps -> Choose defaults by file type
```

## Phase 28 - Generic Metadata Parser

The metadata reader is now a generic overlay parser instead of an AOI-only parser.

### Supported sidecar files
The viewer looks for metadata next to the image in this order:

```text
<image>.meta.json
<image>.overlay.json
<image>.json
<image>.overlay.csv
<image>.csv
<image>.txt
<image>.aoi
```

### Generic JSON format

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
      "points": [[100,100], [200,100], [180,180]]
    }
  ]
}
```

Rectangle geometry accepts common variants: `x/y/width/height`, `x/y/w/h`, `left/top/right/bottom`, `rect`, `Rect`, or `bbox`.
Polygon geometry accepts `points`, `vertices`, or `nodes`.

### Generic CSV format

```csv
type,name,category,x,y,width,height,label
rectangle,Defect 1,defect,100,200,50,30,NG
rectangle,Defect 2,defect,300,400,20,10,NG
```

Polygon CSV rows can use semicolon-separated point pairs:

```csv
type,name,category,points
polygon,ROI 1,roi,"100,100;200,100;180,180"
```

### Legacy compatibility
Legacy AOI JSON fields such as `defectList`, `Rect`, `drawPolygon`, `dieCenterX`, `dieCenterY`, `diePos`, and `recipe_path` are still supported through the same pipeline, but they are treated as adapter input and converted into the generic `ViewerMetadata` / `ViewerOverlay` model.

The shared parser lives in:

```text
AoiBatchExportApi/Metadata/ViewerMetadata.cs
```

Both the WinForms viewer and `AoiBatchExporter` now use this shared parser so single-image review and batch export stay consistent.
