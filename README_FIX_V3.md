# Fix v3 notes

This package contains the v2 UI/grouping fixes plus an additional native stability fix for rapid Prev/Next image switching.

## Rapid switching crash

The Debug Assertion `vector subscript out of range` was most likely caused by native background tile workers reading BMP tile state (`path`, `iw/ih`, `bmpPalette`, stride, tile cache) while `NV_LoadImage` was replacing those fields for the next image.

Changes:

- `NV_LoadImage` now stops and joins tile worker threads before mutating image/tile state, then restarts them after load completes.
- Native API entry points now serialize access with `NativeViewer::apiMtx`, so render/mouse/overlay calls cannot run concurrently with image load.
- Tile queue trimming no longer reads `loadQueue.size()` outside the tile-cache mutex.
- BMP palette lookup is guarded with `bmpPalette.size() > idx` to avoid debug vector assertions if state is inconsistent or malformed.

## Overlay grouping

Manual created objects remain in `Overlay Items`; metadata-seeded objects are tracked by `_metadataOverlayIds` and shown in `MetaData Obj`.
