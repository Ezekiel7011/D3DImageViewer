using System;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace CSharpViewer;

internal enum ToolMode { Pan=0, ZoomRect=1, Pixel=2, Roi=3, Measure=4, Select=5, Circle=6 }
internal enum ProcOp { None=0, Threshold=1, Invert=2, Blur=3, Sobel=4, LinearStretch=5, Gamma=6, OtsuThreshold=7, AdaptiveThreshold=8, Median=9, Sharpen=10, MorphologyErode=11, MorphologyDilate=12, MorphologyOpen=13, MorphologyClose=14 }
internal enum DisplayMode { Base=0, Processed=1, Blend=2 }
internal enum OverlayType { Roi=0, Measure=1, Circle=2, AoiDieCenter=3, AoiDieCorner=4, AoiDefect=5 }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NVImageInfo
{
    public int width, height, bitDepth;
    public long fileSize;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string format;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 520)] public string path;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NVDiagnostics
{
    public float fps, renderMs, scale, offsetX, offsetY;
    public int imageWidth, imageHeight, overlayCount, gpuUploadCount;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NVOverlayInfo
{
    public int id, type, visible;
    public float x1, y1, x2, y2, x3, y3, angle;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NVRoiStats
{
    public int valid, x, y, width, height, pixels;
    public double mean;
    public int minValue, maxValue;
    public double stddev;
    public int blobCount, largestBlobArea, largestBlobX, largestBlobY, largestBlobW, largestBlobH;
}

internal static class NativeMethods
{
    private const string Dll = "NativeD3D11Viewer.dll";
    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, (libraryName, assembly, searchPath) =>
        {
            if (!string.Equals(libraryName, Dll, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            string baseDir = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, Dll),
                Path.Combine(baseDir, "..", "..", "..", Dll),
                Path.Combine(baseDir, "..", "..", "..", "..", "NativeD3D11Viewer", "bin", Dll)
            };

            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                    return NativeLibrary.Load(fullPath);
            }

            return IntPtr.Zero;
        });
    }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern IntPtr NV_Create(IntPtr hwnd);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_Destroy(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] public static extern int NV_LoadImage(IntPtr viewer, string path, out NVImageInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_Resize(IntPtr viewer, int width, int height);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_Render(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_FitToWindow(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_OneToOne(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_SetToolMode(IntPtr viewer, int mode);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_SetOptions(IntPtr viewer, int crosshair, int grayText, int debugOverlay);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_SetProcessing(IntPtr viewer, int op, int displayMode, int threshold, double gamma, int radius);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_MouseDown(IntPtr viewer, int button, int x, int y, int modifiers);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_MouseMove(IntPtr viewer, int x, int y, int modifiers);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_MouseUp(IntPtr viewer, int button, int x, int y, int modifiers);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_MouseWheel(IntPtr viewer, int delta, int x, int y);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_GetMouseImageInfo(IntPtr viewer, int sx, int sy, out int ix, out int iy, out int gray);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_GetDiagnostics(IntPtr viewer, out NVDiagnostics diag);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_GetOverlayCount(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_GetOverlayInfo(IntPtr viewer, int index, out NVOverlayInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_GetSelectedOverlayInfo(IntPtr viewer, out NVOverlayInfo info);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_SetOverlayVisible(IntPtr viewer, int id, int visible);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_SelectOverlay(IntPtr viewer, int id);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_DeleteOverlay(IntPtr viewer, int id);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_ClearOverlays(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_ComputeOtsuThreshold(IntPtr viewer);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern int NV_AnalyzeSelectedRoi(IntPtr viewer, out NVRoiStats stats, int[] histogram256, double[] projX, int projXCapacity, double[] projY, int projYCapacity);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] public static extern int NV_AddOverlay(IntPtr viewer, int type, float x, float y, float w, float h, float angle, string name);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] public static extern void NV_UpdateOverlay(IntPtr viewer, int id, float x, float y, float w, float h, float angle);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)] public static extern int NV_GetLastError(IntPtr viewer, StringBuilder buffer, int capacity);
}
