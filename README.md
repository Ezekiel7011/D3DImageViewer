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
