#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include "NativeViewer.h"
#include "ShaderVSMain.h"
#include "ShaderPSMain.h"
#include "ShaderVSLine.h"
#include "ShaderPSLine.h"
#include <d3d11.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <chrono>
#include <cmath>
#include <algorithm>
#include <queue>
#include <numeric>
#include <cstdio>
#include <cstring>
#include <utility>
#include <cstdint>
#include <cctype>
#include <map>
#include <deque>
#include <mutex>
#include <atomic>
#include <thread>
#include <condition_variable>
#include <set>
#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")

using Microsoft::WRL::ComPtr;

// ===== Local Types and Tile Cache =====

struct Vtx {
    float x, y, u, v;
};

struct LineVtx {
    float x, y, r, g, b, a;
};

struct CBView {
    float sx, sy, ox, oy;
    float invW, invH, proc, display;
    float threshold, gamma, radius, blend;
    float imgW, imgH, pad0, pad1;
};

struct Overlay {
    int id;
    int type;
    bool visible;
    bool readOnly;
    float x1, y1, x2, y2, x3, y3, angle;
    std::wstring name;
    std::vector<float> poly;
};

struct TextItem {
    float x, y, w, h, pt;
    COLORREF color;
    bool center;
    std::wstring text;
};

struct OverlayDeletePredicate {
    int id;
    explicit OverlayDeletePredicate(int value) : id(value) {}
    bool operator()(const Overlay& o) const { return o.id == id && !o.readOnly; }
};
struct OverlayClearPredicate {
    bool operator()(const Overlay& o) const { return !o.readOnly; }
};

// Phase 9: large-image tile-cache scaffold.
// The current renderer still keeps the fast low-resolution preview path, but these
// structures are the basis for the next step: visible-tile planning, CPU cache
// accounting, and per-frame GPU upload throttling. This keeps UI interaction
// responsive while background workers fill high-resolution tiles.
struct TileKey {
    int level;
    int x;
    int y;
    TileKey() : level(0), x(0), y(0) {}
    TileKey(int l, int tx, int ty) : level(l), x(tx), y(ty) {}
    bool operator<(const TileKey& rhs) const {
        if (level != rhs.level) return level < rhs.level;
        if (y != rhs.y) return y < rhs.y;
        return x < rhs.x;
    }
};
struct TileCacheEntry {
    std::vector<unsigned char> gray;
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11ShaderResourceView> srv;
    int imageX;
    int imageY;
    int w;
    int h;
    int byteCount;
    unsigned int lastTouchFrame;
    bool queued;
    bool gpuResident;
    TileCacheEntry() : imageX(0), imageY(0), w(0), h(0), byteCount(0), lastTouchFrame(0), queued(false), gpuResident(false) {}
};
struct TileCacheState {
    std::map<TileKey, TileCacheEntry> cpuTiles;
    // loadQueue is consumed by background worker threads. pending contains CPU-ready tiles
    // that the render thread may upload to the GPU under a per-frame budget.
    std::deque<TileKey> loadQueue;
    std::deque<TileKey> pending;
    std::set<TileKey> queuedKeys;
    std::mutex mtx;
    std::condition_variable cv;
    int tileSize;
    int maxUploadPerFrame;
    int preloadMargin;
    int cpuLimitBytes;
    int gpuLimitBytes;
    unsigned int frameId;
    int uploadedLastFrame;
    int duplicateRequestCount;
    int staleDiscardCount;
    int maxQueueCount;
    TileCacheState() : tileSize(1024), maxUploadPerFrame(12), preloadMargin(1),
        cpuLimitBytes(512 * 1024 * 1024), gpuLimitBytes(384 * 1024 * 1024), frameId(0),
        uploadedLastFrame(0), duplicateRequestCount(0), staleDiscardCount(0), maxQueueCount(512) {}
    void Reset() {
        std::lock_guard<std::mutex> lock(mtx);
        cpuTiles.clear();
        loadQueue.clear();
        pending.clear();
        queuedKeys.clear();
        frameId = 0;
        uploadedLastFrame = 0;
        duplicateRequestCount = 0;
        staleDiscardCount = 0;
        cv.notify_all();
    }
    void TouchVisibleRange(int level, int tx0, int ty0, int tx1, int ty1) {
        std::lock_guard<std::mutex> lock(mtx);
        ++frameId;
        for (int ty = ty0; ty <= ty1; ++ty) {
            for (int tx = tx0; tx <= tx1; ++tx) {
                TileKey key(level, tx, ty);
                std::map<TileKey, TileCacheEntry>::iterator it = cpuTiles.find(key);
                if (it == cpuTiles.end()) {
                    TileCacheEntry e;
                    e.queued = true;
                    e.lastTouchFrame = frameId;
                    cpuTiles.insert(std::make_pair(key, e));
                    if (queuedKeys.insert(key).second) loadQueue.push_back(key);
                    else duplicateRequestCount++;
                } else {
                    it->second.lastTouchFrame = frameId;
                    if (it->second.queued || it->second.gpuResident || !it->second.gray.empty()) {
                        duplicateRequestCount++;
                    } else {
                        it->second.queued = true;
                        if (queuedKeys.insert(key).second) loadQueue.push_back(key);
                        else duplicateRequestCount++;
                    }
                }
            }
        }
        cv.notify_all();
    }
};

// ===== Helper Functions, Shader, and Constants =====

static bool IsAoiType(int t)
{
    return t == NV_OVERLAY_AOI_DIE_CENTER || t == NV_OVERLAY_AOI_DIE_CORNER || t == NV_OVERLAY_AOI_DEFECT;
}

static bool IsFixedCenterRectType(int t)
{
    return t == NV_OVERLAY_FIXED_CENTER_RECT;
}

static bool IsDrawPolygonType(int t)
{
    return t == NV_OVERLAY_DRAW_POLYGON;
}

static int ClampInt(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
static float ClampFloat(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }
static double ClampDouble(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
static bool IsNear(float x, float y, float hx, float hy, float tol) { return fabsf(x - hx) <= tol && fabsf(y - hy) <= tol; }
static void RotateAround(float cx, float cy, float angleDeg, float x, float y, float& rx, float& ry)
{
    float a = angleDeg * 0.017453292519943295f;
    float ca = cosf(a), sa = sinf(a);
    float dx = x - cx, dy = y - cy;
    rx = cx + dx * ca - dy * sa;
    ry = cy + dx * sa + dy * ca;
}
static void InvRotateAround(float cx, float cy, float angleDeg, float x, float y, float& rx, float& ry)
{
    RotateAround(cx, cy, -angleDeg, x, y, rx, ry);
}


// Viewer settings migrated from legacy C# CViewerSettings.
static const float kZoomStep = 1.25f;
static const float kMaxZoomScale = 48.0f;
static const float kMinZoomScaleFactor = 0.5f;
static const float kGrayValueTextZoomThreshold = 45.0f;
static const float kPixelGridZoomThreshold = 45.0f;
static const int kMaxGrayValueTextCount = 800;
static const int kPreviewMaxTextureSize = 2048;
static const int kMaxFullTextureSize = 5120;
static const long long kMaxFullTexturePixels = 128LL * 1024LL * 1024LL;
static const int kGpuCacheLimitMb = 384;
static const int kCpuCacheLimitMb = 512;
static const int kTileSize = 1024;
static const int kDefaultMaxTileUploadPerFrame = 12;
static const int kPreloadTileMargin = 1;
static const double kFullResolutionTileZoomThreshold = 0.25;
static const int kMaxBackgroundTileLoadQueueCount = 512;

// ===== NativeViewer Class =====

// Owns the D3D11/WIC renderer used by the WinForms front-end through the exported C API.
class NativeViewer {
public:
    // Window and device lifetime are owned by this native object; C# only holds an opaque pointer.
    HWND hwnd{};
    int cw = 1, ch = 1;
    std::wstring lastError;

    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<IDXGISwapChain> swap;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11VertexShader> vs, lvs;
    ComPtr<ID3D11PixelShader> ps, lps;
    ComPtr<ID3D11InputLayout> il, lil;
    ComPtr<ID3D11Buffer> vb, cb, lvb;
    ComPtr<ID3D11SamplerState> samp;
    ComPtr<ID3D11BlendState> alphaBlend;

    ComPtr<ID2D1Factory> d2dFactory;
    ComPtr<IDWriteFactory> writeFactory;
    ComPtr<ID2D1RenderTarget> d2dTarget;
    ComPtr<ID2D1SolidColorBrush> d2dBrush;
    ComPtr<IDWriteTextFormat> textFormat;

    // Image state is stored as an 8-bit grayscale preview plus optional BMP tile metadata.
    ComPtr<ID3D11Texture2D> tex;
    ComPtr<ID3D11ShaderResourceView> srv;
    std::vector<unsigned char> pixels;
    int iw = 0, ih = 0, texW = 0, texH = 0, sampleStep = 1, bitDepth = 8;
    std::wstring fmt, path;
    long long fileSize = 0;
    int uploadCount = 0;

    bool bmpTileReady = false, bmpTopDown = false;
    int bmpBpp = 0;
    long long bmpOffBits = 0, bmpRowStride = 0;
    std::vector<RGBQUAD> bmpPalette;

    // View, tool, and overlay state are mutated by mouse/keyboard calls from WinForms.
    float scale = 1, fitScale = 1, ox = 0, oy = 0;
    int tool = NV_TOOL_ZOOM_RECT;
    bool showCross = false, showGray = true, showDebug = false;
    int proc = 0, display = 0, threshold = 128, radius = 12;
    double gamma = 0.7;
    bool dragging = false;
    int dragKind = 0, downX = 0, downY = 0, lastX = 0, lastY = 0;
    float startOx = 0, startOy = 0;
    float roiStartX = 0, roiStartY = 0;
    float selStartX1 = 0, selStartY1 = 0, selStartX2 = 0, selStartY2 = 0;
    int dragHandle = 0;

    std::vector<Overlay> overlays;
    std::vector<TextItem> textItems;
    TileCacheState tileCache;
    int nextId = 1, selected = -1;
    std::vector<std::thread> tileWorkers;
    std::atomic<bool> tileWorkerStop{false};
    std::atomic<int> tileEpoch{0};
    std::mutex apiMtx;
    std::atomic<int> renderReason{0};
    std::chrono::high_resolution_clock::time_point lastFrame = std::chrono::high_resolution_clock::now();
    float fps = 0, renderMs = 0;

    ~NativeViewer()
    {
        StopTileWorkers();
    }

    bool Init(HWND h)
    {
        hwnd = h;

        RECT rc{};
        GetClientRect(hwnd, &rc);
        cw = std::max(1L, rc.right - rc.left);
        ch = std::max(1L, rc.bottom - rc.top);

        DXGI_SWAP_CHAIN_DESC sd{};
        sd.BufferCount = 2;
        sd.BufferDesc.Width = cw;
        sd.BufferDesc.Height = ch;
        sd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.OutputWindow = hwnd;
        sd.SampleDesc.Count = 1;
        sd.Windowed = TRUE;
        sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        UINT flg = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1 };
        D3D_FEATURE_LEVEL got{};
        HRESULT hr = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            flg,
            levels,
            3,
            D3D11_SDK_VERSION,
            &sd,
            &swap,
            &dev,
            &got,
            &ctx);

        if (FAILED(hr)) return Err(L"D3D11CreateDeviceAndSwapChain failed");
        if (!CreateRT()) return false;

        HRESULT d2dHr = D2D1CreateFactory(
            D2D1_FACTORY_TYPE_SINGLE_THREADED,
            __uuidof(ID2D1Factory),
            nullptr,
            reinterpret_cast<void**>(d2dFactory.GetAddressOf())
        );
        if (FAILED(d2dHr)) return Err(L"D2D1CreateFactory failed");

        DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(writeFactory.GetAddressOf()));
        if (writeFactory)
        {
            writeFactory->CreateTextFormat(
                L"Segoe UI",
                nullptr,
                DWRITE_FONT_WEIGHT_BOLD,
                DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL,
                13.0f,
                L"",
                &textFormat);

            if (textFormat)
            {
                textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
                textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
            }
        }

        CreateD2DTarget();

        // Shader compilation used to happen here on every process launch. The viewer is
        // commonly started through a file association, so use build-time bytecode and
        // keep shader compilation entirely off the JPG cold-start path.
        if (FAILED(dev->CreateVertexShader(g_VSMain, sizeof(g_VSMain), nullptr, &vs))) return Err(L"Create VSMain failed");
        if (FAILED(dev->CreatePixelShader(g_PSMain, sizeof(g_PSMain), nullptr, &ps))) return Err(L"Create PSMain failed");
        if (FAILED(dev->CreateVertexShader(g_VSLine, sizeof(g_VSLine), nullptr, &lvs))) return Err(L"Create VSLine failed");
        if (FAILED(dev->CreatePixelShader(g_PSLine, sizeof(g_PSLine), nullptr, &lps))) return Err(L"Create PSLine failed");

        D3D11_INPUT_ELEMENT_DESC ld[] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 }
        };
        dev->CreateInputLayout(ld, 2, g_VSMain, sizeof(g_VSMain), &il);

        D3D11_INPUT_ELEMENT_DESC lld[] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "COLOR", 0, DXGI_FORMAT_R32G32B32A32_FLOAT, 0, 8, D3D11_INPUT_PER_VERTEX_DATA, 0 }
        };
        dev->CreateInputLayout(lld, 2, g_VSLine, sizeof(g_VSLine), &lil);

        Vtx q[6] = {
            { -1, -1, 0, 1 },
            { -1, 1, 0, 0 },
            { 1, 1, 1, 0 },
            { -1, -1, 0, 1 },
            { 1, 1, 1, 0 },
            { 1, -1, 1, 1 }
        };

        D3D11_BUFFER_DESC bd{};
        bd.ByteWidth = sizeof(q);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;

        D3D11_SUBRESOURCE_DATA init{ q, 0, 0 };
        dev->CreateBuffer(&bd, &init, &vb);

        bd = {};
        bd.ByteWidth = sizeof(CBView);
        bd.Usage = D3D11_USAGE_DEFAULT;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        dev->CreateBuffer(&bd, nullptr, &cb);

        bd = {};
        bd.ByteWidth = sizeof(LineVtx) * 524288;
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        dev->CreateBuffer(&bd, nullptr, &lvb);

        D3D11_SAMPLER_DESC ss{};
        // Inspection viewer should not smooth pixels when zooming in.
        // Linear filtering makes 1GB BMP preview / tile edges look blurred versus IrfanView.
        // Use point sampling so each source pixel remains crisp at high zoom.
        ss.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
        ss.AddressU = ss.AddressV = ss.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        ss.MinLOD = 0;
        ss.MaxLOD = D3D11_FLOAT32_MAX;
        dev->CreateSamplerState(&ss, &samp);

        D3D11_BLEND_DESC blendDesc{};
        blendDesc.RenderTarget[0].BlendEnable = TRUE;
        blendDesc.RenderTarget[0].SrcBlend = D3D11_BLEND_SRC_ALPHA;
        blendDesc.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
        blendDesc.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
        blendDesc.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
        blendDesc.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
        blendDesc.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        dev->CreateBlendState(&blendDesc, &alphaBlend);
        tileCache.tileSize = kTileSize;
        tileCache.maxUploadPerFrame = kDefaultMaxTileUploadPerFrame;
        tileCache.preloadMargin = kPreloadTileMargin;
        tileCache.cpuLimitBytes = kCpuCacheLimitMb * 1024 * 1024;
        tileCache.gpuLimitBytes = kGpuCacheLimitMb * 1024 * 1024;
        tileCache.maxQueueCount = kMaxBackgroundTileLoadQueueCount;
        StartTileWorkers();
        return true;
    }
    bool Err(const wchar_t* e)
    {
        lastError = e;
        return false;
    }

    bool CreateRT()
    {
        ComPtr<ID3D11Texture2D> bb;
        if (FAILED(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)bb.GetAddressOf())))
        {
            return Err(L"GetBuffer failed");
        }

        if (FAILED(dev->CreateRenderTargetView(bb.Get(), nullptr, &rtv)))
        {
            return Err(L"CreateRenderTargetView failed");
        }

        return true;
    }

    void CreateD2DTarget()
    {
        d2dTarget.Reset();
        d2dBrush.Reset();
        if (!d2dFactory || !swap) return;

        ComPtr<IDXGISurface> surface;
        if (FAILED(swap->GetBuffer(0, __uuidof(IDXGISurface), (void**)surface.GetAddressOf()))) return;

        D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_UNKNOWN, D2D1_ALPHA_MODE_PREMULTIPLIED),
            0.0f, 0.0f);

        if (SUCCEEDED(d2dFactory->CreateDxgiSurfaceRenderTarget(surface.Get(), &props, &d2dTarget)))
        {
            d2dTarget->CreateSolidColorBrush(D2D1::ColorF(D2D1::ColorF::White), &d2dBrush);
        }
    }

    void Resize(int w, int h)
    {
        cw = std::max(1, w);
        ch = std::max(1, h);
        if (!swap) return;

        d2dTarget.Reset();
        d2dBrush.Reset();
        rtv.Reset();
        swap->ResizeBuffers(0, cw, ch, DXGI_FORMAT_UNKNOWN, 0);
        CreateRT();
        CreateD2DTarget();
    }

    bool Load(const wchar_t* p, NVImageInfo* info)
    {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);

        // Loading a new image replaces path / dimensions / palette / tile state.
        // Background tile workers also read those fields.  Stop and join them first
        // so rapid Prev/Next switching cannot race a worker that is decoding tiles
        // from the previous image and trip debug vector bounds checks.
        bool restartWorkers = !tileWorkers.empty();
        if (restartWorkers) StopTileWorkers();

        bool ok = false;
        tileEpoch.fetch_add(1);
        tileCache.Reset();
        path = p ? p : L"";

        pixels.clear();
        srv.Reset();
        tex.Reset();
        overlays.clear();
        selected = -1;
        nextId = 1;

        texW = texH = 0;
        sampleStep = 1;
        bmpTileReady = false;
        bmpTopDown = false;
        bmpBpp = 0;
        bmpOffBits = 0;
        bmpRowStride = 0;
        bmpPalette.clear();
        fileSize = 0;

        WIN32_FILE_ATTRIBUTE_DATA fad{};
        if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad))
        {
            fileSize = ((long long)fad.nFileSizeHigh << 32) | fad.nFileSizeLow;
        }

        if (IsBmp(path) && LoadBmpLarge(path))
        {
            ok = true;
        }
        else
        {
            ok = LoadWIC(path);
        }

        if (ok) ok = Upload();
        if (ok)
        {
            Fit();
            FillInfo(info);
        }

        if (restartWorkers) StartTileWorkers();
        return ok;
    }

    static bool IsBmp(const std::wstring& p)
    {
        size_t n = p.size();
        return n >= 4 && (_wcsicmp(p.c_str() + n - 4, L".bmp") == 0);
    }

    int PickSampleStep(int w, int h)
    {
        // Keep the source at native resolution when it is safe for a single D3D11 R8 texture.
        // The previous preview-only cap forced every large BMP down to about 2048 px, so zooming in
        // magnified a preview and made bright scratches look broken/blocky.
        long long pix = (long long)w * (long long)h;
        if (w > 0 && h > 0 && w <= kMaxFullTextureSize && h <= kMaxFullTextureSize && pix <= kMaxFullTexturePixels)
            return 1;

        const long long maxPix = 64LL * 1024LL * 1024LL;
        const int maxDim = kPreviewMaxTextureSize;
        int step = 1;
        while (pix / (long long)step / (long long)step > maxPix ||
               (w + step - 1) / step > maxDim ||
               (h + step - 1) / step > maxDim) {
            step *= 2;
        }
        return std::max(1, step);
    }
    bool LoadBmpLarge(const std::wstring& p)
    {
        FILE* f = nullptr;
        if (_wfopen_s(&f, p.c_str(), L"rb") != 0 || !f) return false;

        BITMAPFILEHEADER bfh{};
        if (fread(&bfh, 1, sizeof(bfh), f) != sizeof(bfh))
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"BMP read file header failed");
        }

        if (bfh.bfType != 0x4D42)
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return false;
        }

        BITMAPINFOHEADER bih{};
        if (fread(&bih, 1, sizeof(bih), f) != sizeof(bih))
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"BMP read info header failed");
        }

        if (bih.biCompression != BI_RGB)
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"Only uncompressed BI_RGB BMP is supported by the large BMP loader");
        }

        int w = bih.biWidth;
        int h = std::abs(bih.biHeight);
        if (w <= 0 || h <= 0)
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"Invalid BMP size");
        }

        int bpp = bih.biBitCount;
        if (!(bpp == 8 || bpp == 16 || bpp == 24 || bpp == 32))
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"Large BMP loader supports 8/16/24/32 bpp BI_RGB BMP");
        }

        bool topDown = bih.biHeight < 0;
        iw = w;
        ih = h;
        bitDepth = bpp;
        sampleStep = PickSampleStep(w, h);
        texW = (w + sampleStep - 1) / sampleStep;
        texH = (h + sampleStep - 1) / sampleStep;
        fmt = L"BMP native full-res";

        if (sampleStep > 1)
        {
            fmt = L"BMP preview downsample 1/" + std::to_wstring(sampleStep) + L" + full-res tiles";
        }

        std::vector<RGBQUAD> palette(256);
        if (bpp == 8)
        {
            long palCount = bih.biClrUsed ? bih.biClrUsed : 256;
            if (palCount > 256) palCount = 256;

            fseek(f, sizeof(BITMAPFILEHEADER) + bih.biSize, SEEK_SET);
            fread(palette.data(), sizeof(RGBQUAD), palCount, f);

            for (long i = palCount; i < 256; i++)
            {
                palette[(size_t)i].rgbRed = (BYTE)i;
                palette[(size_t)i].rgbGreen = (BYTE)i;
                palette[(size_t)i].rgbBlue = (BYTE)i;
            }
        }

        long long rowStride = (((long long)w * bpp + 31) / 32) * 4;
        if (rowStride <= 0)
        {
            if (f)
            {
                fclose(f);
                f = nullptr;
            }

            return Err(L"Invalid BMP stride");
        }

        bmpTileReady = true;
        bmpTopDown = topDown;
        bmpBpp = bpp;
        bmpOffBits = (long long)bfh.bfOffBits;
        bmpRowStride = rowStride;
        bmpPalette = palette;

        pixels.assign((size_t)texW * texH, 0);
        std::vector<unsigned char> row((size_t)rowStride);

        for (int ty = 0; ty < texH; ++ty)
        {
            int srcY = std::min(ty * sampleStep, h - 1);
            int fileY = topDown ? srcY : (h - 1 - srcY);
            long long off = (long long)bfh.bfOffBits + (long long)fileY * rowStride;

            if (_fseeki64(f, off, SEEK_SET) != 0)
            {
                if (f)
                {
                    fclose(f);
                    f = nullptr;
                }

                return Err(L"BMP seek failed");
            }

            if (fread(row.data(), 1, (size_t)rowStride, f) != (size_t)rowStride)
            {
                if (f)
                {
                    fclose(f);
                    f = nullptr;
                }

                return Err(L"BMP row read failed");
            }

            unsigned char* dst = &pixels[(size_t)ty * texW];
            for (int tx = 0; tx < texW; ++tx)
            {
                int sx = std::min(tx * sampleStep, w - 1);
                unsigned char g = 0;

                if (bpp == 8)
                {
                    unsigned char idx = row[(size_t)sx];
                    RGBQUAD q = palette[idx];
                    g = (unsigned char)(((int)q.rgbBlue * 29 + (int)q.rgbGreen * 150 + (int)q.rgbRed * 77) >> 8);
                }
                else if (bpp == 16)
                {
                    unsigned short v = *reinterpret_cast<unsigned short*>(&row[(size_t)sx * 2]);
                    g = (unsigned char)(v >> 8);
                }
                else if (bpp == 24)
                {
                    unsigned char* q = &row[(size_t)sx * 3];
                    g = (unsigned char)(((int)q[0] * 29 + (int)q[1] * 150 + (int)q[2] * 77) >> 8);
                }
                else
                {
                    unsigned char* q = &row[(size_t)sx * 4];
                    g = (unsigned char)(((int)q[0] * 29 + (int)q[1] * 150 + (int)q[2] * 77) >> 8);
                }

                dst[tx] = g;
            }
        }

        if (f)
        {
            fclose(f);
            f = nullptr;
        }

        return true;
    }
    void FillInfo(NVImageInfo* info)
    {
        if (!info) return;

        info->width = iw;
        info->height = ih;
        info->bitDepth = bitDepth;
        info->fileSize = fileSize;
        wcsncpy_s(info->format, _countof(info->format), fmt.c_str(), _TRUNCATE);
        wcsncpy_s(info->path, _countof(info->path), path.c_str(), _TRUNCATE);
    }

    bool LoadWIC(const std::wstring& p)
    {
        WIN32_FILE_ATTRIBUTE_DATA fad{};
        if (GetFileAttributesExW(p.c_str(), GetFileExInfoStandard, &fad))
        {
            fileSize = ((long long)fad.nFileSizeHigh << 32) | fad.nFileSizeLow;
        }

        ComPtr<IWICImagingFactory> fac;
        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&fac));
        if (FAILED(hr)) return Err(L"CoCreateInstance WIC failed");

        ComPtr<IWICBitmapDecoder> dec;
        hr = fac->CreateDecoderFromFilename(p.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnLoad, &dec);
        if (FAILED(hr)) return Err(L"WIC cannot decode image");

        ComPtr<IWICBitmapFrameDecode> frame;
        dec->GetFrame(0, &frame);

        UINT w = 0, h = 0;
        frame->GetSize(&w, &h);
        iw = (int)w;
        ih = (int)h;
        texW = iw;
        texH = ih;
        sampleStep = 1;

        WICPixelFormatGUID srcFmt{};
        frame->GetPixelFormat(&srcFmt);
        bitDepth = 8;
        fmt = L"WIC";

        if (srcFmt == GUID_WICPixelFormat16bppGray) bitDepth = 16;
        else if (srcFmt == GUID_WICPixelFormat8bppGray) bitDepth = 8;
        else if (srcFmt == GUID_WICPixelFormat24bppRGB) bitDepth = 24;
        else if (srcFmt == GUID_WICPixelFormat32bppBGRA) bitDepth = 32;

        if (srcFmt == GUID_WICPixelFormat8bppGray)
        {
            pixels.resize((size_t)iw * ih);
            hr = frame->CopyPixels(nullptr, (UINT)iw, (UINT)pixels.size(), pixels.data());
            return SUCCEEDED(hr) || Err(L"CopyPixels 8bppGray failed");
        }

        if (srcFmt == GUID_WICPixelFormat16bppGray)
        {
            std::vector<unsigned short> tmp((size_t)iw * ih);
            hr = frame->CopyPixels(nullptr, (UINT)(iw * 2), (UINT)(tmp.size() * sizeof(unsigned short)), reinterpret_cast<BYTE*>(tmp.data()));
            if (FAILED(hr)) return Err(L"CopyPixels 16bppGray failed");

            pixels.resize((size_t)iw * ih);
            for (size_t i = 0; i < tmp.size(); ++i)
            {
                pixels[i] = (unsigned char)(tmp[i] >> 8);
            }

            return true;
        }

        ComPtr<IWICFormatConverter> conv;
        fac->CreateFormatConverter(&conv);
        hr = conv->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0, WICBitmapPaletteTypeCustom);
        if (FAILED(hr)) return Err(L"WIC format convert failed");

        std::vector<unsigned char> bgra((size_t)iw * ih * 4);
        hr = conv->CopyPixels(nullptr, (UINT)(iw * 4), (UINT)bgra.size(), bgra.data());
        if (FAILED(hr)) return Err(L"CopyPixels BGRA failed");

        pixels.resize((size_t)iw * ih);
        for (size_t i = 0, j = 0; i < pixels.size(); ++i, j += 4)
        {
            pixels[i] = (unsigned char)((bgra[j] * 29 + bgra[j + 1] * 150 + bgra[j + 2] * 77) >> 8);
        }

        return true;
    }

    bool Upload()
    {
        if (iw <= 0 || ih <= 0 || texW <= 0 || texH <= 0 || pixels.empty())
        {
            return Err(L"no pixels");
        }

        D3D11_TEXTURE2D_DESC td{};
        td.Width = texW;
        td.Height = texH;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_IMMUTABLE;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA sd{ pixels.data(), (UINT)texW, 0 };
        if (FAILED(dev->CreateTexture2D(&td, &sd, &tex)))
        {
            return Err(L"CreateTexture2D failed");
        }

        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};
        vd.Format = td.Format;
        vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = 1;
        if (FAILED(dev->CreateShaderResourceView(tex.Get(), &vd, &srv)))
        {
            return Err(L"CreateSRV failed");
        }

        uploadCount++;
        return true;
    }

    void Fit()
    {
        if (iw <= 0 || ih <= 0) return;

        float sx = (float)cw / iw;
        float sy = (float)ch / ih;
        fitScale = std::max(0.0001f, std::min(sx, sy));
        scale = fitScale;
        ox = (cw - iw * scale) * 0.5f;
        oy = (ch - ih * scale) * 0.5f;
    }

    void One()
    {
        scale = 1;
        ox = (cw - iw) * 0.5f;
        oy = (ch - ih) * 0.5f;
    }

    bool ScreenToImage(int sx, int sy, float& ix, float& iy)
    {
        if (scale <= 0) return false;

        ix = (sx - ox) / scale;
        iy = (sy - oy) / scale;
        return ix >= 0 && iy >= 0 && ix < iw && iy < ih;
    }

    void ImageToNdc(float ix, float iy, float& nx, float& ny)
    {
        float sxp = ix * scale + ox;
        float syp = iy * scale + oy;
        nx = sxp / cw * 2 - 1;
        ny = 1 - syp / ch * 2;
    }

    unsigned char Pix(int x, int y) const
    {
        if (x < 0 || y < 0 || x >= iw || y >= ih || pixels.empty()) return 0;

        int tx = ClampInt(x / std::max(1, sampleStep), 0, std::max(0, texW - 1));
        int ty = ClampInt(y / std::max(1, sampleStep), 0, std::max(0, texH - 1));
        return pixels[(size_t)ty * texW + tx];
    }

    bool TryPixExact(int x, int y, unsigned char& value)
    {
        if (x < 0 || y < 0 || x >= iw || y >= ih) return false;
        if (!bmpTileReady || sampleStep <= 1)
        {
            value = Pix(x, y);
            return !pixels.empty();
        }

        // The large-BMP preview stores only one sample per sampleStep pixels. Once
        // full-resolution tiles are being displayed, sampling that preview makes
        // many visibly different pixels report the same gray value. Read the CPU
        // copy of the exact tile instead so labels and cursor values match the image.
        TileKey key(tileEpoch.load(), x / kTileSize, y / kTileSize);
        std::lock_guard<std::mutex> lock(tileCache.mtx);
        std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(key);
        if (it == tileCache.cpuTiles.end()) return false;

        const TileCacheEntry& tile = it->second;
        int localX = x - tile.imageX;
        int localY = y - tile.imageY;
        if (localX < 0 || localY < 0 || localX >= tile.w || localY >= tile.h || tile.gray.empty()) return false;

        size_t index = (size_t)localY * (size_t)tile.w + (size_t)localX;
        if (index >= tile.gray.size()) return false;
        value = tile.gray[index];
        return true;
    }

    void MouseDown(int button, int x, int y)
    {
        downX = lastX = x;
        downY = lastY = y;
        dragging = true;
        startOx = ox;
        startOy = oy;
        dragHandle = 0;

        float ix = 0, iy = 0;
        ScreenToImage(x, y, ix, iy);
        roiStartX = ix;
        roiStartY = iy;
        dragKind = (button == 2) ? NV_TOOL_PAN : tool;

        if (button != 2 && tool == NV_TOOL_SELECT)
        {
            dragHandle = HitHandle(ix, iy);
            if (dragHandle == 0) selected = HitOverlay(ix, iy);

            dragging = selected > 0;
            if (dragging)
            {
                Overlay* o = FindOverlay(selected);
                if (!o || o->readOnly)
                {
                    dragging = false;
                    dragKind = 0;
                    dragHandle = 0;
                    return;
                }

                selStartX1 = o->x1;
                selStartY1 = o->y1;
                selStartX2 = o->x2;
                selStartY2 = o->y2;
            }
        }
    }

    void MouseMove(int x, int y)
    {
        if (!dragging)
        {
            lastX = x;
            lastY = y;
            return;
        }

        if (dragKind == NV_TOOL_PAN)
        {
            ox = startOx + (x - downX);
            oy = startOy + (y - downY);
        }
        else if (dragKind == NV_TOOL_SELECT && selected > 0)
        {
            float ix = 0, iy = 0;
            ScreenToImage(x, y, ix, iy);

            Overlay* o = FindOverlay(selected);
            if (o && !o->readOnly)
            {
                float dx = ix - roiStartX;
                float dy = iy - roiStartY;

                if (dragHandle == 0)
                {
                    o->x1 = selStartX1 + dx;
                    o->y1 = selStartY1 + dy;
                    o->x2 = selStartX2 + dx;
                    o->y2 = selStartY2 + dy;
                }
                else
                {
                    ResizeByHandle(*o, ix, iy);
                }
            }
        }

        lastX = x;
        lastY = y;
    }

    void MouseUp(int button, int x, int y)
    {
        if (!dragging) return;

        float ix, iy;
        ScreenToImage(x, y, ix, iy);

        if (dragKind == NV_TOOL_ZOOM_RECT)
        {
            float w = fabsf(ix - roiStartX);
            float h = fabsf(iy - roiStartY);

            if (w > 4 && h > 4)
            {
                float x0 = std::min(ix, roiStartX);
                float y0 = std::min(iy, roiStartY);
                float minScale = std::max(0.0001f, fitScale * kMinZoomScaleFactor);
                scale = ClampFloat(std::min(cw / w, ch / h), minScale, kMaxZoomScale);
                ox = -x0 * scale + (cw - w * scale) * 0.5f;
                oy = -y0 * scale + (ch - h * scale) * 0.5f;
                SnapForPixelGrid(cw * 0.5f, ch * 0.5f, x0 + w * 0.5f, y0 + h * 0.5f);
            }
        }
        else if (dragKind == NV_TOOL_ROI)
        {
            AddRect(NV_OVERLAY_ROI, roiStartX, roiStartY, ix, iy);
        }
        else if (dragKind == NV_TOOL_MEASURE)
        {
            AddLine(roiStartX, roiStartY, ix, iy);
        }
        else if (dragKind == NV_TOOL_CIRCLE)
        {
            AddCircle(roiStartX, roiStartY, ix, iy);
        }

        dragging = false;
        dragKind = 0;
    }

    void Wheel(int delta, int x, int y)
    {
        float beforeX, beforeY;
        ScreenToImage(x, y, beforeX, beforeY);

        float f = delta > 0 ? kZoomStep : (1.0f / kZoomStep);
        float minScale = std::max(0.0001f, fitScale * kMinZoomScaleFactor);
        scale = ClampFloat(scale * f, minScale, kMaxZoomScale);
        ox = x - beforeX * scale;
        oy = y - beforeY * scale;
        SnapForPixelGrid((float)x, (float)y, beforeX, beforeY);
    }

    void AddRect(int t, float ax, float ay, float bx, float by)
    {
        Overlay o{};
        o.id = nextId++;
        o.type = t;
        o.visible = true;
        o.readOnly = false;
        o.x1 = std::min(ax, bx);
        o.y1 = std::min(ay, by);
        o.x2 = std::max(ax, bx);
        o.y2 = std::max(ay, by);
        o.name = L"ROI " + std::to_wstring(o.id);
        overlays.push_back(o);
        selected = o.id;
    }

    void AddLine(float ax, float ay, float bx, float by)
    {
        Overlay o{};
        o.id = nextId++;
        o.type = NV_OVERLAY_MEASURE;
        o.visible = true;
        o.readOnly = false;
        o.x1 = ax;
        o.y1 = ay;
        o.x2 = bx;
        o.y2 = by;
        o.name = L"Measure " + std::to_wstring(o.id);
        overlays.push_back(o);
        selected = o.id;
    }

    void AddCircle(float ax, float ay, float bx, float by)
    {
        Overlay o{};
        o.id = nextId++;
        o.type = NV_OVERLAY_CIRCLE;
        o.visible = true;
        o.readOnly = false;
        o.x1 = std::min(ax, bx);
        o.y1 = std::min(ay, by);
        o.x2 = std::max(ax, bx);
        o.y2 = std::max(ay, by);
        o.name = L"Circle " + std::to_wstring(o.id);
        overlays.push_back(o);
        selected = o.id;
    }

    Overlay* FindOverlay(int id)
    {
        for (size_t i = 0; i < overlays.size(); ++i)
        {
            if (overlays[i].id == id) return &overlays[i];
        }

        return nullptr;
    }

    int HitOverlay(float ix, float iy)
    {
        for (std::vector<Overlay>::reverse_iterator it = overlays.rbegin(); it != overlays.rend(); ++it)
        {
            if (!it->visible || it->readOnly) continue;

            float x0 = std::min(it->x1, it->x2) - 5 / scale;
            float x1 = std::max(it->x1, it->x2) + 5 / scale;
            float y0 = std::min(it->y1, it->y2) - 5 / scale;
            float y1 = std::max(it->y1, it->y2) + 5 / scale;
            if (ix >= x0 && ix <= x1 && iy >= y0 && iy <= y1) return it->id;
        }

        return -1;
    }

    void RectPts(const Overlay& o, float& ax, float& ay, float& bx, float& by, float& cx, float& cy, float& dx, float& dy)
    {
        float mx = (o.x1 + o.x2) * 0.5f;
        float my = (o.y1 + o.y2) * 0.5f;
        RotateAround(mx, my, o.angle, o.x1, o.y1, ax, ay);
        RotateAround(mx, my, o.angle, o.x2, o.y1, bx, by);
        RotateAround(mx, my, o.angle, o.x2, o.y2, cx, cy);
        RotateAround(mx, my, o.angle, o.x1, o.y2, dx, dy);
    }

    void EllipseHandlePts(const Overlay& o, float& leftX, float& leftY, float& topX, float& topY, float& rightX, float& rightY, float& botX, float& botY, float& tx, float& ty)
    {
        // Four handles are the rotated bounding-box corners, matching the original C# viewer.
        float cx = (o.x1 + o.x2) * 0.5f;
        float cy = (o.y1 + o.y2) * 0.5f;
        RotateAround(cx, cy, o.angle, o.x1, o.y1, leftX, leftY);
        RotateAround(cx, cy, o.angle, o.x2, o.y1, topX, topY);
        RotateAround(cx, cy, o.angle, o.x2, o.y2, rightX, rightY);
        RotateAround(cx, cy, o.angle, o.x1, o.y2, botX, botY);

        float rx = fabsf(o.x2 - o.x1) * 0.5f;
        RotateAround(cx, cy, o.angle, cx + rx * 0.75f, cy, tx, ty);
    }

    void RectCornerPoint(const Overlay& o, int handle, float& x, float& y)
    {
        float ax, ay, bx, by, cx, cy, dx, dy;
        RectPts(o, ax, ay, bx, by, cx, cy, dx, dy);

        if (handle == 1) { x = ax; y = ay; }
        else if (handle == 2) { x = bx; y = by; }
        else if (handle == 3) { x = cx; y = cy; }
        else { x = dx; y = dy; }
    }

    void EllipseCornerPoint(const Overlay& o, int handle, float& x, float& y)
    {
        float lx, ly, tx, ty, rx, ry, bx, by, ta, tb;
        EllipseHandlePts(o, lx, ly, tx, ty, rx, ry, bx, by, ta, tb);

        if (handle == 1) { x = lx; y = ly; }
        else if (handle == 2) { x = tx; y = ty; }
        else if (handle == 3) { x = rx; y = ry; }
        else { x = bx; y = by; }
    }

    void ShiftOverlay(Overlay& o, float dx, float dy)
    {
        o.x1 += dx;
        o.y1 += dy;
        o.x2 += dx;
        o.y2 += dy;
        o.x3 += dx;
        o.y3 += dy;
    }

    int HitHandle(float ix, float iy)
    {
        Overlay* o = FindOverlay(selected);
        if (!o || o->readOnly || !o->visible) return 0;

        float tol = 8.0f / std::max(scale, 0.01f);
        if (o->type == NV_OVERLAY_ROI)
        {
            float ax, ay, bx, by, cx, cy, dx, dy;
            RectPts(*o, ax, ay, bx, by, cx, cy, dx, dy);

            float mx = (o->x1 + o->x2) * 0.5f;
            float my = (o->y1 + o->y2) * 0.5f;
            float hx, hy;
            RotateAround(mx, my, o->angle, (o->x1 + o->x2) * 0.5f, o->y1 - 28.0f / std::max(scale, 0.01f), hx, hy);

            if (IsNear(ix, iy, hx, hy, tol * 1.6f)) return 5;
            if (IsNear(ix, iy, ax, ay, tol)) return 1;
            if (IsNear(ix, iy, bx, by, tol)) return 2;
            if (IsNear(ix, iy, cx, cy, tol)) return 3;
            if (IsNear(ix, iy, dx, dy, tol)) return 4;
        }
        else if (o->type == NV_OVERLAY_MEASURE)
        {
            if (IsNear(ix, iy, o->x1, o->y1, tol)) return 1;
            if (IsNear(ix, iy, o->x2, o->y2, tol)) return 2;
        }
        else if (o->type == NV_OVERLAY_CIRCLE)
        {
            float lx, ly, tx, ty, rx, ry, bx, by, txa, tya;
            EllipseHandlePts(*o, lx, ly, tx, ty, rx, ry, bx, by, txa, tya);

            if (IsNear(ix, iy, lx, ly, tol)) return 1;
            if (IsNear(ix, iy, tx, ty, tol)) return 2;
            if (IsNear(ix, iy, rx, ry, tol)) return 3;
            if (IsNear(ix, iy, bx, by, tol)) return 4;
            if (IsNear(ix, iy, txa, tya, tol * 1.8f)) return 5;
        }

        return 0;
    }
    void ResizeByHandle(Overlay& o, float ix, float iy)
    {
        if (o.type == NV_OVERLAY_ROI)
        {
            float mx = (o.x1 + o.x2) * 0.5f;
            float my = (o.y1 + o.y2) * 0.5f;

            if (dragHandle == 5)
            {
                o.angle = atan2f(iy - my, ix - mx) * 57.2957795f + 90.0f;
                return;
            }

            int anchorHandle = dragHandle == 1 ? 3 : (dragHandle == 2 ? 4 : (dragHandle == 3 ? 1 : 2));
            float anchorX = 0, anchorY = 0;
            RectCornerPoint(o, anchorHandle, anchorX, anchorY);

            float ux, uy;
            InvRotateAround(mx, my, o.angle, ix, iy, ux, uy);

            if (dragHandle == 1) { o.x1 = ux; o.y1 = uy; }
            else if (dragHandle == 2) { o.x2 = ux; o.y1 = uy; }
            else if (dragHandle == 3) { o.x2 = ux; o.y2 = uy; }
            else if (dragHandle == 4) { o.x1 = ux; o.y2 = uy; }

            float newAnchorX = 0, newAnchorY = 0;
            RectCornerPoint(o, anchorHandle, newAnchorX, newAnchorY);
            ShiftOverlay(o, anchorX - newAnchorX, anchorY - newAnchorY);
        }
        else if (o.type == NV_OVERLAY_MEASURE)
        {
            if (dragHandle == 1)
            {
                o.x1 = ix;
                o.y1 = iy;
            }
            else if (dragHandle == 2)
            {
                o.x2 = ix;
                o.y2 = iy;
            }
        }
        else if (o.type == NV_OVERLAY_CIRCLE)
        {
            float cx = (o.x1 + o.x2) * 0.5f;
            float cy = (o.y1 + o.y2) * 0.5f;

            if (dragHandle == 5)
            {
                o.angle = atan2f(iy - cy, ix - cx) * 57.2957795f;
                return;
            }

            int anchorHandle = dragHandle == 1 ? 3 : (dragHandle == 2 ? 4 : (dragHandle == 3 ? 1 : 2));
            float anchorX = 0, anchorY = 0;
            EllipseCornerPoint(o, anchorHandle, anchorX, anchorY);

            float ux, uy;
            InvRotateAround(cx, cy, o.angle, ix, iy, ux, uy);

            if (dragHandle == 1) { o.x1 = ux; o.y1 = uy; }
            else if (dragHandle == 2) { o.x2 = ux; o.y1 = uy; }
            else if (dragHandle == 3) { o.x2 = ux; o.y2 = uy; }
            else if (dragHandle == 4) { o.x1 = ux; o.y2 = uy; }

            float newAnchorX = 0, newAnchorY = 0;
            EllipseCornerPoint(o, anchorHandle, newAnchorX, newAnchorY);
            ShiftOverlay(o, anchorX - newAnchorX, anchorY - newAnchorY);
        }
    }


    void StartTileWorkers()
    {
        if (!tileWorkers.empty()) return;

        tileWorkerStop.store(false);
        int count = std::max(1, std::min(4, (int)std::thread::hardware_concurrency() - 2));
        for (int i = 0; i < count; ++i)
        {
            tileWorkers.emplace_back(&NativeViewer::TileWorkerLoop, this);
        }
    }

    void StopTileWorkers()
    {
        tileWorkerStop.store(true);
        tileCache.cv.notify_all();
        for (size_t i = 0; i < tileWorkers.size(); ++i)
        {
            if (tileWorkers[i].joinable())
            {
                tileWorkers[i].join();
            }
        }
        tileWorkers.clear();
    }

    void TileWorkerLoop()
    {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        while (!tileWorkerStop.load())
        {
            TileKey key;
            bool haveKey = false;
            {
                std::unique_lock<std::mutex> lock(tileCache.mtx);
                tileCache.cv.wait(lock, [&]() { return tileWorkerStop.load() || !tileCache.loadQueue.empty(); });
                if (tileWorkerStop.load()) break;
                if (!tileCache.loadQueue.empty())
                {
                    key = tileCache.loadQueue.front();
                    tileCache.loadQueue.pop_front();
                    tileCache.queuedKeys.erase(key);
                    haveKey = true;
                }
            }
            if (!haveKey) continue;
            if (key.level != tileEpoch.load()) {
                std::lock_guard<std::mutex> lock(tileCache.mtx);
                tileCache.staleDiscardCount++;
                continue;
            }
            TileCacheEntry loaded;
            if (!ReadBmpTile(key, loaded)) continue;
            if (key.level != tileEpoch.load()) {
                std::lock_guard<std::mutex> lock(tileCache.mtx);
                tileCache.staleDiscardCount++;
                continue;
            }
            loaded.queued = false;
            loaded.lastTouchFrame = tileCache.frameId;
            {
                std::lock_guard<std::mutex> lock(tileCache.mtx);
                std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(key);
                if (it == tileCache.cpuTiles.end()) continue;
                if (it->second.gpuResident) continue;
                it->second.gray.swap(loaded.gray);
                it->second.imageX = loaded.imageX;
                it->second.imageY = loaded.imageY;
                it->second.w = loaded.w;
                it->second.h = loaded.h;
                it->second.byteCount = loaded.byteCount;
                it->second.queued = false;
                it->second.lastTouchFrame = loaded.lastTouchFrame;
                tileCache.pending.push_back(key);
            }
        }
        CoUninitialize();
    }

    void TrimTileCaches() {
        std::lock_guard<std::mutex> lock(tileCache.mtx);
        long long cpuBytes = 0;
        long long gpuBytes = 0;
        for (std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.begin(); it != tileCache.cpuTiles.end(); ++it) {
            cpuBytes += (long long)it->second.gray.size();
            if (it->second.gpuResident) gpuBytes += (long long)it->second.w * it->second.h;
        }
        if (cpuBytes <= tileCache.cpuLimitBytes && gpuBytes <= tileCache.gpuLimitBytes) return;
        std::vector<TileKey> keys;
        for (std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.begin(); it != tileCache.cpuTiles.end(); ++it) keys.push_back(it->first);
        std::sort(keys.begin(), keys.end(), [&](const TileKey& a, const TileKey& b) {
            return tileCache.cpuTiles[a].lastTouchFrame < tileCache.cpuTiles[b].lastTouchFrame;
        });
        for (size_t i = 0; i < keys.size(); ++i) {
            std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(keys[i]);
            if (it == tileCache.cpuTiles.end()) continue;
            if (it->second.lastTouchFrame + 2 >= tileCache.frameId) continue; // keep current viewport and near-past tiles
            cpuBytes -= (long long)it->second.gray.size();
            if (it->second.gpuResident) gpuBytes -= (long long)it->second.w * it->second.h;
            tileCache.queuedKeys.erase(keys[i]);
            tileCache.cpuTiles.erase(it);
            if (cpuBytes <= tileCache.cpuLimitBytes && gpuBytes <= tileCache.gpuLimitBytes) break;
        }
    }

    unsigned char DecodeBmpGrayFromRow(const std::vector<unsigned char>& row, int sx) const {
        unsigned char g = 0;
        if (bmpBpp == 8) {
            unsigned char idx = row[(size_t)sx];
            RGBQUAD q = (bmpPalette.size() > (size_t)idx) ? bmpPalette[(size_t)idx] : RGBQUAD{idx,idx,idx,0};
            g = (unsigned char)(((int)q.rgbBlue * 29 + (int)q.rgbGreen * 150 + (int)q.rgbRed * 77) >> 8);
        } else if (bmpBpp == 16) {
            unsigned short v = *reinterpret_cast<const unsigned short*>(&row[(size_t)sx * 2]);
            g = (unsigned char)(v >> 8);
        } else if (bmpBpp == 24) {
            const unsigned char* q = &row[(size_t)sx * 3];
            g = (unsigned char)(((int)q[0] * 29 + (int)q[1] * 150 + (int)q[2] * 77) >> 8);
        } else if (bmpBpp == 32) {
            const unsigned char* q = &row[(size_t)sx * 4];
            g = (unsigned char)(((int)q[0] * 29 + (int)q[1] * 150 + (int)q[2] * 77) >> 8);
        }
        return g;
    }

    bool ReadBmpTile(const TileKey& key, TileCacheEntry& out) {
        if (!bmpTileReady || path.empty() || bmpRowStride <= 0) return false;
        int imageX = key.x * kTileSize;
        int imageY = key.y * kTileSize;
        if (imageX >= iw || imageY >= ih) return false;
        int tw = std::min(kTileSize, iw - imageX);
        int th = std::min(kTileSize, ih - imageY);
        FILE* f = nullptr;
        if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || !f) return false;
        std::vector<unsigned char> row((size_t)bmpRowStride);
        std::vector<unsigned char> gray((size_t)tw * (size_t)th);
        for (int y = 0; y < th; ++y) {
            int srcY = imageY + y;
            int fileY = bmpTopDown ? srcY : (ih - 1 - srcY);
            long long off = bmpOffBits + (long long)fileY * bmpRowStride;
            if (_fseeki64(f, off, SEEK_SET) != 0) { fclose(f); return false; }
            if (fread(row.data(), 1, (size_t)bmpRowStride, f) != (size_t)bmpRowStride) { fclose(f); return false; }
            unsigned char* dst = &gray[(size_t)y * (size_t)tw];
            for (int x = 0; x < tw; ++x) dst[x] = DecodeBmpGrayFromRow(row, imageX + x);
        }
        fclose(f);
        out.imageX = imageX;
        out.imageY = imageY;
        out.w = tw;
        out.h = th;
        out.byteCount = tw * th;
        out.gray.swap(gray);
        return true;
    }

    bool UploadTileToGpu(TileCacheEntry& e) {
        if (e.w <= 0 || e.h <= 0 || e.gray.empty()) return false;
        D3D11_TEXTURE2D_DESC td{};
        td.Width = e.w;
        td.Height = e.h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_IMMUTABLE;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA sd{ e.gray.data(), (UINT)e.w, 0 };
        if (FAILED(dev->CreateTexture2D(&td, &sd, &e.tex))) return false;
        D3D11_SHADER_RESOURCE_VIEW_DESC vd{};
        vd.Format = td.Format;
        vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipLevels = 1;
        if (FAILED(dev->CreateShaderResourceView(e.tex.Get(), &vd, &e.srv))) return false;
        e.gpuResident = true;
        uploadCount++;
        return true;
    }

    void ProcessTileQueue() {
        {
            std::lock_guard<std::mutex> lock(tileCache.mtx);
            tileCache.uploadedLastFrame = 0;
        }
        if (!bmpTileReady || sampleStep <= 1 || scale < fitScale * (float)kFullResolutionTileZoomThreshold) return;
        int processed = 0;
        int perFrame = std::max(1, tileCache.maxUploadPerFrame);
        while (processed < perFrame) {
            TileKey key;
            TileCacheEntry local;
            bool haveReadyTile = false;
            {
                std::lock_guard<std::mutex> lock(tileCache.mtx);
                while (!tileCache.pending.empty()) {
                    key = tileCache.pending.front();
                    tileCache.pending.pop_front();
                    if (key.level != tileEpoch.load()) { tileCache.staleDiscardCount++; continue; }
                    std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(key);
                    if (it == tileCache.cpuTiles.end()) continue;
                    if (it->second.gpuResident || it->second.gray.empty()) continue;
                    local = it->second;
                    haveReadyTile = true;
                    break;
                }
            }
            if (!haveReadyTile) break;
            if (!UploadTileToGpu(local)) continue;
            local.queued = false;
            local.lastTouchFrame = tileCache.frameId;
            {
                std::lock_guard<std::mutex> lock(tileCache.mtx);
                std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(key);
                if (it != tileCache.cpuTiles.end()) {
                    it->second.tex = local.tex;
                    it->second.srv = local.srv;
                    it->second.gpuResident = true;
                    it->second.queued = false;
                    it->second.lastTouchFrame = local.lastTouchFrame;
                    tileCache.uploadedLastFrame++;
                    // Keep gray data in CPU cache for now; the LRU limiter below will trim it.
                }
            }
            ++processed;
        }
        TrimTileCaches();
    }

    void DrawTextureRegion(ID3D11ShaderResourceView* useSrv, float imgX, float imgY, float imgW, float imgH, int srcW, int srcH) {
        if (!useSrv || imgW <= 0 || imgH <= 0 || srcW <= 0 || srcH <= 0) return;
        CBView c{};
        c.sx = scale; c.sy = scale; c.ox = ox; c.oy = oy; c.invW = 1.0f / cw; c.invH = 1.0f / ch;
        c.proc = (float)proc; c.display = (float)display; c.threshold = threshold / 255.0f; c.gamma = (float)gamma;
        c.radius = (float)radius; c.blend = 0.5f; c.imgW = (float)std::max(1, srcW); c.imgH = (float)std::max(1, srcH);
        ctx->UpdateSubresource(cb.Get(), 0, nullptr, &c, 0, 0);
        float sl = SnapScreenCoord(ox + imgX * scale);
        float sr = SnapScreenCoord(ox + (imgX + imgW) * scale);
        float st = SnapScreenCoord(oy + imgY * scale);
        float sb = SnapScreenCoord(oy + (imgY + imgH) * scale);
        float l = (sl / cw) * 2.0f - 1.0f;
        float r = (sr / cw) * 2.0f - 1.0f;
        float t = 1.0f - (st / ch) * 2.0f;
        float b = 1.0f - (sb / ch) * 2.0f;
        Vtx q[6] = { {l,b,0,1}, {l,t,0,0}, {r,t,1,0}, {l,b,0,1}, {r,t,1,0}, {r,b,1,1} };
        D3D11_MAPPED_SUBRESOURCE map{};
        if (SUCCEEDED(ctx->Map(vb.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &map))) { memcpy(map.pData, q, sizeof(q)); ctx->Unmap(vb.Get(), 0); }
        UINT stride = sizeof(Vtx), off = 0;
        ctx->IASetInputLayout(il.Get());
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->IASetVertexBuffers(0, 1, vb.GetAddressOf(), &stride, &off);
        ctx->VSSetShader(vs.Get(), nullptr, 0);
        ctx->PSSetShader(ps.Get(), nullptr, 0);
        ctx->VSSetConstantBuffers(0, 1, cb.GetAddressOf());
        ctx->PSSetConstantBuffers(0, 1, cb.GetAddressOf());
        ctx->PSSetShaderResources(0, 1, &useSrv);
        ctx->PSSetSamplers(0, 1, samp.GetAddressOf());
        ctx->Draw(6, 0);
        ID3D11ShaderResourceView* nullsrv = nullptr;
        ctx->PSSetShaderResources(0, 1, &nullsrv);
    }

    void DrawVisibleTiles() {
        if (!bmpTileReady || sampleStep <= 1) return;
        float ix0 = std::max(0.0f, (-ox) / scale), iy0 = std::max(0.0f, (-oy) / scale);
        float ix1 = std::min((float)iw, (cw - ox) / scale), iy1 = std::min((float)ih, (ch - oy) / scale);
        int tx0 = ClampInt((int)std::floor(ix0 / kTileSize), 0, std::max(0, (iw - 1) / kTileSize));
        int ty0 = ClampInt((int)std::floor(iy0 / kTileSize), 0, std::max(0, (ih - 1) / kTileSize));
        int tx1 = ClampInt((int)std::floor(ix1 / kTileSize), 0, std::max(0, (iw - 1) / kTileSize));
        int ty1 = ClampInt((int)std::floor(iy1 / kTileSize), 0, std::max(0, (ih - 1) / kTileSize));
        std::vector<TileCacheEntry*> drawList;
        {
            std::lock_guard<std::mutex> lock(tileCache.mtx);
            for (int ty = ty0; ty <= ty1; ++ty) for (int tx = tx0; tx <= tx1; ++tx) {
                TileKey key(tileEpoch.load(), tx, ty);
                std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(key);
                if (it != tileCache.cpuTiles.end() && it->second.gpuResident && it->second.srv) drawList.push_back(&it->second);
            }
        }
        for (size_t i = 0; i < drawList.size(); ++i) {
            TileCacheEntry* e = drawList[i];
            DrawTextureRegion(e->srv.Get(), (float)e->imageX, (float)e->imageY, (float)e->w, (float)e->h, e->w, e->h);
        }
    }

    void PlanVisibleTiles(){
        if(iw <= 0 || ih <= 0) return;
        // Use full-resolution tile planning only after the viewer is zoomed in
        // enough that the preview texture can no longer represent the requested detail.
        // This is intentionally lightweight: it only marks visible tile IDs and queues
        // them. The next phase can attach worker IO + GPU tile texture upload here.
        if(scale < fitScale * (float)kFullResolutionTileZoomThreshold) return;
        float ix0=std::max(0.0f,(-ox)/scale), iy0=std::max(0.0f,(-oy)/scale);
        float ix1=std::min((float)iw,(cw-ox)/scale), iy1=std::min((float)ih,(ch-oy)/scale);
        int tx0=ClampInt((int)std::floor(ix0 / kTileSize) - kPreloadTileMargin, 0, std::max(0,(iw-1)/kTileSize));
        int ty0=ClampInt((int)std::floor(iy0 / kTileSize) - kPreloadTileMargin, 0, std::max(0,(ih-1)/kTileSize));
        int tx1=ClampInt((int)std::floor(ix1 / kTileSize) + kPreloadTileMargin, 0, std::max(0,(iw-1)/kTileSize));
        int ty1=ClampInt((int)std::floor(iy1 / kTileSize) + kPreloadTileMargin, 0, std::max(0,(ih-1)/kTileSize));
        tileCache.TouchVisibleRange(tileEpoch.load(), tx0, ty0, tx1, ty1);
        {
            std::lock_guard<std::mutex> lock(tileCache.mtx);
            while((int)tileCache.loadQueue.size() > tileCache.maxQueueCount) {
                TileKey oldKey = tileCache.loadQueue.front();
                tileCache.loadQueue.pop_front();
                tileCache.queuedKeys.erase(oldKey);
                std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.find(oldKey);
                if (it != tileCache.cpuTiles.end()) it->second.queued = false;
            }
        }
    }
    int TilePendingCount(){
        if(!bmpTileReady || sampleStep <= 1) return 0;
        std::lock_guard<std::mutex> lock(tileCache.mtx);
        return (int)(tileCache.pending.size() + tileCache.loadQueue.size());
    }
    void GetTileDiag(int& pendingWorker, int& readyUpload, int& cpuCached, int& gpuResident, int& uploadedLastFrame, int& duplicateRequests, int& staleDiscards, int& maxUploadPerFrame, int& cpuLimitMb, int& gpuLimitMb){
        pendingWorker = readyUpload = cpuCached = gpuResident = uploadedLastFrame = duplicateRequests = staleDiscards = 0;
        maxUploadPerFrame = tileCache.maxUploadPerFrame;
        cpuLimitMb = tileCache.cpuLimitBytes / (1024 * 1024);
        gpuLimitMb = tileCache.gpuLimitBytes / (1024 * 1024);
        if(!bmpTileReady || sampleStep <= 1) return;
        std::lock_guard<std::mutex> lock(tileCache.mtx);
        pendingWorker = (int)tileCache.loadQueue.size();
        readyUpload = (int)tileCache.pending.size();
        cpuCached = (int)tileCache.cpuTiles.size();
        for (std::map<TileKey, TileCacheEntry>::iterator it = tileCache.cpuTiles.begin(); it != tileCache.cpuTiles.end(); ++it) {
            if (it->second.gpuResident && it->second.srv) gpuResident++;
        }
        uploadedLastFrame = tileCache.uploadedLastFrame;
        duplicateRequests = tileCache.duplicateRequestCount;
        staleDiscards = tileCache.staleDiscardCount;
        maxUploadPerFrame = tileCache.maxUploadPerFrame;
        cpuLimitMb = tileCache.cpuLimitBytes / (1024 * 1024);
        gpuLimitMb = tileCache.gpuLimitBytes / (1024 * 1024);
    }
    const char* RenderReasonText() const {
        int r = renderReason.load();
        if (r == 1) return "DIRTY";
        if (r == 2) return "ACTIVE";
        if (r == 3) return "TILE";
        if (r == 4) return "DEBUG";
        return "IDLE";
    }
    void SetTileSettings(int uploadPerFrame, int cpuLimitMb, int gpuLimitMb, int maxQueueCount)
    {
        std::lock_guard<std::mutex> lock(tileCache.mtx);
        tileCache.maxUploadPerFrame = ClampInt(uploadPerFrame, 1, 64);
        tileCache.cpuLimitBytes = ClampInt(cpuLimitMb, 64, 4096) * 1024 * 1024;
        tileCache.gpuLimitBytes = ClampInt(gpuLimitMb, 64, 4096) * 1024 * 1024;
        tileCache.maxQueueCount = ClampInt(maxQueueCount, 32, 8192);
    }

    void Render()
    {
        if (!ctx || !rtv) return;

        PlanVisibleTiles();
        ProcessTileQueue();

        std::chrono::high_resolution_clock::time_point t0 = std::chrono::high_resolution_clock::now();
        float clr[4] = { 0.02f, 0.02f, 0.02f, 1 };
        ctx->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        ctx->ClearRenderTargetView(rtv.Get(), clr);

        D3D11_VIEWPORT vp{ 0, 0, (float)cw, (float)ch, 0, 1 };
        ctx->RSSetViewports(1, &vp);

        if (srv)
        {
            DrawTextureRegion(srv.Get(), 0.0f, 0.0f, (float)iw, (float)ih, std::max(1, texW), std::max(1, texH));
            DrawVisibleTiles();
        }

        DrawOverlays();
        swap->Present(1, 0);

        std::chrono::high_resolution_clock::time_point t1 = std::chrono::high_resolution_clock::now();
        renderMs = std::chrono::duration<float, std::milli>(t1 - t0).count();
        float dt = std::chrono::duration<float>(t1 - lastFrame).count();
        if (dt > 0) fps = 0.9f * fps + 0.1f * (1.0f / dt);
        lastFrame = t1;
    }

    void PushLine(std::vector<LineVtx>& v, float ax, float ay, float bx, float by, float r, float g, float b, float a)
    {
        float x1, y1, x2, y2;
        ImageToNdc(ax, ay, x1, y1);
        ImageToNdc(bx, by, x2, y2);
        v.push_back({ x1, y1, r, g, b, a });
        v.push_back({ x2, y2, r, g, b, a });
    }

    void ScreenToNdc(float sx, float sy, float& x, float& y)
    {
        x = (sx / (float)std::max(1, cw)) * 2.0f - 1.0f;
        y = 1.0f - (sy / (float)std::max(1, ch)) * 2.0f;
    }

    void PushScreenLine(std::vector<LineVtx>& v, float ax, float ay, float bx, float by, float r, float g, float b, float a)
    {
        float x1, y1, x2, y2;
        ScreenToNdc(ax, ay, x1, y1);
        ScreenToNdc(bx, by, x2, y2);
        v.push_back({ x1, y1, r, g, b, a });
        v.push_back({ x2, y2, r, g, b, a });
    }

    void ImageToScreen(float ix, float iy, float& sx, float& sy)
    {
        sx = ox + ix * scale;
        sy = oy + iy * scale;
    }

    void SnapForPixelGrid(float anchorScreenX, float anchorScreenY, float anchorImageX, float anchorImageY)
    {
        // At high inspection zoom, make one image pixel map to a stable integer screen grid.
        // This prevents large-BMP preview/full-res tiles from drifting into non-grid sampling
        // after repeated wheel zoom or zoom-ROI operations.
        if (scale < kPixelGridZoomThreshold) return;

        float minScale = std::max(0.0001f, fitScale * kMinZoomScaleFactor);
        scale = ClampFloat(std::round(scale), minScale, kMaxZoomScale);
        ox = std::round(anchorScreenX - anchorImageX * scale);
        oy = std::round(anchorScreenY - anchorImageY * scale);
    }

    float SnapScreenCoord(float v) const { return scale >= kPixelGridZoomThreshold ? std::round(v) : v; }

    void FillRectScreen(std::vector<LineVtx>& v, float x, float y, float w, float h, float r, float g, float b, float a)
    {
        for (float yy = y; yy <= y + h; yy += 0.55f)
        {
            PushScreenLine(v, x, yy, x + w, yy, r, g, b, a);
        }
    }

    void RectScreen(std::vector<LineVtx>& v, float x, float y, float w, float h, float r, float g, float b, float a)
    {
        PushScreenLine(v, x, y, x + w, y, r, g, b, a);
        PushScreenLine(v, x + w, y, x + w, y + h, r, g, b, a);
        PushScreenLine(v, x + w, y + h, x, y + h, r, g, b, a);
        PushScreenLine(v, x, y + h, x, y, r, g, b, a);
    }

    const char* Glyph7(char c)
    {
        switch(c)
        {
        case '0': return "01110100011001110101110011000101110";
        case '1': return "00100011000010000100001000010001110";
        case '2': return "01110100010000100010001000100011111";
        case '3': return "11110000010000101110000010000111110";
        case '4': return "00010001100101010010111110001000010";
        case '5': return "11111100001111000001000011000101110";
        case '6': return "00110010001000011110100011000101110";
        case '7': return "11111000010001000100010001000010000";
        case '8': return "01110100011000101110100011000101110";
        case '9': return "01110100011000101111000010001001100";
        case 'A': return "01110100011000111111100011000110001";
        case 'B': return "11110100011000111110100011000111110";
        case 'C': return "01110100011000010000100001000101110";
        case 'D': return "11110100011000110001100011000111110";
        case 'E': return "11111100001000011110100001000011111";
        case 'F': return "11111100001000011110100001000010000";
        case 'G': return "01110100011000010111100011000101110";
        case 'H': return "10001100011000111111100011000110001";
        case 'I': return "01110001000010000100001000010001110";
        case 'J': return "00111000100001000010000101001001100";
        case 'K': return "10001100101010011000101001001010001";
        case 'L': return "10000100001000010000100001000011111";
        case 'M': return "10001110111010110101100011000110001";
        case 'N': return "10001110011010110011100011000110001";
        case 'O': return "01110100011000110001100011000101110";
        case 'P': return "11110100011000111110100001000010000";
        case 'Q': return "01110100011000110001101011001001101";
        case 'R': return "11110100011000111110101001001010001";
        case 'S': return "01111100001000001110000010000111110";
        case 'T': return "11111001000010000100001000010000100";
        case 'U': return "10001100011000110001100011000101110";
        case 'V': return "10001100011000110001100010101000100";
        case 'W': return "10001100011000110101101011101110001";
        case 'X': return "10001100010101000100010101000110001";
        case 'Y': return "10001100010101000100001000010000100";
        case 'Z': return "11111000010001000100010001000011111";
        case '-': return "00000000000000011111000000000000000";
        case '.': return "00000000000000000000000000000000100";
        case ':': return "00000001000010000000001000010000000";
        case '/': return "00001000100001000100010001000010000";
        case '%': return "11001001010001000100010001010010011";
        case '_': return "00000000000000000000000000000011111";
        case ' ': return "00000000000000000000000000000000000";
        default: return "00000000000000000000000000000000000"; }
    }
    void DrawChar(std::vector<LineVtx>& v,float x,float y,float s,char c,float r,float g,float b){
        const char* m=Glyph7((char)toupper((unsigned char)c));
        for(int yy=0;yy<7;yy++) for(int xx=0;xx<5;xx++) if(m[yy*5+xx]=='1') FillRectScreen(v,x+xx*s,y+yy*s,s*0.92f,s*0.92f,r,g,b,1);
    }
    std::wstring Widen(const std::string& text){ return std::wstring(text.begin(), text.end()); }
    void QueueText(float x,float y,float w,float h,float pt,const std::string& text,COLORREF color,bool center=false){ TextItem ti; ti.x=x; ti.y=y; ti.w=w; ti.h=h; ti.pt=pt; ti.text=Widen(text); ti.color=color; ti.center=center; textItems.push_back(ti); }
    void QueueTextWide(float x,float y,float w,float h,float pt,const std::wstring& text,COLORREF color,bool center=false){ TextItem ti; ti.x=x; ti.y=y; ti.w=w; ti.h=h; ti.pt=pt; ti.text=text; ti.color=color; ti.center=center; textItems.push_back(ti); }
    void DrawQueuedText(std::vector<LineVtx>& v){
        // Fallback bitmap font kept for environments where DirectWrite target creation fails.
        for(size_t i=0;i<textItems.size();++i){
            TextItem& t=textItems[i];
            float r=(float)GetRValue(t.color)/255.0f, g=(float)GetGValue(t.color)/255.0f, b=(float)GetBValue(t.color)/255.0f;
            std::string s;
            s.reserve(t.text.size());
            for (wchar_t wc : t.text) s.push_back((wc >= 0 && wc <= 0x7F) ? static_cast<char>(wc) : '?');
            float scalePx = std::max(1.7f, t.pt / 7.0f);
            float cx=t.x, cy=t.y;
            for(size_t k=0;k<s.size();++k){ char c=s[k]; if(c=='\n'){ cy += scalePx*9.0f; cx = t.x; continue; } DrawChar(v, cx, cy, scalePx, c, r, g, b); cx += scalePx*6.0f; }
        }
        textItems.clear();
    }
    void DrawDWriteText(){
        if(textItems.empty()) return;
        if(!d2dTarget || !d2dBrush || !textFormat){ textItems.clear(); return; }
        d2dTarget->BeginDraw();
        for(size_t i=0;i<textItems.size();++i){
            TextItem& t=textItems[i];
            d2dBrush->SetColor(D2D1::ColorF(GetRValue(t.color)/255.0f, GetGValue(t.color)/255.0f, GetBValue(t.color)/255.0f, 1.0f));
            ComPtr<IDWriteTextFormat> fmt;
            if(writeFactory) writeFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, t.pt, L"", &fmt);
            if(!fmt) fmt = textFormat;
            if(fmt){ fmt->SetTextAlignment(t.center ? DWRITE_TEXT_ALIGNMENT_CENTER : DWRITE_TEXT_ALIGNMENT_LEADING); fmt->SetParagraphAlignment(t.center ? DWRITE_PARAGRAPH_ALIGNMENT_CENTER : DWRITE_PARAGRAPH_ALIGNMENT_NEAR); }
            D2D1_RECT_F rc = D2D1::RectF(t.x, t.y, t.x + t.w, t.y + t.h);
            d2dTarget->DrawTextW(t.text.c_str(), (UINT32)t.text.size(), fmt.Get(), rc, d2dBrush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
        }
        HRESULT hr = d2dTarget->EndDraw();
        if(hr == D2DERR_RECREATE_TARGET) CreateD2DTarget();
        textItems.clear();
    }
    void DrawGdiText(){}
    void DrawGrayNumbers(std::vector<LineVtx>& v){
        // Pixel grid and gray-value text are controlled independently.
        // showGray only controls the numeric gray-value labels, not the grid.
        if(iw<=0 || scale < kPixelGridZoomThreshold) return;
        float ix0=std::max(0.0f,(-ox)/scale), iy0=std::max(0.0f,(-oy)/scale);
        float ix1=std::min((float)iw,(cw-ox)/scale), iy1=std::min((float)ih,(ch-oy)/scale);
        int step=std::max(1,(int)std::floor(kPixelGridZoomThreshold/std::max(scale,1.0f)));
        if(scale >= kPixelGridZoomThreshold) step = 1;
        float cellScreen = step * scale;
        if(cellScreen < 18.0f) return;
        int startX = ((int)std::floor(ix0 / step)) * step;
        int startY = ((int)std::floor(iy0 / step)) * step;
        if(startX < 0) startX = 0; if(startY < 0) startY = 0;

        // Grid is always drawn when the zoom threshold is reached.
        for(int x=startX; x<= (int)ix1 + step; x+=step) PushLine(v,(float)x,iy0,(float)x,iy1,0.78f,0.78f,0.78f,0.65f);
        for(int y=startY; y<= (int)iy1 + step; y+=step) PushLine(v,ix0,(float)y,ix1,(float)y,0.78f,0.78f,0.78f,0.65f);

        // Numeric labels are optional and use their own threshold.
        if(!showGray || scale < kGrayValueTextZoomThreshold || cellScreen < 38.0f) return;
        int count=0;
        for(int y=startY; y<(int)iy1; y+=step){
            for(int x=startX; x<(int)ix1; x+=step){
                float cxImg = x + step*0.5f, cyImg = y + step*0.5f;
                if(cxImg < ix0 || cyImg < iy0 || cxImg >= ix1 || cyImg >= iy1) continue;
                if(count++ >= kMaxGrayValueTextCount) return;
                int sampleX = ClampInt((int)std::floor(cxImg),0,iw-1);
                int sampleY = ClampInt((int)std::floor(cyImg),0,ih-1);
                unsigned char exactValue = 0;
                // Do not show a known-inaccurate preview value while a full-resolution
                // tile is still loading. The label appears on the next rendered frame.
                if(!TryPixExact(sampleX, sampleY, exactValue)) continue;
                int val = exactValue;
                float sx,sy; ImageToScreen(cxImg, cyImg, sx, sy);
                char buf[16]; snprintf(buf,sizeof(buf),"%d",val);
                std::string t(buf);
                float boxW=std::max(26.0f,(float)t.size()*7.2f+7.0f), boxH=14.0f;
                FillRectScreen(v,sx-boxW*0.5f,sy-boxH*0.5f,boxW,boxH,0.34f,0.34f,0.34f,0.58f);
                QueueText(sx-boxW*0.5f,sy-boxH*0.5f-0.5f,boxW,boxH+1.0f,9.3f,t,RGB(255,255,0),true);
            }
        }
    }
    void DrawDebugOverlay(std::vector<LineVtx>& v){
        if(!showDebug) return;
        int q=0, ready=0, cpu=0, gpu=0, up=0, dup=0, stale=0, maxUp=0, cpuMb=0, gpuMb=0;
        GetTileDiag(q, ready, cpu, gpu, up, dup, stale, maxUp, cpuMb, gpuMb);
        FillRectScreen(v,18,18,360,172,0.0f,0.0f,0.0f,0.85f);
        char buf[512];
        snprintf(buf,sizeof(buf),
            "FPS %.1f  RENDER %.2f MS\n"
            "REASON %s  ZOOM %.3f\n"
            "TILE WORK %d  READY %d\n"
            "CPU TILE %d  GPU TILE %d\n"
            "UPLOAD/FRAME %d / %d\n"
            "DUPREQ %d  STALE %d\n"
            "CACHE CPU %dMB GPU %dMB",
            fps, renderMs, RenderReasonText(), scale, q, ready, cpu, gpu, up, maxUp, dup, stale, cpuMb, gpuMb);
        QueueText(28,26,336,162,12.0f,buf,RGB(255,255,255));
    }
    void DrawCornerL(std::vector<LineVtx>& v,float x1,float y1,float x2,float y2,float r,float g,float b){ float len=std::min(fabsf(x2-x1),fabsf(y2-y1))*0.18f; len=std::max(len,24.0f/std::max(scale,0.01f)); PushLine(v,x1,y1,x1+len,y1,r,g,b,1); PushLine(v,x1,y1,x1,y1+len,r,g,b,1); PushLine(v,x2,y1,x2-len,y1,r,g,b,1); PushLine(v,x2,y1,x2,y1+len,r,g,b,1); PushLine(v,x1,y2,x1+len,y2,r,g,b,1); PushLine(v,x1,y2,x1,y2-len,r,g,b,1); PushLine(v,x2,y2,x2-len,y2,r,g,b,1); PushLine(v,x2,y2,x2,y2-len,r,g,b,1); }
    void DrawHandleScreen(std::vector<LineVtx>& v,float ix,float iy){ float sx,sy; ImageToScreen(ix,iy,sx,sy); FillRectScreen(v,sx-4,sy-4,8,8,1.0f,1.0f,0.0f,1); RectScreen(v,sx-4,sy-4,8,8,0.0f,0.0f,0.0f,1); }
    void DrawHandles(std::vector<LineVtx>& v,const Overlay& o){
        if(o.readOnly||o.id!=selected) return;
        if(o.type==NV_OVERLAY_ROI){
            float ax,ay,bx,by,cx,cy,dx,dy; RectPts(o,ax,ay,bx,by,cx,cy,dx,dy);
            DrawHandleScreen(v,ax,ay); DrawHandleScreen(v,bx,by); DrawHandleScreen(v,cx,cy); DrawHandleScreen(v,dx,dy);
            float mx=(o.x1+o.x2)*0.5f, my=(o.y1+o.y2)*0.5f; float topCx=(ax+bx)*0.5f, topCy=(ay+by)*0.5f; float hx,hy;
            RotateAround(mx,my,o.angle,(o.x1+o.x2)*0.5f,o.y1-28.0f/std::max(scale,0.01f),hx,hy);
            PushLine(v,topCx,topCy,hx,hy,1,1,0,1); DrawHandleScreen(v,hx,hy);
        } else if(o.type==NV_OVERLAY_MEASURE){
            DrawHandleScreen(v,o.x1,o.y1); DrawHandleScreen(v,o.x2,o.y2);
        } else if(o.type==NV_OVERLAY_CIRCLE){
            float lx,ly,tx,ty,rx,ry,bx,by,txa,tya; EllipseHandlePts(o,lx,ly,tx,ty,rx,ry,bx,by,txa,tya);
            DrawHandleScreen(v,lx,ly); DrawHandleScreen(v,tx,ty); DrawHandleScreen(v,rx,ry); DrawHandleScreen(v,bx,by);
            float cx=(o.x1+o.x2)*0.5f, cy=(o.y1+o.y2)*0.5f; PushLine(v,cx,cy,txa,tya,1,1,0,1); DrawHandleScreen(v,txa,tya);
        }
    }
    void DrawOverlays(){ textItems.clear(); std::vector<LineVtx> v; for(size_t oi=0; oi<overlays.size(); ++oi){ Overlay& o = overlays[oi]; if(!o.visible)continue;
        float r=0.1f,g=1.0f,b=0.1f;
        if(o.id==selected){ r=1.0f; g=1.0f; b=0.0f; }
        else if(o.type==NV_OVERLAY_AOI_DEFECT){ r=1.0f; g=0.05f; b=0.05f; }
        else if(o.type==NV_OVERLAY_MEASURE){ r=0.25f; g=0.85f; b=1.0f; }
        else if(o.type==NV_OVERLAY_CIRCLE || o.type==NV_OVERLAY_AOI_DIE_CENTER){ r=0.0f; g=1.0f; b=0.1f; }
        else if(o.type==NV_OVERLAY_FIXED_CENTER_RECT){ r=1.0f; g=0.65f; b=0.0f; }
        else if(o.type==NV_OVERLAY_DRAW_POLYGON){ r=1.0f; g=0.3f; b=1.0f; }
        if(o.type==NV_OVERLAY_AOI_DIE_CORNER){
            DrawCornerL(v,o.x1,o.y1,o.x2,o.y2,r,g,b);
        } else if(o.type==NV_OVERLAY_ROI || o.type==NV_OVERLAY_AOI_DEFECT || o.type==NV_OVERLAY_FIXED_CENTER_RECT || o.type==NV_OVERLAY_DRAW_POLYGON){
            if(o.type==NV_OVERLAY_ROI || o.type==NV_OVERLAY_FIXED_CENTER_RECT){ float ax,ay,bx,by,cx,cy,dx,dy; RectPts(o,ax,ay,bx,by,cx,cy,dx,dy); PushLine(v,ax,ay,bx,by,r,g,b,1); PushLine(v,bx,by,cx,cy,r,g,b,1); PushLine(v,cx,cy,dx,dy,r,g,b,1); PushLine(v,dx,dy,ax,ay,r,g,b,1); }
            else if(o.type==NV_OVERLAY_AOI_DEFECT) { PushLine(v,o.x1,o.y1,o.x2,o.y1,r,g,b,1); PushLine(v,o.x2,o.y1,o.x2,o.y2,r,g,b,1); PushLine(v,o.x2,o.y2,o.x1,o.y2,r,g,b,1); PushLine(v,o.x1,o.y2,o.x1,o.y1,r,g,b,1); }
            else if(o.type==NV_OVERLAY_DRAW_POLYGON){
                const size_t npts = o.poly.size() / 2;
                if(npts >= 2){
                    for(size_t pi=0; pi<npts; ++pi){
                        size_t pj = (pi + 1) % npts;
                        PushLine(v, o.poly[pi*2], o.poly[pi*2+1], o.poly[pj*2], o.poly[pj*2+1], r, g, b, 1);
                    }
                    for(size_t pi=0; pi<npts; ++pi){ DrawHandleScreen(v, o.poly[pi*2], o.poly[pi*2+1]); }
                    if(!o.name.empty()){
                        float sx,sy;
                        ImageToScreen(o.x1,o.y1,sx,sy);
                        float textUnits=0;
                        for(size_t ci=0;ci<o.name.size();++ci) textUnits += o.name[ci] > 0x7f ? 2.0f : 1.0f;
                        float boxW=ClampFloat(textUnits*7.2f+12.0f,52.0f,420.0f), boxH=22.0f;
                        float labelY=sy-boxH-2.0f;
                        if(labelY<2.0f) labelY=sy+2.0f;
                        FillRectScreen(v,sx,labelY,boxW,boxH,0.0f,0.0f,0.0f,0.88f);
                        QueueTextWide(sx+5.0f,labelY+1.0f,boxW-10.0f,boxH-2.0f,12.0f,o.name,RGB(255,255,255));
                    }
                }
            }
            if(o.type==NV_OVERLAY_AOI_DEFECT){ float sx,sy; ImageToScreen(std::min(o.x1,o.x2),std::min(o.y1,o.y2),sx,sy); std::string label="W "+std::to_string((int)fabsf(o.x2-o.x1))+"um H "+std::to_string((int)fabsf(o.y2-o.y1))+"um"; FillRectScreen(v,sx,sy-24,(float)label.size()*9.5f+10,22,0,0,0,0.92f); QueueText(sx+5,sy-22,(float)label.size()*10.5f+8,22,13.0f,label,RGB(255,255,255)); }
        } else if(o.type==NV_OVERLAY_MEASURE){ PushLine(v,o.x1,o.y1,o.x2,o.y2,r,g,b,1); }
        else { float cx=(o.x1+o.x2)*0.5f, cy=(o.y1+o.y2)*0.5f; float rx=std::max(1.0f,fabsf(o.x2-o.x1)*0.5f); float ry=std::max(1.0f,fabsf(o.y2-o.y1)*0.5f); if(o.type==NV_OVERLAY_AOI_DIE_CENTER){ cx=o.x1; cy=o.y1; float rr=std::max(6.0f/std::max(scale,0.01f), std::min(18.0f/std::max(scale,0.01f), std::max(1.0f, fabsf(o.x2-o.x1)))); const int n=48; for(int i=0;i<n;i++){ float a1=6.2831853f*i/n,a2=6.2831853f*(i+1)/n; PushLine(v,cx+cosf(a1)*rr,cy+sinf(a1)*rr,cx+cosf(a2)*rr,cy+sinf(a2)*rr,r,g,b,1); } PushLine(v,cx-rr*0.65f,cy,cx+rr*0.65f,cy,r,g,b,1); PushLine(v,cx,cy-rr*0.65f,cx,cy+rr*0.65f,r,g,b,1); } else { const int n=96; for(int i=0;i<n;i++){ float a1=6.2831853f*i/n,a2=6.2831853f*(i+1)/n; float p1x=cx+cosf(a1)*rx,p1y=cy+sinf(a1)*ry,p2x=cx+cosf(a2)*rx,p2y=cy+sinf(a2)*ry; float rp1x,rp1y,rp2x,rp2y; RotateAround(cx,cy,o.angle,p1x,p1y,rp1x,rp1y); RotateAround(cx,cy,o.angle,p2x,p2y,rp2x,rp2y); PushLine(v,rp1x,rp1y,rp2x,rp2y,r,g,b,1);} float ex,ey; RotateAround(cx,cy,o.angle,cx+rx*0.75f,cy,ex,ey); PushLine(v,cx,cy,ex,ey,r,g,b,1); } }
        DrawHandles(v,o);
    }
        if(showCross&&iw>0){ float cx=iw*0.5f,cy=ih*0.5f; PushLine(v,0,cy,(float)iw,cy,0.0f,1.0f,0.1f,1); PushLine(v,cx,0,cx,(float)ih,0.0f,1.0f,0.1f,1); }
        if(dragging){
            float ix,iy; ScreenToImage(lastX,lastY,ix,iy);
            if(dragKind==NV_TOOL_ROI||dragKind==NV_TOOL_ZOOM_RECT){
                PushLine(v,roiStartX,roiStartY,ix,roiStartY,1,1,0,1); PushLine(v,ix,roiStartY,ix,iy,1,1,0,1); PushLine(v,ix,iy,roiStartX,iy,1,1,0,1); PushLine(v,roiStartX,iy,roiStartX,roiStartY,1,1,0,1);
                float sx,sy; ImageToScreen(std::min(roiStartX,ix),std::min(roiStartY,iy),sx,sy);
                char buf[96]; snprintf(buf,sizeof(buf),"W %.1f  H %.1f",fabsf(ix-roiStartX),fabsf(iy-roiStartY));
                FillRectScreen(v,sx,sy-24,170,22,0.0f,0.0f,0.0f,0.86f); QueueText(sx+5,sy-22,170,22,13.0f,buf,RGB(255,255,255));
            }
            else if(dragKind==NV_TOOL_MEASURE){
                PushLine(v,roiStartX,roiStartY,ix,iy,1,1,0,1);
                DrawHandleScreen(v,roiStartX,roiStartY); DrawHandleScreen(v,ix,iy);
                float mx=(roiStartX+ix)*0.5f, my=(roiStartY+iy)*0.5f, sx,sy; ImageToScreen(mx,my,sx,sy);
                float len=hypotf(ix-roiStartX,iy-roiStartY); char buf[96]; snprintf(buf,sizeof(buf),"L %.1f",len);
                FillRectScreen(v,sx,sy-26,92,22,0.0f,0.0f,0.0f,0.86f); QueueText(sx+5,sy-24,92,22,13.0f,buf,RGB(255,255,255));
            }
            else if(dragKind==NV_TOOL_CIRCLE){
                float x1=std::min(roiStartX,ix), y1=std::min(roiStartY,iy), x2=std::max(roiStartX,ix), y2=std::max(roiStartY,iy);
                float cx=(x1+x2)*0.5f, cy=(y1+y2)*0.5f, rx=std::max(1.0f,(x2-x1)*0.5f), ry=std::max(1.0f,(y2-y1)*0.5f);
                const int n=80; for(int i=0;i<n;i++){ float a1=6.2831853f*i/n,a2=6.2831853f*(i+1)/n; PushLine(v,cx+cosf(a1)*rx,cy+sinf(a1)*ry,cx+cosf(a2)*rx,cy+sinf(a2)*ry,1,1,0,1); }
                DrawHandleScreen(v,x1,y1); DrawHandleScreen(v,x2,y1); DrawHandleScreen(v,x2,y2); DrawHandleScreen(v,x1,y2);
                float sx,sy; ImageToScreen(x1,y1,sx,sy); char buf[96]; snprintf(buf,sizeof(buf),"W %.1f  H %.1f",x2-x1,y2-y1);
                FillRectScreen(v,sx,sy-24,170,22,0.0f,0.0f,0.0f,0.86f); QueueText(sx+5,sy-22,170,22,13.0f,buf,RGB(255,255,255));
            }
        }
        DrawGrayNumbers(v); DrawDebugOverlay(v); if(!d2dTarget || !d2dBrush || !textFormat) DrawQueuedText(v); if(v.empty()){ DrawDWriteText(); return; } if(v.size()>524288) v.resize(524288); D3D11_MAPPED_SUBRESOURCE m{}; if(SUCCEEDED(ctx->Map(lvb.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&m))){ memcpy(m.pData,v.data(),v.size()*sizeof(LineVtx)); ctx->Unmap(lvb.Get(),0); UINT stride=sizeof(LineVtx),off=0; float blendFactor[4]={0,0,0,0}; if(alphaBlend) ctx->OMSetBlendState(alphaBlend.Get(), blendFactor, 0xffffffff); ctx->IASetInputLayout(lil.Get()); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST); ctx->IASetVertexBuffers(0,1,lvb.GetAddressOf(),&stride,&off); ctx->VSSetShader(lvs.Get(),nullptr,0); ctx->PSSetShader(lps.Get(),nullptr,0); ctx->Draw((UINT)v.size(),0); ctx->OMSetBlendState(nullptr, blendFactor, 0xffffffff);} DrawDWriteText(); }
    int ComputeOtsuThreshold()
    {
        if (pixels.empty()) return threshold;

        int hist[256];
        memset(hist, 0, sizeof(hist));
        for (size_t i = 0; i < pixels.size(); ++i)
        {
            hist[pixels[i]]++;
        }

        long long total = (long long)pixels.size();
        long long sum = 0;
        for (int i = 0; i < 256; i++)
        {
            sum += (long long)i * hist[i];
        }

        long long wB = 0, sumB = 0;
        double best = -1.0;
        int bestT = threshold;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t];
            if (wB == 0) continue;

            long long wF = total - wB;
            if (wF == 0) break;

            sumB += (long long)t*hist[t];
            double mB = (double)sumB / (double)wB;
            double mF = (double)(sum - sumB) / (double)wF;
            double between = (double)wB * (double)wF * (mB - mF) * (mB - mF);
            if (between > best)
            {
                best = between;
                bestT = t;
            }
        }

        threshold = bestT;
        return bestT;
    }

    int AddOverlay(int type, float x, float y, float w, float h, float angle, const wchar_t* nm)
    {
        Overlay o{};
        o.id = nextId++;
        o.type = type;
        o.visible = true;
        o.readOnly = IsAoiType(type) || IsFixedCenterRectType(type) || IsDrawPolygonType(type);
        o.x1 = x;
        o.y1 = y;
        o.x2 = x + w;
        o.y2 = y + h;
        o.x3 = 0;
        o.y3 = 0;
        o.angle = angle;
        o.name = nm ? nm : L"Object";
        overlays.push_back(o);

        if (!o.readOnly) selected = o.id;
        return o.id;
    }

    int AddPolygonOverlay(int type, const float* xyPairs, int pointCount, const wchar_t* nm)
    {
        if (!xyPairs || pointCount < 3 || pointCount > 100000) return 0;

        Overlay o{};
        o.id = nextId++;
        o.type = type;
        o.visible = true;
        o.readOnly = true;
        o.angle = 0;
        o.name = nm ? nm : L"Polygon";
        o.poly.assign(xyPairs, xyPairs + pointCount * 2);

        float minx = xyPairs[0], maxx = xyPairs[0];
        float miny = xyPairs[1], maxy = xyPairs[1];
        for (int i = 1; i < pointCount; i++)
        {
            float x = xyPairs[i * 2];
            float y = xyPairs[i * 2 + 1];
            minx = std::min(minx, x);
            maxx = std::max(maxx, x);
            miny = std::min(miny, y);
            maxy = std::max(maxy, y);
        }

        o.x1 = minx;
        o.y1 = miny;
        o.x2 = maxx;
        o.y2 = maxy;
        o.x3 = pointCount > 0 ? xyPairs[0] : 0;
        o.y3 = pointCount > 0 ? xyPairs[1] : 0;
        overlays.push_back(std::move(o));
        return overlays.back().id;
    }

    void UpdateOverlay(int id, float x, float y, float w, float h, float angle)
    {
        for (size_t i = 0; i < overlays.size(); ++i)
        {
            if (overlays[i].id == id && (!overlays[i].readOnly || IsFixedCenterRectType(overlays[i].type)))
            {
                Overlay& o = overlays[i];
                o.x1 = x;
                o.y1 = y;
                o.x2 = x + w;
                o.y2 = y + h;
                o.angle = angle;
                break;
            }
        }
    }

    int Analyze(NVRoiStats* st, int* hist, double* px, int capx, double* py, int capy)
    {
        if (!st) return 0;

        *st = {};
        Overlay* roi = nullptr;
        for (size_t i = 0; i < overlays.size(); ++i)
        {
            if (overlays[i].id == selected && overlays[i].type == NV_OVERLAY_ROI)
            {
                roi = &overlays[i];
                break;
            }
        }

        if (!roi) return 0;

        int x0 = (int)ClampDouble(std::floor((double)std::min(roi->x1, roi->x2)), 0.0, (double)iw);
        int y0 = (int)ClampDouble(std::floor((double)std::min(roi->y1, roi->y2)), 0.0, (double)ih);
        int x1 = (int)ClampDouble(std::ceil((double)std::max(roi->x1, roi->x2)), 0.0, (double)iw);
        int y1 = (int)ClampDouble(std::ceil((double)std::max(roi->y1, roi->y2)), 0.0, (double)ih);
        int w = x1 - x0;
        int h = y1 - y0;
        if (w <= 0 || h <= 0) return 0;

        if (hist) memset(hist, 0, 256 * sizeof(int));
        if (px) memset(px, 0, sizeof(double) * capx);
        if (py) memset(py, 0, sizeof(double) * capy);

        long long sum = 0, sum2 = 0;
        int mn = 255, mx = 0;
        int astep = std::max(1, sampleStep);
        int n = 0;

        for (int y = y0; y < y1; y += astep)
        {
            for (int x = x0; x < x1; x += astep)
            {
                int v = Pix(x, y);
                sum += v;
                sum2 += (long long)v * v;
                mn = std::min(mn, v);
                mx = std::max(mx, v);
                if (hist) hist[v]++;
                if (px && (x - x0) / astep < capx) px[(x - x0) / astep] += v;
                if (py && (y - y0) / astep < capy) py[(y - y0) / astep] += v;
                n++;
            }
        }

        if (n <= 0) return 0;

        double mean = (double)sum / n;
        double var = std::max(0.0, (double)sum2 / n - mean * mean);
        st->valid = 1;
        st->x = x0;
        st->y = y0;
        st->width = w;
        st->height = h;
        st->pixels = n;
        st->mean = mean;
        st->minValue = mn;
        st->maxValue = mx;
        st->stddev = sqrt(var);
        Blob(x0, y0, w, h, st);
        return 1;
    }

    void Blob(int x0, int y0, int w, int h, NVRoiStats* st)
    {
        if ((long long)w * h > 16000000LL)
        {
            st->blobCount = -1;
            return;
        }

        std::vector<unsigned char> seen((size_t)w * h);
        int th = threshold;
        int blobs = 0, best = 0;
        int bx = 0, by = 0, bw = 0, bh = 0;
        int dx[4] = { 1, -1, 0, 0 };
        int dy[4] = { 0, 0, 1, -1 };

        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                size_t idx = (size_t)yy * w + xx;
                if (seen[idx] || Pix(x0 + xx, y0 + yy) < th) continue;

                blobs++;
                int area = 0;
                int minx = xx, maxx = xx;
                int miny = yy, maxy = yy;
                std::queue<std::pair<int, int>> q;
                q.push({ xx, yy });
                seen[idx] = 1;

                while (!q.empty())
                {
                    std::pair<int, int> cur = q.front();
                    q.pop();

                    int cx = cur.first;
                    int cy = cur.second;
                    area++;
                    minx = std::min(minx, cx);
                    maxx = std::max(maxx, cx);
                    miny = std::min(miny, cy);
                    maxy = std::max(maxy, cy);

                    for (int k = 0; k < 4; k++)
                    {
                        int nx = cx + dx[k];
                        int ny = cy + dy[k];
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;

                        size_t ni = (size_t)ny * w + nx;
                        if (!seen[ni] && Pix(x0 + nx, y0 + ny) >= th)
                        {
                            seen[ni] = 1;
                            q.push({ nx, ny });
                        }
                    }
                }

                if (area > best)
                {
                    best = area;
                    bx = x0 + minx;
                    by = y0 + miny;
                    bw = maxx - minx + 1;
                    bh = maxy - miny + 1;
                }
            }
        }

        st->blobCount = blobs;
        st->largestBlobArea = best;
        st->largestBlobX = bx;
        st->largestBlobY = by;
        st->largestBlobW = bw;
        st->largestBlobH = bh;
    }
};

// ===== Exported C API =====

NV_API void* __stdcall NV_Create(HWND hwnd)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    NativeViewer* v = new NativeViewer();
    if (!v->Init(hwnd))
    {
        delete v;
        return nullptr;
    }

    return v;
}

NV_API void __stdcall NV_Destroy(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;
    delete v;
}

NV_API int __stdcall NV_LoadImage(void* viewer, const wchar_t* path, NVImageInfo* info)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->Load(path, info) ? 1 : 0;
}

NV_API void __stdcall NV_Resize(void* viewer, int w, int h)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->Resize(w, h);
}

NV_API void __stdcall NV_Render(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->Render();
}

NV_API void __stdcall NV_FitToWindow(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->Fit();
}

NV_API void __stdcall NV_OneToOne(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->One();
}

NV_API void __stdcall NV_SetToolMode(void* viewer, int mode)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->tool = mode;
}

NV_API void __stdcall NV_SetOptions(void* viewer, int c, int g, int d)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->showCross = c != 0;
    v->showGray = g != 0;
    v->showDebug = d != 0;
}

NV_API void __stdcall NV_SetProcessing(void* viewer, int op, int dm, int th, double ga, int ra)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->proc = op;
    v->display = dm;
    v->threshold = ClampInt(th, 0, 255);
    v->gamma = std::max(0.01, ga);
    v->radius = std::max(1, ra);
}

NV_API void __stdcall NV_MouseDown(void* viewer, int b, int x, int y, int m)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->MouseDown(b, x, y);
}

NV_API void __stdcall NV_MouseMove(void* viewer, int x, int y, int m)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->MouseMove(x, y);
}

NV_API void __stdcall NV_MouseUp(void* viewer, int b, int x, int y, int m)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->MouseUp(b, x, y);
}

NV_API void __stdcall NV_MouseWheel(void* viewer, int delta, int x, int y)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->Wheel(delta, x, y);
}

NV_API int __stdcall NV_GetMouseImageInfo(void* viewer, int sx, int sy, int* ix, int* iy, int* gray)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    float x, y;
    if (!v->ScreenToImage(sx, sy, x, y)) return 0;

    int xi = (int)x;
    int yi = (int)y;
    if (ix) *ix = xi;
    if (iy) *iy = yi;
    if (gray)
    {
        unsigned char exactValue = 0;
        *gray = v->TryPixExact(xi, yi, exactValue) ? exactValue : v->Pix(xi, yi);
    }
    return 1;
}

NV_API void __stdcall NV_GetDiagnostics(void* viewer, NVDiagnostics* d)
{
    if (!d) return;

    *d = {};
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    d->fps = v->fps;
    d->renderMs = v->renderMs;
    d->scale = v->scale;
    d->offsetX = v->ox;
    d->offsetY = v->oy;
    d->imageWidth = v->iw;
    d->imageHeight = v->ih;
    d->overlayCount = (int)v->overlays.size();
    d->gpuUploadCount = v->uploadCount;
    v->GetTileDiag(d->pendingWorkerRequests, d->readyToUploadTiles, d->cpuCachedTiles, d->gpuResidentTiles, d->uploadedTilesLastFrame, d->duplicateTileRequests, d->staleTileDiscards, d->maxTileUploadPerFrame, d->cpuCacheLimitMb, d->gpuCacheLimitMb);
    d->renderReason = v->renderReason.load();
}

NV_API int __stdcall NV_GetOverlayCount(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return (int)v->overlays.size();
}

NV_API int __stdcall NV_GetOverlayInfo(void* viewer, int index, NVOverlayInfo* info)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v || !info) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    if (index < 0 || index >= (int)v->overlays.size()) return 0;

    Overlay& o = v->overlays[index];
    *info = {};
    info->id = o.id;
    info->type = o.type;
    info->visible = o.visible ? 1 : 0;
    info->x1 = o.x1;
    info->y1 = o.y1;
    info->x2 = o.x2;
    info->y2 = o.y2;
    info->x3 = o.x3;
    info->y3 = o.y3;
    info->angle = o.angle;
    wcsncpy_s(info->name, _countof(info->name), o.name.c_str(), _TRUNCATE);
    return 1;
}

NV_API int __stdcall NV_GetSelectedOverlayInfo(void* viewer, NVOverlayInfo* info)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v || !info) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    if (v->selected <= 0) return 0;

    Overlay* o = v->FindOverlay(v->selected);
    if (!o) return 0;

    *info = {};
    info->id = o->id;
    info->type = o->type;
    info->visible = o->visible ? 1 : 0;
    info->x1 = o->x1;
    info->y1 = o->y1;
    info->x2 = o->x2;
    info->y2 = o->y2;
    info->x3 = o->x3;
    info->y3 = o->y3;
    info->angle = o->angle;
    wcsncpy_s(info->name, _countof(info->name), o->name.c_str(), _TRUNCATE);
    return 1;
}

NV_API void __stdcall NV_SetOverlayVisible(void* viewer, int id, int vis)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    for (size_t i = 0; i < v->overlays.size(); ++i)
    {
        if (v->overlays[i].id == id)
        {
            v->overlays[i].visible = vis != 0;
        }
    }
}

NV_API void __stdcall NV_SelectOverlay(void* viewer, int id)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->selected = id;
}

NV_API void __stdcall NV_DeleteOverlay(void* viewer, int id)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->overlays.erase(std::remove_if(v->overlays.begin(), v->overlays.end(), OverlayDeletePredicate(id)), v->overlays.end());
    if (v->selected == id) v->selected = -1;
}

NV_API void __stdcall NV_ClearOverlays(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->overlays.erase(std::remove_if(v->overlays.begin(), v->overlays.end(), OverlayClearPredicate()), v->overlays.end());
    v->selected = -1;
}

NV_API int __stdcall NV_AnalyzeSelectedRoi(void* viewer, NVRoiStats* s, int* h, double* px, int cx, double* py, int cy)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->Analyze(s, h, px, cx, py, cy);
}

NV_API int __stdcall NV_AddOverlay(void* viewer, int type, float x, float y, float w, float h, float angle, const wchar_t* name)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->AddOverlay(type, x, y, w, h, angle, name);
}

NV_API void __stdcall NV_UpdateOverlay(void* viewer, int id, float x, float y, float w, float h, float angle)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->UpdateOverlay(id, x, y, w, h, angle);
}

NV_API int __stdcall NV_AddPolygonOverlay(void* viewer, int type, const float* xyPairs, int pointCount, const wchar_t* name)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->AddPolygonOverlay(type, xyPairs, pointCount, name);
}

NV_API int __stdcall NV_ComputeOtsuThreshold(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 128;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->ComputeOtsuThreshold();
}

NV_API int __stdcall NV_GetPendingTileCount(void* viewer)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return 0;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    return v->TilePendingCount();
}

NV_API void __stdcall NV_SetTileSettings(void* viewer, int uploadPerFrame, int cpuCacheLimitMb, int gpuCacheLimitMb, int maxQueueCount)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    std::lock_guard<std::mutex> lock(v->apiMtx);
    v->SetTileSettings(uploadPerFrame, cpuCacheLimitMb, gpuCacheLimitMb, maxQueueCount);
}

NV_API void __stdcall NV_SetRenderReason(void* viewer, int reason)
{
    NativeViewer* v = (NativeViewer*)viewer;
    if (!v) return;

    v->renderReason.store(reason);
}

NV_API int __stdcall NV_GetLastError(void* viewer, wchar_t* b, int cap)
{
    if (!b || cap <= 0) return 0;

    NativeViewer* v = (NativeViewer*)viewer;
    if (!v)
    {
        wcsncpy_s(b, cap, L"Native viewer is null", _TRUNCATE);
        return 21;
    }

    std::lock_guard<std::mutex> lock(v->apiMtx);
    std::wstring s = v->lastError;
    wcsncpy_s(b, cap, s.c_str(), _TRUNCATE);
    return (int)s.size();
}
