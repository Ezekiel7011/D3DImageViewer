#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#ifdef NATIVE_EXPORTS
#define NV_API extern "C" __declspec(dllexport)
#else
#define NV_API extern "C" __declspec(dllimport)
#endif

enum NVToolMode { NV_TOOL_PAN=0, NV_TOOL_ZOOM_RECT=1, NV_TOOL_PIXEL=2, NV_TOOL_ROI=3, NV_TOOL_MEASURE=4, NV_TOOL_SELECT=5, NV_TOOL_CIRCLE=6 };
enum NVProcessingOperator { NV_PROC_NONE=0, NV_PROC_THRESHOLD=1, NV_PROC_INVERT=2, NV_PROC_BLUR=3, NV_PROC_SOBEL=4, NV_PROC_LINEAR_STRETCH=5, NV_PROC_GAMMA=6, NV_PROC_OTSU=7, NV_PROC_ADAPTIVE=8, NV_PROC_MEDIAN=9, NV_PROC_SHARPEN=10, NV_PROC_ERODE=11, NV_PROC_DILATE=12, NV_PROC_OPEN=13, NV_PROC_CLOSE=14 };
enum NVDisplayMode { NV_DISPLAY_BASE=0, NV_DISPLAY_PROCESSED=1, NV_DISPLAY_BLEND=2 };
enum NVOverlayType { NV_OVERLAY_ROI=0, NV_OVERLAY_MEASURE=1, NV_OVERLAY_CIRCLE=2, NV_OVERLAY_AOI_DIE_CENTER=3, NV_OVERLAY_AOI_DIE_CORNER=4, NV_OVERLAY_AOI_DEFECT=5 };

struct NVImageInfo { int width; int height; int bitDepth; long long fileSize; wchar_t format[64]; wchar_t path[520]; };
struct NVDiagnostics { float fps; float renderMs; float scale; float offsetX; float offsetY; int imageWidth; int imageHeight; int overlayCount; int gpuUploadCount; };
struct NVOverlayInfo { int id; int type; int visible; float x1; float y1; float x2; float y2; float x3; float y3; float angle; wchar_t name[64]; };
struct NVRoiStats { int valid; int x; int y; int width; int height; int pixels; double mean; int minValue; int maxValue; double stddev; int blobCount; int largestBlobArea; int largestBlobX; int largestBlobY; int largestBlobW; int largestBlobH; };

NV_API void* __stdcall NV_Create(HWND hwnd);
NV_API void __stdcall NV_Destroy(void* viewer);
NV_API int __stdcall NV_LoadImage(void* viewer, const wchar_t* path, NVImageInfo* info);
NV_API void __stdcall NV_Resize(void* viewer, int width, int height);
NV_API void __stdcall NV_Render(void* viewer);
NV_API void __stdcall NV_FitToWindow(void* viewer);
NV_API void __stdcall NV_OneToOne(void* viewer);
NV_API void __stdcall NV_SetToolMode(void* viewer, int mode);
NV_API void __stdcall NV_SetOptions(void* viewer, int crosshair, int grayText, int debugOverlay);
NV_API void __stdcall NV_SetProcessing(void* viewer, int op, int displayMode, int threshold, double gamma, int radius);
NV_API void __stdcall NV_MouseDown(void* viewer, int button, int x, int y, int modifiers);
NV_API void __stdcall NV_MouseMove(void* viewer, int x, int y, int modifiers);
NV_API void __stdcall NV_MouseUp(void* viewer, int button, int x, int y, int modifiers);
NV_API void __stdcall NV_MouseWheel(void* viewer, int delta, int x, int y);
NV_API int __stdcall NV_GetMouseImageInfo(void* viewer, int sx, int sy, int* ix, int* iy, int* gray);
NV_API void __stdcall NV_GetDiagnostics(void* viewer, NVDiagnostics* diag);
NV_API int __stdcall NV_GetOverlayCount(void* viewer);
NV_API int __stdcall NV_GetOverlayInfo(void* viewer, int index, NVOverlayInfo* info);
NV_API int __stdcall NV_GetSelectedOverlayInfo(void* viewer, NVOverlayInfo* info);
NV_API void __stdcall NV_SetOverlayVisible(void* viewer, int id, int visible);
NV_API void __stdcall NV_SelectOverlay(void* viewer, int id);
NV_API void __stdcall NV_DeleteOverlay(void* viewer, int id);
NV_API void __stdcall NV_ClearOverlays(void* viewer);
NV_API int __stdcall NV_AnalyzeSelectedRoi(void* viewer, NVRoiStats* stats, int* histogram256, double* projX, int projXCapacity, double* projY, int projYCapacity);
NV_API int __stdcall NV_ComputeOtsuThreshold(void* viewer);
NV_API int __stdcall NV_GetLastError(void* viewer, wchar_t* buffer, int capacity);
NV_API int __stdcall NV_AddOverlay(void* viewer, int type, float x, float y, float w, float h, float angle, const wchar_t* name);
NV_API void __stdcall NV_UpdateOverlay(void* viewer, int id, float x, float y, float w, float h, float angle);
