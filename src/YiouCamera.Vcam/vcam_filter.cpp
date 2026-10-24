// vcam_filter.cpp - production DirectShow "virtual camera" source filter for the
//                   YiouCamera / ICCCE wireless document-camera integration.
//
// Self-contained raw COM (no ATL, no strmbase): the DirectShow base classes are NOT
// shipped with the modern Windows SDK, so everything the host touches is implemented here.
//
// Contract: docs/CONTRACTS.md section 5 and docs/PLAN.md section 8 (C1-C20).
//   C1  register in CLSID_VideoInputDeviceCategory (per-user HKCU, no elevation)
//   C2  moniker BindToObject works
//   C3  x64 in-proc COM, no STA assumption (ThreadingModel=Both, one worker thread)
//   C4  TWO output pins with proper pin categories, like a real capture filter:
//         pin "Capture" -> PIN_CATEGORY_CAPTURE + IAMStreamConfig
//         pin "Preview" -> PIN_CATEGORY_PREVIEW
//       WPFMediaKit's VideoCapturePlayer does
//       RenderStream(PinCategory.Preview, MediaType.Video, filter, null, renderer),
//       which needs a Preview pin - a capture-only source fails with VFW_E_CANNOT_CONNECT.
//       Both pins are fed by ONE worker thread from ONE frame source (Smart-Tee-like),
//       so no SHM double-read and no halved frame rate.
//   C6  IAMStreamConfig on both pins (GetNumberOfCapabilities / GetStreamCaps /
//       SetFormat / GetFormat)
//   C8  FORMAT_VideoInfo, RGB24, positive biHeight, stride = (w*3+3) & ~3
//   C10 SetFormat works while unconnected (also tolerates w=h=0 the way WPFMediaKit
//       calls it when DesiredPixelWidth/Height were never set: keeps the default format)
//   C11 push source: worker thread calls IMemInputPin::Receive; Stop joins it
//   C13 repeated Stop -> rebuild leaves nothing behind (allocators are kept across
//       Stop/Run and only released on Disconnect; the worker thread is restarted)
//   C14 graph build < 1.5 s + MediaOpened < 1.5 s: a gray "no signal" placeholder frame
//       is delivered immediately on Pause (preroll) and Run - never waits for the producer
//   C17 FriendlyName is stable: 驿欧无线展台  (U+9A7F U+6B27 U+65E0 U+7EBF U+5C55 U+53F0)
//   C18 DevicePath is written by the registration code so DsDevice.DevicePath is
//       never null:  yocam:yiou-wireless-dc
//   intentionally NOT implemented: IAMVideoProcAmp / IAMCameraControl (PLAN section 8)
//
// Frame source: Local\YoCameraVcamFrames (see vcam_shm.h). When the producer is absent,
// stalled or torn, the filter keeps pushing a gray placeholder at the frame rate - it
// never stops pushing (otherwise the host preview freezes).
//
// Registry (per-user, DllInstall -> regsvr32 /i or LoadLibrary+DllInstall):
//   HKCU\Software\Classes\CLSID\{9A3E...}\(default)                = 驿欧无线展台
//   HKCU\Software\Classes\CLSID\{9A3E...}\InprocServer32\(default) = <dll path>
//   HKCU\Software\Classes\CLSID\{9A3E...}\InprocServer32\ThreadingModel = Both
//   HKCU\Software\Classes\CLSID\{860BB310...}\Instance\{9A3E...}\FriendlyName = 驿欧无线展台
//   HKCU\Software\Classes\CLSID\{860BB310...}\Instance\{9A3E...}\CLSID        = {9A3E...}
//   HKCU\Software\Classes\CLSID\{860BB310...}\Instance\{9A3E...}\DevicePath   = yocam:yiou-wireless-dc
//
// Build: src\YiouCamera.Vcam\build.ps1  (static CRT, only system DLLs)
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dshow.h>
#include <strsafe.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <vector>
#include <string>
#include <atomic>
#include <thread>
#include <chrono>

#include "vcam_shm.h"

#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "uuid.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")

// ---------------------------------------------------------------------------
// constants frozen by docs/CONTRACTS.md section 1
// ---------------------------------------------------------------------------
// {9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}
static const GUID CLSID_YoCameraVirtualCam =
{ 0x9A3E7D21, 0x4C5B, 0x4E8A, { 0x9B, 0x77, 0x2D, 0x14, 0x6C, 0xE0, 0x5A, 0x31 } };

#define VCAM_CATEGORY_CLSID_STR L"{860BB310-5D01-11d0-BD3B-00A0C911CE86}" // CLSID_VideoInputDeviceCategory
#define VCAM_CLSID_STR          L"{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}"
#define VCAM_DEVICE_PATH        L"yocam:yiou-wireless-dc"

// 驿欧无线展台 - pure ASCII escapes so the source file encoding can never mangle it.
static const wchar_t* FILTER_FRIENDLY_NAME =
    L"\u9A7F\u6B27\u65E0\u7EBF\u5C55\u53F0";

// Default and advertised formats (720p is the contract default, 1080p is selectable).
static const int VCAM_DEFAULT_W = 1280;
static const int VCAM_DEFAULT_H = 720;
static const int VCAM_FPS       = 30;
static const int VCAM_MAX_W     = 1920;
static const int VCAM_MAX_H     = 1080;

static LONG      g_cDllRef = 0;
static HINSTANCE g_hInst   = nullptr;

// ---------------------------------------------------------------------------
// optional diagnostics: set the environment variable VCAM_LOG=<file path> before the
// host starts and the filter appends a line-level trace to that file. Off by default;
// zero cost when unset. Used by the regression tooling to explain "graph opened but no
// frames" situations.
// ---------------------------------------------------------------------------
static CRITICAL_SECTION g_logCs;
static bool             g_logCsInit = false;
static WCHAR            g_logPath[MAX_PATH] = { 0 };
static bool             g_logPathInit = false;
static HANDLE           g_logHandle = INVALID_HANDLE_VALUE;

// Deliberately CRT-free: only Win32 + strsafe, so the trace can never trip a CRT
// invalid-parameter fast-fail (that is exactly what it is there to diagnose).
static void VcamLog(const char* fmt, ...)
{
    if (!g_logCsInit) return;
    EnterCriticalSection(&g_logCs);
    if (!g_logPathInit)
    {
        g_logPathInit = true;
        DWORD n = GetEnvironmentVariableW(L"YIOU_VCAM_LOG", g_logPath, MAX_PATH);
        if (n == 0 || n >= MAX_PATH)
            n = GetEnvironmentVariableW(L"VCAM_LOG", g_logPath, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) g_logPath[0] = 0;
    }
    if (g_logPath[0] != 0)
    {
        if (g_logHandle == INVALID_HANDLE_VALUE)
        {
            g_logHandle = CreateFileW(g_logPath, FILE_APPEND_DATA,
                                      FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                      OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        }
        if (g_logHandle != INVALID_HANDLE_VALUE)
        {
            char msg[900];
            msg[0] = 0;
            va_list ap;
            va_start(ap, fmt);
            StringCchVPrintfA(msg, ARRAYSIZE(msg), fmt, ap);
            va_end(ap);
            SYSTEMTIME st;
            GetLocalTime(&st);
            char line[1024];
            StringCchPrintfA(line, ARRAYSIZE(line), "[%02u:%02u:%02u.%03u tid=%lu] %s\r\n",
                             st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                             (unsigned long)GetCurrentThreadId(), msg);
            DWORD written = 0;
            WriteFile(g_logHandle, line, (DWORD)strlen(line), &written, nullptr);
        }
    }
    LeaveCriticalSection(&g_logCs);
}

// ---------------------------------------------------------------------------
// small helpers
// ---------------------------------------------------------------------------
static LONGLONG QpcNow100ns()
{
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return f.QuadPart ? (c.QuadPart * 10000000LL / f.QuadPart) : 0;
}

// Allocator strategy override for diagnostics / compatibility experiments:
//   unset or "0" : renderer allocator first, own-memory fallback if it cannot be committed
//   "1"          : renderer allocator only (never fall back)
//   "2"          : always our own CLSID_MemoryAllocator
static DWORD AllocMode()
{
    static DWORD cached = 0xFFFFFFFFu;
    if (cached == 0xFFFFFFFFu)
    {
        WCHAR buf[16] = { 0 };
        const DWORD n = GetEnvironmentVariableW(L"YIOU_VCAM_ALLOC", buf, ARRAYSIZE(buf));
        cached = (n > 0) ? (DWORD)_wtoi(buf) : 0u;
        if (cached > 2) cached = 0;
    }
    return cached;
}

static void FreeMediaType(AM_MEDIA_TYPE& mt)
{
    if (mt.cbFormat != 0 && mt.pbFormat != nullptr)
        CoTaskMemFree(mt.pbFormat);
    if (mt.pUnk != nullptr)
        mt.pUnk->Release();
    ZeroMemory(&mt, sizeof(mt));
}

static const wchar_t* ShmName();   // defined with the registry helpers at the bottom

static HRESULT CopyMediaType(AM_MEDIA_TYPE* pDst, const AM_MEDIA_TYPE* pSrc)
{
    if (!pDst || !pSrc) return E_POINTER;
    *pDst = *pSrc;
    pDst->pbFormat = nullptr;
    if (pSrc->pUnk) pSrc->pUnk->AddRef();
    if (pSrc->cbFormat)
    {
        pDst->pbFormat = static_cast<BYTE*>(CoTaskMemAlloc(pSrc->cbFormat));
        if (!pDst->pbFormat) { ZeroMemory(pDst, sizeof(*pDst)); return E_OUTOFMEMORY; }
        memcpy(pDst->pbFormat, pSrc->pbFormat, pSrc->cbFormat);
    }
    return S_OK;
}

static int Rgb24Stride(int w) { return (w * 3 + 3) & ~3; }
static int Yuy2Stride(int w) { return (w * 2 + 3) & ~3; }

static bool IsYuy2Subtype(const GUID& sub) { return IsEqualGUID(sub, MEDIASUBTYPE_YUY2) != FALSE; }
static bool IsRgb24Subtype(const GUID& sub) { return IsEqualGUID(sub, MEDIASUBTYPE_RGB24) != FALSE; }
static int FrameStride(const GUID& sub, int w) { return IsYuy2Subtype(sub) ? Yuy2Stride(w) : Rgb24Stride(w); }
static int FrameBytesFor(const GUID& sub, int w, int h) { return FrameStride(sub, w) * h; }

// YUY2 is advertised first: WPFMediaKit drives VMR9 renderless (X8R8G8B8 surfaces) and a
// 16bpp 4:2:2 source is the format every real UVC camera negotiates there. RGB24 stays
// available for the SampleGrabber / DirectShowCameraService path, which asks for it.
static const GUID& PreferredSubtype() { return MEDIASUBTYPE_YUY2; }

static HRESULT CreateVcamMediaType(AM_MEDIA_TYPE** ppmt, const GUID& subtype, int w, int h, int fps)
{
    if (!ppmt) return E_POINTER;
    *ppmt = nullptr;
    if (w <= 0 || h <= 0 || fps <= 0) return E_INVALIDARG;
    if (!IsYuy2Subtype(subtype) && !IsRgb24Subtype(subtype)) return E_INVALIDARG;
    const bool yuy2 = IsYuy2Subtype(subtype);

    AM_MEDIA_TYPE* pmt = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (!pmt) return E_OUTOFMEMORY;
    ZeroMemory(pmt, sizeof(*pmt));
    VIDEOINFOHEADER* vih = static_cast<VIDEOINFOHEADER*>(CoTaskMemAlloc(sizeof(VIDEOINFOHEADER)));
    if (!vih) { CoTaskMemFree(pmt); return E_OUTOFMEMORY; }
    ZeroMemory(vih, sizeof(*vih));

    const int stride = FrameStride(subtype, w);
    vih->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih->bmiHeader.biWidth = w;
    vih->bmiHeader.biHeight = h;              // positive: bottom-up rows (classic DShow)
    vih->bmiHeader.biPlanes = 1;
    vih->bmiHeader.biBitCount = yuy2 ? 16 : 24;
    vih->bmiHeader.biCompression = yuy2 ? MAKEFOURCC('Y', 'U', 'Y', '2') : BI_RGB;
    vih->bmiHeader.biSizeImage = stride * h;
    vih->rcSource.left = 0; vih->rcSource.top = 0; vih->rcSource.right = w; vih->rcSource.bottom = h;
    vih->rcTarget = vih->rcSource;
    vih->dwBitRate = (DWORD)stride * 8u * (DWORD)h * (DWORD)fps;
    vih->AvgTimePerFrame = 10000000LL / fps;

    pmt->majortype = MEDIATYPE_Video;
    pmt->subtype = subtype;
    pmt->bFixedSizeSamples = TRUE;
    pmt->bTemporalCompression = FALSE;
    pmt->lSampleSize = stride * h;
    pmt->formattype = FORMAT_VideoInfo;
    pmt->pUnk = nullptr;
    pmt->cbFormat = sizeof(VIDEOINFOHEADER);
    pmt->pbFormat = reinterpret_cast<BYTE*>(vih);
    *ppmt = pmt;
    return S_OK;
}

// S_OK    -> fully usable type, *psub/*pw/*ph/*pfps are the normalised values
// S_FALSE -> structurally valid type but zero/negative size: keep current format
// failure -> not acceptable at all
static HRESULT ParseVideoType(const AM_MEDIA_TYPE* pmt, GUID* psub, int* pw, int* ph, int* pfps)
{
    if (!pmt) return E_POINTER;
    if (pmt->formattype != FORMAT_VideoInfo) return VFW_E_TYPE_NOT_ACCEPTED;
    if (pmt->majortype != MEDIATYPE_Video) return VFW_E_TYPE_NOT_ACCEPTED;
    if (pmt->cbFormat < sizeof(VIDEOINFOHEADER) || !pmt->pbFormat) return VFW_E_TYPE_NOT_ACCEPTED;
    const bool yuy2 = IsYuy2Subtype(pmt->subtype);
    const bool rgb24 = IsRgb24Subtype(pmt->subtype);
    if (!yuy2 && !rgb24) return VFW_E_TYPE_NOT_ACCEPTED;

    const VIDEOINFOHEADER* vih = reinterpret_cast<const VIDEOINFOHEADER*>(pmt->pbFormat);
    if (yuy2)
    {
        if (vih->bmiHeader.biBitCount != 16 ||
            vih->bmiHeader.biCompression != MAKEFOURCC('Y', 'U', 'Y', '2'))
            return VFW_E_TYPE_NOT_ACCEPTED;
    }
    else
    {
        if (vih->bmiHeader.biBitCount != 24 || vih->bmiHeader.biCompression != BI_RGB)
            return VFW_E_TYPE_NOT_ACCEPTED;
    }
    if (vih->bmiHeader.biHeight < 0) return VFW_E_TYPE_NOT_ACCEPTED;   // top-down not offered

    int fps = VCAM_FPS;
    if (vih->AvgTimePerFrame > 0)
    {
        const LONGLONG f = (10000000LL + vih->AvgTimePerFrame / 2) / vih->AvgTimePerFrame;
        if (f >= 1 && f <= 120) fps = (int)f;
    }
    int w = (int)vih->bmiHeader.biWidth;
    int h = (int)vih->bmiHeader.biHeight;
    if (w <= 0 || h <= 0)
    {
        // WPFMediaKit's VideoCapturePlayer overwrites biWidth/biHeight with
        // DesiredWidth/DesiredHeight, which default to 0 when the host never sets
        // DesiredPixelWidth/Height. Treat it as "keep your default".
        if (psub) *psub = pmt->subtype;
        if (pw) *pw = VCAM_DEFAULT_W;
        if (ph) *ph = VCAM_DEFAULT_H;
        if (pfps) *pfps = fps;
        return S_FALSE;
    }
    if (w < 16 || h < 16 || w > VCAM_MAX_W || h > VCAM_MAX_H)
        return VFW_E_TYPE_NOT_ACCEPTED;
    if (psub) *psub = pmt->subtype;
    if (pw) *pw = w;
    if (ph) *ph = h;
    if (pfps) *pfps = fps;
    return S_OK;
}

static HRESULT SetVcamMediaType(AM_MEDIA_TYPE& dst, const GUID& subtype, int w, int h, int fps)
{
    AM_MEDIA_TYPE* p = nullptr;
    HRESULT hr = CreateVcamMediaType(&p, subtype, w, h, fps);
    if (FAILED(hr)) return hr;
    FreeMediaType(dst);
    hr = CopyMediaType(&dst, p);
    FreeMediaType(*p);
    CoTaskMemFree(p);
    return hr;
}

// ---------- RGB24 (BGR, bottom-up) -> YUY2 (bottom-up, 4:2:2) -------------
static inline BYTE ClampByte(int v) { return (BYTE)(v < 0 ? 0 : (v > 255 ? 255 : v)); }

static void ConvertBgrToYuy2(const BYTE* bgr, int bgrStride, BYTE* yuy2, int yuy2Stride, int w, int h)
{
    for (int y = 0; y < h; ++y)
    {
        const BYTE* s = bgr + static_cast<size_t>(y) * bgrStride;
        BYTE* d = yuy2 + static_cast<size_t>(y) * yuy2Stride;
        int x = 0;
        for (; x + 1 < w; x += 2)
        {
            const BYTE b0 = s[x * 3 + 0], g0 = s[x * 3 + 1], r0 = s[x * 3 + 2];
            const BYTE b1 = s[x * 3 + 3], g1 = s[x * 3 + 4], r1 = s[x * 3 + 5];
            const int y0 = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
            const int y1 = ((66 * r1 + 129 * g1 + 25 * b1 + 128) >> 8) + 16;
            const int b = (b0 + b1) / 2, g = (g0 + g1) / 2, r = (r0 + r1) / 2;
            const int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
            const int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;
            d[(x / 2) * 4 + 0] = ClampByte(y0);
            d[(x / 2) * 4 + 1] = ClampByte(u);
            d[(x / 2) * 4 + 2] = ClampByte(y1);
            d[(x / 2) * 4 + 3] = ClampByte(v);
        }
        if (x < w)
        {
            const BYTE b0 = s[x * 3 + 0], g0 = s[x * 3 + 1], r0 = s[x * 3 + 2];
            const int yy = ((66 * r0 + 129 * g0 + 25 * b0 + 128) >> 8) + 16;
            const int u = ((-38 * r0 - 74 * g0 + 112 * b0 + 128) >> 8) + 128;
            const int v = ((112 * r0 - 94 * g0 - 18 * b0 + 128) >> 8) + 128;
            d[(x / 2) * 4 + 0] = ClampByte(yy);
            d[(x / 2) * 4 + 1] = ClampByte(u);
            d[(x / 2) * 4 + 2] = ClampByte(yy);
            d[(x / 2) * 4 + 3] = ClampByte(v);
        }
    }
}

// ---------------------------------------------------------------------------
// placeholder painter - gray "no signal" frame, pushed continuously.
//
// BGR byte order, positive biHeight (bottom-up), stride bytes. The content is kept
// deliberately boring: a flat gray field with a border and a blinking liveness block,
// so that a test (or a human) can tell "filter placeholder" apart from a real frame.
// ---------------------------------------------------------------------------
class CPlaceholderPainter
{
public:
    void Ensure(int w, int h)
    {
        if (w == m_w && h == m_h && !m_template.empty()) return;
        m_w = w; m_h = h;
        m_template.assign(static_cast<size_t>(w) * 3 * h, 0);
        Fill(0, 0, w, h, 48, 48, 48);                 // flat gray field
        Fill(0, 0, w, 2, 88, 88, 88);                 // border (bottom/top)
        Fill(0, h - 2, w, 2, 88, 88, 88);
        Fill(0, 0, 2, h, 88, 88, 88);                 // border (left/right)
        Fill(w - 2, 0, 2, h, 88, 88, 88);
        const int bw = w / 6;
        if (bw >= 24)
        {
            const int bh = h / 10;
            Fill(w / 2 - bw / 2, h / 2 - bh / 2, bw, bh, 64, 64, 64);
        }
    }

    void Paint(BYTE* dst, int stride, int w, int h, UINT64 frameIndex)
    {
        Ensure(w, h);
        for (int y = 0; y < h; ++y)
            memcpy(dst + static_cast<size_t>(y) * stride,
                   &m_template[static_cast<size_t>(y) * w * 3],
                   static_cast<size_t>(w) * 3);
        // blinking liveness block: ~2 blinks/s at 30 fps. Proves frames keep flowing.
        if (w > 128 && h > 96)
        {
            const bool on = ((frameIndex / 15) & 1) != 0;
            const BYTE v = on ? 200 : 96;
            FillInto(dst, stride, w, h, w - 64, 24, 24, 24, v, v, v);
        }
    }

private:
    void Fill(int x, int y, int cw, int ch, BYTE b, BYTE g, BYTE r)
    {
        if (cw <= 0 || ch <= 0) return;
        for (int yy = y; yy < y + ch; ++yy)
        {
            if (yy < 0 || yy >= m_h) continue;
            BYTE* row = &m_template[static_cast<size_t>(yy) * m_w * 3];
            for (int xx = x; xx < x + cw; ++xx)
            {
                if (xx < 0 || xx >= m_w) continue;
                row[xx * 3 + 0] = b; row[xx * 3 + 1] = g; row[xx * 3 + 2] = r;
            }
        }
    }

    static void FillInto(BYTE* dst, int stride, int w, int h, int x, int y, int cw, int ch,
                         BYTE b, BYTE g, BYTE r)
    {
        if (cw <= 0 || ch <= 0) return;
        for (int yy = y; yy < y + ch; ++yy)
        {
            if (yy < 0 || yy >= h) continue;
            BYTE* row = dst + static_cast<size_t>(yy) * stride;
            for (int xx = x; xx < x + cw; ++xx)
            {
                if (xx < 0 || xx >= w) continue;
                row[xx * 3 + 0] = b; row[xx * 3 + 1] = g; row[xx * 3 + 2] = r;
            }
        }
    }

    int m_w = 0, m_h = 0;
    std::vector<BYTE> m_template;
};

// ---------------------------------------------------------------------------
class CVcamFilter;

enum VcamPinRole
{
    VcamPinCapture = 0,
    VcamPinPreview = 1,
    VcamPinCount   = 2
};

class CVcamPin : public IPin, public IAMStreamConfig, public IKsPropertySet
{
public:
    CVcamPin(CVcamFilter* pFilter, VcamPinRole role, HRESULT* phr);
    ~CVcamPin();

    VcamPinRole Role() const { return m_role; }
    void GetCategory(GUID* out) const
    {
        *out = (m_role == VcamPinCapture) ? PIN_CATEGORY_CAPTURE : PIN_CATEGORY_PREVIEW;
    }
    const wchar_t* RoleName() const { return (m_role == VcamPinCapture) ? L"Capture" : L"Preview"; }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IPin
    STDMETHODIMP Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP Disconnect() override;
    STDMETHODIMP ConnectedTo(IPin** ppPin) override;
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP QueryPinInfo(PIN_INFO* pInfo) override;
    STDMETHODIMP QueryDirection(PIN_DIRECTION* pPinDir) override;
    STDMETHODIMP QueryId(LPWSTR* Id) override;
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes** ppEnum) override;
    STDMETHODIMP QueryInternalConnections(IPin** apPin, ULONG* nPin) override;
    STDMETHODIMP EndOfStream() override;
    STDMETHODIMP BeginFlush() override;
    STDMETHODIMP EndFlush() override;
    STDMETHODIMP NewSegment(REFERENCE_TIME tStart, REFERENCE_TIME tStop, double dRate) override;

    // IAMStreamConfig
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* pmt) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** pmt) override;
    STDMETHODIMP GetNumberOfCapabilities(int* piCount, int* piSize) override;
    STDMETHODIMP GetStreamCaps(int iIndex, AM_MEDIA_TYPE** pmt, BYTE* pSCC) override;

    // IKsPropertySet - lets ICaptureGraphBuilder2::RenderStream(category, ...) find us
    STDMETHODIMP Set(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData,
                     LPVOID pPropData, DWORD cbPropData) override;
    STDMETHODIMP Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData,
                     LPVOID pPropData, DWORD cbPropData, DWORD* pcbReturned) override;
    STDMETHODIMP QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport) override;

    // used by the filter
    void     GetFormatDims(int* w, int* h, int* fps) const { *w = m_w; *h = m_h; *fps = m_fps; }
    void     GetFormatInfo(GUID* sub, int* w, int* h, int* fps) const
    { *sub = m_subtype; *w = m_w; *h = m_h; *fps = m_fps; }
    const GUID& Subtype() const { return m_subtype; }
    IMemAllocator* Allocator() { return m_pAllocator; }
    IMemInputPin*  InputPin()  { return m_pInputPin; }
    bool     IsConnected() const { return m_pConnected != nullptr; }
    bool     HasAllocator() const { return m_pAllocator != nullptr; }
    bool     IsCommitted() const { return m_allocatorCommitted; }
    LONGLONG LastReceiveResult() const { return m_lastReceive; }
    void     SetLastReceive(LONGLONG hr) { m_lastReceive = hr; }
    bool     CommitAllocator();
    void     DecommitAllocator();
    void     DecommitAndReleaseAllocator();

private:
    bool     SwitchToOwnAllocator();

private:
    LONG m_ref;
    CVcamFilter* m_pFilter;
    VcamPinRole m_role;
    IPin* m_pConnected;
    IMemInputPin* m_pInputPin;
    IMemAllocator* m_pAllocator;
    bool m_allocatorCommitted;
    bool m_ownAllocator;        // allocator created by us (not the renderer's)
    bool m_allowOwnFallback;    // may switch to our own allocator if the renderer's refuses
    bool m_triedPropVariants;   // one-shot allocator-property exploration for VMR9
    AM_MEDIA_TYPE m_mt;      // negotiated (or default) media type
    bool m_fmtSet;
    GUID m_subtype;          // MEDIASUBTYPE_YUY2 (default) or MEDIASUBTYPE_RGB24
    int m_w, m_h, m_fps;
    volatile LONGLONG m_lastReceive;
};

// ---------------------------------------------------------------------------
class CVcamFilter : public IBaseFilter, public IAMFilterMiscFlags
{
public:
    CVcamFilter(HRESULT* phr);
    ~CVcamFilter();

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override;
    STDMETHODIMP_(ULONG) AddRef() override;
    STDMETHODIMP_(ULONG) Release() override;

    // IPersist
    STDMETHODIMP GetClassID(CLSID* pClsID) override;
    // IMediaFilter
    STDMETHODIMP Stop() override;
    STDMETHODIMP Pause() override;
    STDMETHODIMP Run(REFERENCE_TIME tStart) override;
    STDMETHODIMP GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State) override;
    STDMETHODIMP SetSyncSource(IReferenceClock* pClock) override;
    STDMETHODIMP GetSyncSource(IReferenceClock** ppClock) override;
    // IBaseFilter
    STDMETHODIMP EnumPins(IEnumPins** ppEnum) override;
    STDMETHODIMP FindPin(LPCWSTR Id, IPin** ppPin) override;
    STDMETHODIMP QueryFilterInfo(FILTER_INFO* pInfo) override;
    STDMETHODIMP JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName) override;
    STDMETHODIMP QueryVendorInfo(LPWSTR* pVendorInfo) override;
    // IAMFilterMiscFlags
    STDMETHODIMP_(ULONG) GetMiscFlags() override { return AM_FILTER_MISC_FLAGS_IS_SOURCE; }

    // internal
    CVcamPin* PinAt(int index) { return (index >= 0 && index < VcamPinCount) ? m_pPins[index] : nullptr; }
    void ResetStream();
    void FillNextFrame(BYTE* dst, int stride, int w, int h, UINT32* frameIndexOut);

private:
    void StreamThread();
    void StartThread();
    LONGLONG StreamTimeNow();
    bool EnsureShm();
    void CloseShm();
    void MarkFrameConsumed();
    bool CopyShmSlot(const VcamShmHeader* hdr, int slot, BYTE* dst, int dstStride, int w, int h);
    bool BuildColMap(uint32_t srcW, int dstW);
    void ShowPlaceholder(BYTE* dst, int stride, int w, int h);
    void EnsureBgrScratch(int w, int h);
    void FillPinFrame(const GUID& subtype, BYTE* dst, int dstStride, int w, int h, UINT32* frameIndexOut);
    void CommitAllPins();
    void DecommitAllPins();

    LONG m_ref;
    CRITICAL_SECTION m_cs;
    std::atomic<int> m_state;
    CVcamPin* m_pPins[VcamPinCount];
    IFilterGraph* m_pGraph;
    WCHAR m_name[128];
    IReferenceClock* m_pClock;
    HANDLE m_hStopEvent;
    std::atomic<bool> m_threadExited;
    std::thread m_thread;
    std::atomic<bool> m_resetTiming;
    std::atomic<bool> m_prerollDone;
    std::atomic<UINT32> m_frameIdx;
    std::atomic<UINT64> m_framesDelivered;
    std::atomic<UINT64> m_framesDropped;
    std::atomic<UINT64> m_framesPlaceholder;
    std::atomic<UINT64> m_framesFromShm;
    LONGLONG m_rtStart;
    LONGLONG m_qpcBase;
    CPlaceholderPainter m_placeholder;
    // RGB24 staging buffer for format conversion (e.g. -> YUY2 for VMR9)
    std::vector<BYTE> m_bgrScratch;
    int m_bgrScratchW;
    int m_bgrScratchH;
    // shared memory frame source
    HANDLE m_hShm;
    VcamShmHeader* m_pShm;      // writable: consumer progress (readSeq) is published here
    bool m_shmWritable;
    size_t m_shmBytes;
    uint32_t m_shmProbeMiss;
    uint64_t m_shmLastFrame;
    LONGLONG m_shmLastChange100ns;
    int m_shmLastSlot;
    bool m_shmHasFrame;
    int m_colMapSrcW;
    int m_colMapW;
    std::vector<uint32_t> m_colMap;
};

// ---------------------------------------------------------------------------
// enumerators
// ===========================================================================
class CEnumPins : public IEnumPins
{
public:
    CEnumPins(CVcamFilter* pFilter, HRESULT* phr) : m_ref(1), m_pos(0), m_pFilter(pFilter)
    {
        m_pFilter->AddRef();
        if (phr) *phr = S_OK;
    }
    ~CEnumPins() { m_pFilter->Release(); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IEnumPins) { *ppv = static_cast<IEnumPins*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }
    STDMETHODIMP Next(ULONG cPins, IPin** ppPins, ULONG* pcFetched) override
    {
        if (!ppPins) return E_POINTER;
        ULONG fetched = 0;
        while (fetched < cPins && m_pos < VcamPinCount)
        {
            IPin* p = m_pFilter->PinAt((int)m_pos);
            if (!p) break;
            p->AddRef();
            ppPins[fetched++] = p;
            m_pos++;
        }
        if (pcFetched) *pcFetched = fetched;
        return (fetched == cPins) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG c) override
    {
        m_pos += (LONG)c;
        if (m_pos > VcamPinCount) m_pos = VcamPinCount + 1;
        return (m_pos <= VcamPinCount) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Reset() override { m_pos = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumPins** ppEnum) override
    {
        if (!ppEnum) return E_POINTER;
        HRESULT hr = S_OK;
        CEnumPins* p = new CEnumPins(m_pFilter, &hr);
        if (!p) return E_OUTOFMEMORY;
        p->m_pos = m_pos;
        *ppEnum = p;
        return S_OK;
    }
private:
    LONG m_ref;
    LONG m_pos;
    CVcamFilter* m_pFilter;
};

// index 0 = YUY2 1920x1080 (VMR9 renderless / WPFMediaKit preference),
// index 1 = YUY2 1280x720, index 2 = RGB24 1920x1080, index 3 = RGB24 1280x720
class CEnumMediaTypes : public IEnumMediaTypes
{
public:
    CEnumMediaTypes() : m_ref(1), m_pos(0) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IEnumMediaTypes) { *ppv = static_cast<IEnumMediaTypes*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }
    STDMETHODIMP Next(ULONG cTypes, AM_MEDIA_TYPE** ppTypes, ULONG* pcFetched) override
    {
        if (!ppTypes) return E_POINTER;
        ULONG fetched = 0;
        while (fetched < cTypes && m_pos < kCount)
        {
            static const int kDims[kCount][2] = { { 1920, 1080 }, { 1280, 720 }, { 1920, 1080 }, { 1280, 720 } };
            const GUID& sub = (m_pos < 2) ? MEDIASUBTYPE_YUY2 : MEDIASUBTYPE_RGB24;
            AM_MEDIA_TYPE* pmt = nullptr;
            if (FAILED(CreateVcamMediaType(&pmt, sub, kDims[m_pos][0], kDims[m_pos][1], VCAM_FPS))) break;
            ppTypes[fetched++] = pmt;
            m_pos++;
        }
        if (pcFetched) *pcFetched = fetched;
        return (fetched == cTypes) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Skip(ULONG c) override
    {
        m_pos += (LONG)c;
        if (m_pos > kCount) m_pos = kCount + 1;
        return (m_pos <= kCount) ? S_OK : S_FALSE;
    }
    STDMETHODIMP Reset() override { m_pos = 0; return S_OK; }
    STDMETHODIMP Clone(IEnumMediaTypes** ppEnum) override
    {
        if (!ppEnum) return E_POINTER;
        CEnumMediaTypes* p = new CEnumMediaTypes();
        if (!p) return E_OUTOFMEMORY;
        p->m_pos = m_pos;
        *ppEnum = p;      // ctor refcount == 1, handed to the caller
        return S_OK;
    }
private:
    static const LONG kCount = 4;
    LONG m_ref;
    LONG m_pos;
};

// ===========================================================================
// CVcamPin
// ===========================================================================
CVcamPin::CVcamPin(CVcamFilter* pFilter, VcamPinRole role, HRESULT* phr)
    : m_ref(1), m_pFilter(pFilter), m_role(role), m_pConnected(nullptr), m_pInputPin(nullptr),
      m_pAllocator(nullptr), m_allocatorCommitted(false), m_ownAllocator(false),
      m_allowOwnFallback(true), m_triedPropVariants(false), m_fmtSet(false), m_subtype(PreferredSubtype()),
      m_w(VCAM_DEFAULT_W), m_h(VCAM_DEFAULT_H), m_fps(VCAM_FPS), m_lastReceive(0)
{
    ZeroMemory(&m_mt, sizeof(m_mt));
    SetVcamMediaType(m_mt, m_subtype, m_w, m_h, m_fps);
    m_fmtSet = true;
    m_pFilter->AddRef();
    if (phr) *phr = S_OK;
}

CVcamPin::~CVcamPin()
{
    Disconnect();
    FreeMediaType(m_mt);
    m_pFilter->Release();
}

STDMETHODIMP CVcamPin::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IPin) { *ppv = static_cast<IPin*>(this); }
    else if (riid == IID_IAMStreamConfig) { *ppv = static_cast<IAMStreamConfig*>(this); }
    else if (riid == IID_IKsPropertySet) { *ppv = static_cast<IKsPropertySet*>(this); }
    else return E_NOINTERFACE;
    AddRef();
    return S_OK;
}
STDMETHODIMP_(ULONG) CVcamPin::AddRef() { return InterlockedIncrement(&m_ref); }
STDMETHODIMP_(ULONG) CVcamPin::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) delete this;
    return (ULONG)r;
}

void CVcamPin::DecommitAndReleaseAllocator()
{
    if (m_pAllocator)
    {
        if (m_allocatorCommitted) { m_pAllocator->Decommit(); m_allocatorCommitted = false; }
        m_pAllocator->Release();
        m_pAllocator = nullptr;
    }
    m_ownAllocator = false;
    m_allowOwnFallback = true;
    m_triedPropVariants = false;
    if (m_pInputPin) { m_pInputPin->Release(); m_pInputPin = nullptr; }
}

bool CVcamPin::CommitAllocator()
{
    if (!m_pAllocator) return false;
    if (m_allocatorCommitted) return true;

    HRESULT hr = m_pAllocator->Commit();
    if (SUCCEEDED(hr))
    {
        m_allocatorCommitted = true;
        VcamLog("pin[%ls] allocator committed (own=%d)", RoleName(), m_ownAllocator ? 1 : 0);
        return true;
    }
    VcamLog("pin[%ls] Commit FAILED hr=0x%08X (own=%d) - continuing anyway, GetBuffer decides",
            RoleName(), (unsigned)hr, m_ownAllocator ? 1 : 0);

    if (!m_ownAllocator && !m_triedPropVariants)
    {
        // VMR9 renderless owns a surface allocator whose allocation info comes from the
        // negotiated media type and these buffer properties. Different builds/drivers
        // accept different combinations, so try the useful ones once and log each result;
        // the first successful Commit wins.
        m_triedPropVariants = true;
        const int frameBytes = Rgb24Stride(m_w) * m_h;   // conservative (24bpp) upper bound
        const int surfaceBytes = m_w * m_h * 4;          // X8R8G8B8 surface upper bound
        struct Variant { int cBuffers; int cbBuffer; };
        const Variant variants[] = {
            { 1, frameBytes },
            { 4, frameBytes },
            { 1, surfaceBytes },
            { 4, surfaceBytes },
            { 1, 0 }
        };
        for (size_t i = 0; i < ARRAYSIZE(variants); ++i)
        {
            ALLOCATOR_PROPERTIES props = { variants[i].cBuffers, variants[i].cbBuffer, 1, 0 };
            ALLOCATOR_PROPERTIES actual = { 0, 0, 0, 0 };
            const HRESULT hs = m_pAllocator->SetProperties(&props, &actual);
            const HRESULT hc = m_pAllocator->Commit();
            VcamLog("pin[%ls] alloc variant %u {%ld,%ld}: set=0x%08X commit=0x%08X actual{%ld,%ld}",
                    RoleName(), (unsigned)i, (long)variants[i].cBuffers, (long)variants[i].cbBuffer,
                    (unsigned)hs, (unsigned)hc, (long)actual.cBuffers, (long)actual.cbBuffer);
            if (SUCCEEDED(hc))
            {
                m_allocatorCommitted = true;
                VcamLog("pin[%ls] allocator committed with variant %u", RoleName(), (unsigned)i);
                return true;
            }
        }
    }
    return false;
}

// Fallback used only when the renderer's allocator cannot be committed at all (e.g. a
// VMR9 renderless surface allocator that refuses upstream configuration). We create a
// plain memory allocator, announce it to the renderer and commit it.
bool CVcamPin::SwitchToOwnAllocator()
{
    if (m_allocatorCommitted) return true;
    if (m_ownAllocator) return false;
    if (m_pAllocator) { m_pAllocator->Release(); m_pAllocator = nullptr; }
    IMemAllocator* pAlloc = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_IMemAllocator, reinterpret_cast<void**>(&pAlloc));
    if (FAILED(hr) || !pAlloc) return false;
    ALLOCATOR_PROPERTIES props = { 4, Rgb24Stride(m_w) * m_h, 1, 0 };
    ALLOCATOR_PROPERTIES actual = { 0, 0, 0, 0 };
    hr = pAlloc->SetProperties(&props, &actual);
    if (FAILED(hr)) { pAlloc->Release(); return false; }
    if (m_pInputPin)
    {
        const HRESULT hn = m_pInputPin->NotifyAllocator(pAlloc, TRUE);
        VcamLog("pin[%ls] NotifyAllocator(own fallback) hr=0x%08X", RoleName(), (unsigned)hn);
    }
    hr = pAlloc->Commit();
    if (FAILED(hr))
    {
        VcamLog("pin[%ls] own allocator Commit FAILED hr=0x%08X", RoleName(), (unsigned)hr);
        pAlloc->Release();
        return false;
    }
    m_pAllocator = pAlloc;
    m_ownAllocator = true;
    m_allocatorCommitted = true;
    VcamLog("pin[%ls] switched to own allocator, committed", RoleName());
    return true;
}

void CVcamPin::DecommitAllocator()
{
    if (m_pAllocator && m_allocatorCommitted)
    {
        m_pAllocator->Decommit();   // unblocks a thread parked in GetBuffer
        m_allocatorCommitted = false;
    }
}

STDMETHODIMP CVcamPin::Connect(IPin* pReceivePin, const AM_MEDIA_TYPE* pmt)
{
    if (!pReceivePin) return E_POINTER;
    if (m_pConnected) return VFW_E_ALREADY_CONNECTED;

    IMemInputPin* pIn = nullptr;
    HRESULT hr = pReceivePin->QueryInterface(IID_IMemInputPin, reinterpret_cast<void**>(&pIn));
    if (FAILED(hr) || !pIn) return VFW_E_TYPE_NOT_ACCEPTED;

    // Candidate order: (1) the caller's requested type if fully valid, then the
    // advertised preference: YUY2 720p, YUY2 1080p, RGB24 720p, RGB24 1080p.
    GUID wantSub = PreferredSubtype();
    int wantW = 0, wantH = 0, wantFps = VCAM_FPS;
    const bool haveRequest = pmt && ParseVideoType(pmt, &wantSub, &wantW, &wantH, &wantFps) == S_OK;

    struct Cand { GUID sub; int w, h; };
    const Cand fallbacks[4] = {
        { MEDIASUBTYPE_YUY2,  VCAM_DEFAULT_W, VCAM_DEFAULT_H },
        { MEDIASUBTYPE_YUY2,  1920, 1080 },
        { MEDIASUBTYPE_RGB24, VCAM_DEFAULT_W, VCAM_DEFAULT_H },
        { MEDIASUBTYPE_RGB24, 1920, 1080 }
    };
    AM_MEDIA_TYPE* chosen = nullptr;
    const int maxTries = haveRequest ? 5 : 4;
    for (int i = 0; i < maxTries && !chosen; ++i)
    {
        GUID csub;
        int cw, ch, cfps;
        if (i == 0 && haveRequest) { csub = wantSub; cw = wantW; ch = wantH; cfps = wantFps; }
        else
        {
            const int fi = haveRequest ? i - 1 : i;
            if (fi < 0 || fi > 3) continue;
            csub = fallbacks[fi].sub; cw = fallbacks[fi].w; ch = fallbacks[fi].h; cfps = VCAM_FPS;
        }
        AM_MEDIA_TYPE* cand = nullptr;
        if (FAILED(CreateVcamMediaType(&cand, csub, cw, ch, cfps))) continue;
        hr = pReceivePin->ReceiveConnection(this, cand);
        if (SUCCEEDED(hr)) chosen = cand;
        else { FreeMediaType(*cand); CoTaskMemFree(cand); }
    }
    if (!chosen) { pIn->Release(); return VFW_E_TYPE_NOT_ACCEPTED; }

    // adopt the negotiated format (our own normalised VIDEOINFOHEADER)
    {
        const VIDEOINFOHEADER* vih = reinterpret_cast<const VIDEOINFOHEADER*>(chosen->pbFormat);
        m_subtype = chosen->subtype;
        m_w = (int)vih->bmiHeader.biWidth;
        m_h = (int)vih->bmiHeader.biHeight;
        m_fps = VCAM_FPS;
        if (vih->AvgTimePerFrame > 0)
        {
            const int f = (int)((10000000LL + vih->AvgTimePerFrame / 2) / vih->AvgTimePerFrame);
            if (f >= 1 && f <= 120) m_fps = f;
        }
        FreeMediaType(m_mt);
        CopyMediaType(&m_mt, chosen);
        m_fmtSet = true;
    }
    FreeMediaType(*chosen);
    CoTaskMemFree(chosen);

    const DWORD allocMode = AllocMode();
    const int frameBytes = FrameBytesFor(m_subtype, m_w, m_h);

    // Allocator selection (live source). Renderers that manage their own buffers - VMR9
    // renderless with a custom IVMRSurfaceAllocator9 is the one WPFMediaKit uses - already
    // have the buffer size fixed by the negotiated media type and reject both
    // SetProperties (D3DERR_INVALIDCALL) and a foreign allocator (NotifyAllocator E_FAIL).
    // So: adopt the renderer's allocator when it is usable as-is, only then try to
    // configure it, and only fall back to our own memory allocator as a last resort.
    IMemAllocator* pAlloc = nullptr;
    bool ownAllocator = false;
    bool rendererUsable = false;
    if (allocMode != 2)
    {
        if (SUCCEEDED(pIn->GetAllocator(&pAlloc)) && pAlloc)
        {
            rendererUsable = true;    // adopted as-is; CommitAllocator negotiates later
            ALLOCATOR_PROPERTIES actual = { 0, 0, 0, 0 };
            const HRESULT hp = pAlloc->GetProperties(&actual);
            VcamLog("pin[%ls] adopting renderer allocator: cBuffers=%ld cbBuffer=%ld cbAlign=%ld hr=0x%08X (need cbBuffer>=%d)",
                    RoleName(), (long)actual.cBuffers, (long)actual.cbBuffer, (long)actual.cbAlign,
                    (unsigned)hp, frameBytes);
            // Announce it as ours: some renderer pins only commit the allocator after this.
            const HRESULT hn = pIn->NotifyAllocator(pAlloc, TRUE);
            VcamLog("pin[%ls] NotifyAllocator(renderer, TRUE) hr=0x%08X", RoleName(), (unsigned)hn);
        }
    }
    if (!pAlloc)
    {
        hr = CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
                              IID_IMemAllocator, reinterpret_cast<void**>(&pAlloc));
        if (FAILED(hr) || !pAlloc) { pIn->Release(); return VFW_E_NO_ALLOCATOR; }
        ownAllocator = true;
        ALLOCATOR_PROPERTIES props = { 4, frameBytes, 1, 0 };
        ALLOCATOR_PROPERTIES actual = { 0, 0, 0, 0 };
        hr = pAlloc->SetProperties(&props, &actual);
        if (FAILED(hr))
        {
            VcamLog("pin[%ls] own allocator SetProperties failed hr=0x%08X", RoleName(), (unsigned)hr);
            pAlloc->Release(); pIn->Release(); return hr;
        }
        // Only a self-created allocator must be announced to the renderer.
        const HRESULT hn = pIn->NotifyAllocator(pAlloc, TRUE);
        VcamLog("pin[%ls] NotifyAllocator(own) hr=0x%08X", RoleName(), (unsigned)hn);
    }
    m_ownAllocator = ownAllocator;
    // No swapping to a foreign allocator: VMR9 renderless rejects it (NotifyAllocator
    // E_FAIL). If the renderer's allocator is the only one, use it and let GetBuffer decide.
    m_allowOwnFallback = false;
    VcamLog("pin[%ls] Connect negotiated %dx%d@%d allocator=%s mode=%lu", RoleName(),
            m_w, m_h, m_fps, ownAllocator ? "own" : "renderer", (unsigned long)allocMode);

    m_pConnected = pReceivePin;
    m_pConnected->AddRef();
    m_pInputPin = pIn;
    m_pAllocator = pAlloc;
    m_allocatorCommitted = false;
    return S_OK;
}

STDMETHODIMP CVcamPin::ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt)
{
    // We are an output pin: nobody else may push data into us.
    UNREFERENCED_PARAMETER(pConnector);
    UNREFERENCED_PARAMETER(pmt);
    return E_UNEXPECTED;
}

STDMETHODIMP CVcamPin::Disconnect()
{
    DecommitAndReleaseAllocator();
    if (m_pConnected) { m_pConnected->Release(); m_pConnected = nullptr; }
    return S_OK;
}

STDMETHODIMP CVcamPin::ConnectedTo(IPin** ppPin)
{
    if (!ppPin) return E_POINTER;
    *ppPin = m_pConnected;
    if (m_pConnected) m_pConnected->AddRef();
    return m_pConnected ? S_OK : VFW_E_NOT_CONNECTED;
}

STDMETHODIMP CVcamPin::ConnectionMediaType(AM_MEDIA_TYPE* pmt)
{
    if (!pmt) return E_POINTER;
    if (!m_pConnected) return VFW_E_NOT_CONNECTED;
    return CopyMediaType(pmt, &m_mt);
}

STDMETHODIMP CVcamPin::QueryPinInfo(PIN_INFO* pInfo)
{
    if (!pInfo) return E_POINTER;
    pInfo->pFilter = static_cast<IBaseFilter*>(m_pFilter);
    pInfo->pFilter->AddRef();
    pInfo->dir = PINDIR_OUTPUT;
    wcscpy_s(pInfo->achName, RoleName());
    return S_OK;
}

STDMETHODIMP CVcamPin::QueryDirection(PIN_DIRECTION* pPinDir)
{
    if (!pPinDir) return E_POINTER;
    *pPinDir = PINDIR_OUTPUT;
    return S_OK;
}

STDMETHODIMP CVcamPin::QueryId(LPWSTR* Id)
{
    if (!Id) return E_POINTER;
    const wchar_t* name = RoleName();
    const size_t cb = (wcslen(name) + 1) * sizeof(wchar_t);
    LPWSTR p = static_cast<LPWSTR>(CoTaskMemAlloc(cb));
    if (!p) return E_OUTOFMEMORY;
    memcpy(p, name, cb);
    *Id = p;
    return S_OK;
}

STDMETHODIMP CVcamPin::QueryAccept(const AM_MEDIA_TYPE* pmt)
{
    GUID sub;
    int w, h, fps;
    return (S_OK == ParseVideoType(pmt, &sub, &w, &h, &fps)) ? S_OK : S_FALSE;
}

STDMETHODIMP CVcamPin::EnumMediaTypes(IEnumMediaTypes** ppEnum)
{
    if (!ppEnum) return E_POINTER;
    CEnumMediaTypes* p = new CEnumMediaTypes();
    if (!p) return E_OUTOFMEMORY;
    *ppEnum = p;   // ctor refcount == 1, handed to caller
    return S_OK;
}

STDMETHODIMP CVcamPin::QueryInternalConnections(IPin** apPin, ULONG* nPin)
{
    UNREFERENCED_PARAMETER(apPin);
    if (nPin) *nPin = 0;
    return E_NOTIMPL;
}

// IKsPropertySet: the pin category. CaptureGraphBuilder2/RenderStream look this up.
STDMETHODIMP CVcamPin::Set(REFGUID, DWORD, LPVOID, DWORD, LPVOID, DWORD)
{
    return E_NOTIMPL;
}

STDMETHODIMP CVcamPin::Get(REFGUID guidPropSet, DWORD dwPropID, LPVOID pInstanceData, DWORD cbInstanceData,
                           LPVOID pPropData, DWORD cbPropData, DWORD* pcbReturned)
{
    UNREFERENCED_PARAMETER(pInstanceData);
    UNREFERENCED_PARAMETER(cbInstanceData);
    if (guidPropSet != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    const DWORD need = sizeof(GUID);
    if (pcbReturned) *pcbReturned = need;
    if (!pPropData) return S_OK;                       // size query
    if (cbPropData < need) return E_UNEXPECTED;
    GUID value = { 0 };
    GetCategory(&value);
    memcpy(pPropData, &value, need);
    return S_OK;
}

STDMETHODIMP CVcamPin::QuerySupported(REFGUID guidPropSet, DWORD dwPropID, DWORD* pTypeSupport)
{
    if (pTypeSupport) *pTypeSupport = KSPROPERTY_SUPPORT_GET;
    if (guidPropSet != AMPROPSETID_Pin) return E_PROP_SET_UNSUPPORTED;
    if (dwPropID != AMPROPERTY_PIN_CATEGORY) return E_PROP_ID_UNSUPPORTED;
    return S_OK;
}
STDMETHODIMP CVcamPin::EndOfStream() { return S_OK; }
STDMETHODIMP CVcamPin::BeginFlush() { return S_OK; }
STDMETHODIMP CVcamPin::EndFlush() { return S_OK; }
STDMETHODIMP CVcamPin::NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) { return S_OK; }

STDMETHODIMP CVcamPin::SetFormat(AM_MEDIA_TYPE* pmt)
{
    GUID sub = m_subtype;
    int w = 0, h = 0, fps = VCAM_FPS;
    HRESULT hr = ParseVideoType(pmt, &sub, &w, &h, &fps);
    if (FAILED(hr)) return hr;

    if (m_pConnected)
    {
        // Same format on a connected pin is a harmless no-op, a real change is refused
        // (the graph must reconnect; this is what capture sources do).
        if (hr == S_OK && IsEqualGUID(sub, m_subtype) && w == m_w && h == m_h && fps == m_fps) return S_OK;
        return VFW_E_INVALIDMEDIATYPE;
    }

    if (hr == S_FALSE)
    {
        // zero-size request (WPFMediaKit with unset DesiredPixelWidth/Height):
        // keep the current default format but honour a requested frame rate.
        if (!m_fmtSet) return VFW_E_INVALIDMEDIATYPE;
        if (fps != m_fps)
        {
            if (FAILED(SetVcamMediaType(m_mt, m_subtype, m_w, m_h, fps))) return E_OUTOFMEMORY;
            m_fps = fps;
        }
        return S_OK;
    }

    hr = SetVcamMediaType(m_mt, sub, w, h, fps);   // normalise to our own VIDEOINFOHEADER
    if (FAILED(hr)) return hr;
    m_subtype = sub;
    m_w = w; m_h = h; m_fps = fps; m_fmtSet = true;
    return S_OK;
}

STDMETHODIMP CVcamPin::GetFormat(AM_MEDIA_TYPE** ppmt)
{
    if (!ppmt) return E_POINTER;
    *ppmt = nullptr;
    if (!m_fmtSet) return VFW_E_NOT_CONNECTED;
    AM_MEDIA_TYPE* p = static_cast<AM_MEDIA_TYPE*>(CoTaskMemAlloc(sizeof(AM_MEDIA_TYPE)));
    if (!p) return E_OUTOFMEMORY;
    HRESULT hr = CopyMediaType(p, &m_mt);
    if (FAILED(hr)) { CoTaskMemFree(p); return hr; }
    *ppmt = p;
    return S_OK;
}

STDMETHODIMP CVcamPin::GetNumberOfCapabilities(int* piCount, int* piSize)
{
    if (!piCount || !piSize) return E_POINTER;
    *piCount = 4;
    *piSize = sizeof(VIDEO_STREAM_CONFIG_CAPS);
    return S_OK;
}

STDMETHODIMP CVcamPin::GetStreamCaps(int iIndex, AM_MEDIA_TYPE** ppmt, BYTE* pSCC)
{
    if (!ppmt) return E_POINTER;
    *ppmt = nullptr;
    if (iIndex < 0 || iIndex > 3) return E_INVALIDARG;
    static const int kDims[4][2] = { { 1920, 1080 }, { 1280, 720 }, { 1920, 1080 }, { 1280, 720 } };
    const GUID& sub = (iIndex < 2) ? MEDIASUBTYPE_YUY2 : MEDIASUBTYPE_RGB24;
    const int w = kDims[iIndex][0];
    const int h = kDims[iIndex][1];
    HRESULT hr = CreateVcamMediaType(ppmt, sub, w, h, VCAM_FPS);
    if (FAILED(hr)) return hr;
    if (!pSCC) return S_OK;      // caller may only want the media type
    VIDEO_STREAM_CONFIG_CAPS* pCaps = reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(pSCC);
    ZeroMemory(pCaps, sizeof(*pCaps));
    pCaps->guid = FORMAT_VideoInfo;
    pCaps->VideoStandard = 0;
    pCaps->InputSize.cx = w;  pCaps->InputSize.cy = h;
    pCaps->MinCroppingSize.cx = w; pCaps->MinCroppingSize.cy = h;
    pCaps->MaxCroppingSize.cx = w; pCaps->MaxCroppingSize.cy = h;
    pCaps->CropGranularityX = 1; pCaps->CropGranularityY = 1;
    pCaps->CropAlignX = 0; pCaps->CropAlignY = 0;
    pCaps->MinOutputSize.cx = w; pCaps->MinOutputSize.cy = h;
    pCaps->MaxOutputSize.cx = w; pCaps->MaxOutputSize.cy = h;
    pCaps->OutputGranularityX = 1; pCaps->OutputGranularityY = 1;
    pCaps->StretchTapsX = 0; pCaps->StretchTapsY = 0;
    pCaps->ShrinkTapsX = 0; pCaps->ShrinkTapsY = 0;
    pCaps->MinFrameInterval = 10000000LL / VCAM_FPS;
    pCaps->MaxFrameInterval = 10000000LL / VCAM_FPS;
    pCaps->MinBitsPerSecond = FrameStride(sub, w) * 8 * h * VCAM_FPS;
    pCaps->MaxBitsPerSecond = pCaps->MinBitsPerSecond;
    return S_OK;
}

// ===========================================================================
// CVcamFilter
// ===========================================================================
CVcamFilter::CVcamFilter(HRESULT* phr)
    : m_ref(1), m_state(State_Stopped), m_pGraph(nullptr),
      m_pClock(nullptr), m_hStopEvent(nullptr), m_threadExited(false),
      m_resetTiming(false), m_prerollDone(false),
      m_frameIdx(0), m_framesDelivered(0), m_framesDropped(0),
      m_framesPlaceholder(0), m_framesFromShm(0),
      m_rtStart(0), m_qpcBase(0), m_bgrScratchW(0), m_bgrScratchH(0),
      m_hShm(nullptr), m_pShm(nullptr), m_shmWritable(false),
      m_shmBytes(0), m_shmProbeMiss(0), m_shmLastFrame(0), m_shmLastChange100ns(0),
      m_shmLastSlot(-1), m_shmHasFrame(false), m_colMapSrcW(0), m_colMapW(0)
{
    for (int i = 0; i < VcamPinCount; ++i) m_pPins[i] = nullptr;
    InitializeCriticalSection(&m_cs);
    m_name[0] = 0;
    wcscpy_s(m_name, FILTER_FRIENDLY_NAME);
    m_hStopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    HRESULT hrCapture = S_OK;
    m_pPins[VcamPinCapture] = new CVcamPin(this, VcamPinCapture, &hrCapture);
    if (!m_pPins[VcamPinCapture] || FAILED(hrCapture))
    {
        if (phr) *phr = E_OUTOFMEMORY;
        return;
    }
    HRESULT hrPreview = S_OK;
    m_pPins[VcamPinPreview] = new CVcamPin(this, VcamPinPreview, &hrPreview);
    if (!m_pPins[VcamPinPreview] || FAILED(hrPreview))
    {
        if (phr) *phr = E_OUTOFMEMORY;
        return;
    }
    if (phr) *phr = S_OK;
}

CVcamFilter::~CVcamFilter()
{
    Stop();
    for (int i = 0; i < VcamPinCount; ++i)
    {
        if (m_pPins[i]) m_pPins[i]->Release();
        m_pPins[i] = nullptr;
    }
    if (m_pClock) m_pClock->Release();
    if (m_pGraph) m_pGraph->Release();
    CloseShm();
    if (m_hStopEvent) CloseHandle(m_hStopEvent);
    DeleteCriticalSection(&m_cs);
}

STDMETHODIMP CVcamFilter::QueryInterface(REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    *ppv = nullptr;
    if (riid == IID_IUnknown || riid == IID_IBaseFilter || riid == IID_IMediaFilter || riid == IID_IPersist)
        *ppv = static_cast<IBaseFilter*>(this);
    else if (riid == IID_IAMFilterMiscFlags)
        *ppv = static_cast<IAMFilterMiscFlags*>(this);
    else
        return E_NOINTERFACE;
    AddRef();
    return S_OK;
}
STDMETHODIMP_(ULONG) CVcamFilter::AddRef() { return InterlockedIncrement(&m_ref); }
STDMETHODIMP_(ULONG) CVcamFilter::Release()
{
    LONG r = InterlockedDecrement(&m_ref);
    if (r == 0) delete this;
    return (ULONG)r;
}

STDMETHODIMP CVcamFilter::GetClassID(CLSID* pClsID)
{
    if (!pClsID) return E_POINTER;
    *pClsID = CLSID_YoCameraVirtualCam;
    return S_OK;
}

void CVcamFilter::CommitAllPins()
{
    for (int i = 0; i < VcamPinCount; ++i)
        if (m_pPins[i]) m_pPins[i]->CommitAllocator();
}

void CVcamFilter::DecommitAllPins()
{
    for (int i = 0; i < VcamPinCount; ++i)
        if (m_pPins[i]) m_pPins[i]->DecommitAllocator();
}

void CVcamFilter::ResetStream()
{
    m_frameIdx = 0;
    m_framesDelivered = 0;
    m_framesDropped = 0;
    m_framesPlaceholder = 0;
    m_framesFromShm = 0;
}

LONGLONG CVcamFilter::StreamTimeNow()
{
    EnterCriticalSection(&m_cs);
    IReferenceClock* pClock = m_pClock;
    if (pClock) pClock->AddRef();
    const LONGLONG base = m_qpcBase;
    LeaveCriticalSection(&m_cs);
    if (pClock)
    {
        REFERENCE_TIME t = 0;
        const HRESULT hr = pClock->GetTime(&t);
        pClock->Release();
        if (SUCCEEDED(hr)) return (LONGLONG)t;
    }
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (LONGLONG)((c.QuadPart - base) * 10000000LL / (f.QuadPart ? f.QuadPart : 1));
}

// ---- shared memory frame source -------------------------------------------

bool CVcamFilter::BuildColMap(uint32_t srcW, int dstW)
{
    if ((int)srcW == m_colMapSrcW && dstW == m_colMapW && !m_colMap.empty()) return true;
    if (dstW <= 0 || dstW > 8192) return false;
    m_colMap.resize((size_t)dstW);
    for (int x = 0; x < dstW; ++x)
        m_colMap[(size_t)x] = (uint32_t)(((uint64_t)(uint32_t)x * srcW) / (uint64_t)(uint32_t)dstW);
    m_colMapSrcW = (int)srcW;
    m_colMapW = dstW;
    return true;
}

bool CVcamFilter::CopyShmSlot(const VcamShmHeader* hdr, int slot, BYTE* dst, int dstStride, int w, int h)
{
    const uint32_t sw = hdr->width, sh = hdr->height, ss = hdr->stride;
    if (hdr->format != VCAM_FMT_RGB24) return false;
    if (sw < 8 || sh < 8 || sw > 4096 || sh > 2160) return false;
    if (ss < sw * 3) return false;
    if (slot < 0 || (uint32_t)slot >= hdr->slotCount) return false;
    if ((uint64_t)ss * sh > hdr->slotBytes) return false;
    const BYTE* src = reinterpret_cast<const BYTE*>(hdr) + VCAM_SHM_HEADER_BYTES +
                      static_cast<size_t>(slot) * hdr->slotBytes;

    // The producer writes RGB24/BGR **top-down** (docs/CONTRACTS.md section 1: row 0 is
    // the top row), while the DirectShow sample buffer we advertise has a positive
    // biHeight, i.e. it is bottom-up. Copy row-reversed.
    if ((int)sw == w && (int)sh == h)
    {
        for (int y = 0; y < h; ++y)
            memcpy(dst + static_cast<size_t>(h - 1 - y) * dstStride,
                   src + static_cast<size_t>(y) * ss,
                   static_cast<size_t>(w) * 3);
        return true;
    }
    // producer resolution differs from the negotiated one: nearest-neighbour scale
    if (!BuildColMap(sw, w)) return false;
    for (int y = 0; y < h; ++y)
    {
        const uint32_t sy = (uint32_t)(((uint64_t)(uint32_t)y * sh) / (uint64_t)(uint32_t)h);
        const BYTE* srow = src + static_cast<size_t>(sy) * ss;
        BYTE* drow = dst + static_cast<size_t>(h - 1 - y) * dstStride;
        const uint32_t* cm = m_colMap.data();
        for (int x = 0; x < w; ++x)
        {
            const BYTE* p = srow + static_cast<size_t>(cm[x]) * 3;
            drow[x * 3 + 0] = p[0];
            drow[x * 3 + 1] = p[1];
            drow[x * 3 + 2] = p[2];
        }
    }
    return true;
}

bool CVcamFilter::EnsureShm()
{
    if (m_pShm) return true;
    // Probe ~2x/second at 30 fps; a producer may start long after the graph was built.
    if ((m_shmProbeMiss++ % 15) != 0) return false;

    // Write access first: the consumer publishes its progress in header.readSeq, which
    // is how the regression harness proves the filter is actually consuming frames.
    bool writable = true;
    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE, ShmName());
    if (!h) { writable = false; h = OpenFileMappingW(FILE_MAP_READ, FALSE, ShmName()); }
    if (!h)
    {
        static int openFails = 0;
        if (openFails++ < 3) VcamLog("SHM OpenFileMapping failed err=%lu (attempt %d)", GetLastError(), openFails);
        return false;
    }
    void* p = MapViewOfFile(h, writable ? (FILE_MAP_WRITE | FILE_MAP_READ) : FILE_MAP_READ, 0, 0, 0);
    if (!p) { VcamLog("SHM MapViewOfFile failed err=%lu", GetLastError()); CloseHandle(h); return false; }
    VcamShmHeader* hdr = static_cast<VcamShmHeader*>(p);
    if (hdr->magic != VCAM_SHM_MAGIC || hdr->version != VCAM_SHM_VERSION ||
        hdr->headerBytes != VCAM_SHM_HEADER_BYTES || hdr->slotCount == 0 ||
        hdr->slotCount > 8 || hdr->slotBytes == 0)
    {
        UnmapViewOfFile(p);
        CloseHandle(h);
        return false;
    }
    m_hShm = h;
    m_pShm = hdr;
    m_shmWritable = writable;
    m_shmBytes = VCAM_SHM_HEADER_BYTES + static_cast<size_t>(hdr->slotCount) * hdr->slotBytes;
    m_shmHasFrame = false;
    m_shmLastSlot = -1;
    m_shmLastFrame = 0;
    m_shmLastChange100ns = QpcNow100ns();
    VcamLog("SHM opened %ux%u stride=%u slots=%u writable=%d",
            hdr->width, hdr->height, hdr->stride, hdr->slotCount, writable ? 1 : 0);
    return true;
}

void CVcamFilter::MarkFrameConsumed()
{
    if (m_pShm && m_shmWritable)
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_pShm->readSeq));
}

void CVcamFilter::CloseShm()
{
    if (m_pShm) VcamLog("SHM closed");
    if (m_pShm) { UnmapViewOfFile(m_pShm); m_pShm = nullptr; }
    if (m_hShm) { CloseHandle(m_hShm); m_hShm = nullptr; }
    m_shmWritable = false;
    m_shmBytes = 0;
    m_shmHasFrame = false;
    m_shmLastSlot = -1;
    m_shmProbeMiss = 0;      // try to reopen on the very next sample
}

void CVcamFilter::ShowPlaceholder(BYTE* dst, int stride, int w, int h)
{
    m_placeholder.Paint(dst, stride, w, h, m_frameIdx.load());
    m_framesPlaceholder++;
}

void CVcamFilter::EnsureBgrScratch(int w, int h)
{
    const size_t need = static_cast<size_t>(Rgb24Stride(w)) * static_cast<size_t>(h);
    if (w == m_bgrScratchW && h == m_bgrScratchH && m_bgrScratch.size() >= need) return;
    m_bgrScratch.assign(need, 0);
    m_bgrScratchW = w;
    m_bgrScratchH = h;
}

// Produce one frame in the pin's negotiated format. The SHM/placeholder source is always
// RGB24 (BGR); YUY2 is converted on the fly so both the VMR9 Preview path (YUY2) and the
// SampleGrabber Capture path (RGB24) share exactly the same frame source.
void CVcamFilter::FillPinFrame(const GUID& subtype, BYTE* dst, int dstStride, int w, int h, UINT32* frameIndexOut)
{
    if (IsYuy2Subtype(subtype))
    {
        EnsureBgrScratch(w, h);
        const int bgrStride = Rgb24Stride(w);
        FillNextFrame(m_bgrScratch.data(), bgrStride, w, h, frameIndexOut);
        ConvertBgrToYuy2(m_bgrScratch.data(), bgrStride, dst, dstStride, w, h);
    }
    else
    {
        FillNextFrame(dst, dstStride, w, h, frameIndexOut);
    }
}

void CVcamFilter::FillNextFrame(BYTE* dst, int stride, int w, int h, UINT32* frameIndexOut)
{
    const LONGLONG now = QpcNow100ns();
    if (EnsureShm() && m_pShm)
    {
        const VcamShmHeader* hdr = m_pShm;
        const int32_t seq1 = hdr->writeSeq;

        // 1) newest published slot
        if (seq1 > 0 && (seq1 & 1) == 0)
        {
            const int slot = (int)(((uint32_t)(seq1 / 2 - 1)) % hdr->slotCount);
            if (CopyShmSlot(hdr, slot, dst, stride, w, h) && hdr->writeSeq == seq1)
            {
                const uint64_t fi = hdr->frameIndex;
                if (!m_shmHasFrame || fi != m_shmLastFrame)
                    m_shmLastChange100ns = now;
                m_shmLastFrame = fi;
                m_shmLastSlot = slot;
                m_shmHasFrame = true;
                m_framesFromShm++;
                *frameIndexOut = (UINT32)fi;
                return;
            }
        }

        const bool producerAlive = (hdr->flags & 1u) != 0;
        const bool stale = !producerAlive ||
                           (m_shmLastChange100ns != 0 && (now - m_shmLastChange100ns) > 2000000LL);
        if (!stale)
        {
            // 2) replay the last good slot (ring wraps after 4 frames, so still valid)
            if (m_shmHasFrame && m_shmLastSlot >= 0 &&
                CopyShmSlot(hdr, m_shmLastSlot, dst, stride, w, h))
            {
                m_framesFromShm++;
                *frameIndexOut = (UINT32)m_shmLastFrame;
                return;
            }
            // 3) no good slot yet (start-up torn read): use the previous published slot
            if (seq1 > 2)
            {
                const int prev = (int)(((uint32_t)(seq1 / 2 - 2)) % hdr->slotCount);
                if (CopyShmSlot(hdr, prev, dst, stride, w, h))
                {
                    m_shmHasFrame = true;
                    m_shmLastSlot = prev;
                    m_shmLastFrame = hdr->frameIndex;
                    m_shmLastChange100ns = now;
                    m_framesFromShm++;
                    *frameIndexOut = (UINT32)m_shmLastFrame;
                    return;
                }
            }
        }
        else
        {
            // Producer gone (or stopped publishing). Drop the mapping so a restarted
            // producer is picked up again; until then the placeholder keeps flowing.
            CloseShm();
        }
    }
    ShowPlaceholder(dst, stride, w, h);
    *frameIndexOut = m_frameIdx.load();
}

// ---- streaming thread -----------------------------------------------------

void CVcamFilter::StreamThread()
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    VcamLog("StreamThread started");
    LONGLONG next = 0;
    LONGLONG startBase = 0;
    bool firstSample = true;
    unsigned pumpIter = 0;
    unsigned getBufFail = 0;
    unsigned recvFail = 0;
    unsigned long long gbNotCommitted = 0, gbNotConnected = 0, gbOther = 0;
    unsigned long long recvOther = 0;
    unsigned prerollAttempts = 0;
    HRESULT lastGetBufHr = S_OK;
    LONG lastSampleSize = 0;
    while (WaitForSingleObject(m_hStopEvent, 0) == WAIT_TIMEOUT)
    {
        const int st = m_state.load();
        static int loggedState = -1;
        if (st != loggedState) { loggedState = st; VcamLog("StreamThread state -> %d", st); }
        if ((++pumpIter % 30) == 0)
        {
            CVcamPin* pa = m_pPins[0];
            CVcamPin* pb = m_pPins[1];
            VcamLog("pump #%u state=%d cap(alloc=%d comm=%d last=0x%08X) prev(alloc=%d comm=%d last=0x%08X) delivered=%llu shm=%llu placeholder=%llu getbuf(nc=%llu ncon=%llu oth=%llu) lastGet=0x%08X lastSize=%ld recv(oth=%llu)",
                    pumpIter, st,
                    pa ? pa->HasAllocator() : -1, pa ? pa->IsCommitted() : -1,
                    pa ? (unsigned)pa->LastReceiveResult() : 0,
                    pb ? pb->HasAllocator() : -1, pb ? pb->IsCommitted() : -1,
                    pb ? (unsigned)pb->LastReceiveResult() : 0,
                    (unsigned long long)m_framesDelivered.load(),
                    (unsigned long long)m_framesFromShm.load(),
                    (unsigned long long)m_framesPlaceholder.load(),
                    gbNotCommitted, gbNotConnected, gbOther, recvOther,
                    (unsigned)lastGetBufHr, (long)lastSampleSize);
        }
        if (st == State_Stopped) { Sleep(2); continue; }

        const bool paused = (st == State_Paused);
        if (paused && m_prerollDone.load()) { Sleep(2); continue; }

        if (m_resetTiming.exchange(false))
        {
            startBase = 0;
            next = 0;
            firstSample = true;
        }

        // scheduling clock: capture pin's fps when connected, else preview's
        int fps = VCAM_FPS;
        {
            int w = 0, h = 0, f = 0;
            CVcamPin* clockPin = (m_pPins[VcamPinCapture] && m_pPins[VcamPinCapture]->InputPin())
                                 ? m_pPins[VcamPinCapture] : m_pPins[VcamPinPreview];
            if (clockPin) { clockPin->GetFormatDims(&w, &h, &f); if (f >= 1) fps = f; }
        }
        const LONGLONG interval = 10000000LL / fps;

        const LONGLONG now = StreamTimeNow();
        if (startBase == 0) startBase = now;
        if (next == 0) next = startBase;
        if (!paused)
        {
            if (now < next)
            {
                DWORD ms = (DWORD)((next - now) / 10000);
                if (ms == 0) ms = 1;
                if (ms > 10) ms = 10;
                Sleep(ms);
                continue;
            }
            if (now > next + interval * 10) next = now;   // resync after a long stall
        }

        // deliver one frame to every connected pin (one shared frame source, one clock)
        bool deliveredAny = false;
        HRESULT lastReceiveHr = E_FAIL;
        for (int i = 0; i < VcamPinCount; ++i)
        {
            static int loggedNoAlloc = 0;
            static int loggedGetBufFail = 0;
            static int loggedShortBuf = 0;
            static bool loggedFirstRecv[VcamPinCount] = { false, false };
            static int loggedRecvErr = 0;

            CVcamPin* pin = m_pPins[i];
            if (!pin) continue;
            IMemAllocator* pAlloc = pin->Allocator();
            IMemInputPin*  pIn = pin->InputPin();
            if (!pAlloc || !pIn)
            {
                if ((loggedNoAlloc++ % 300) == 0)
                    VcamLog("pin[%d] has no allocator/input pin yet (alloc=%p in=%p)", i, (void*)pAlloc, (void*)pIn);
                continue;
            }

            GUID sub = MEDIASUBTYPE_RGB24;
            int w = 0, h = 0, pfps = VCAM_FPS;
            pin->GetFormatInfo(&sub, &w, &h, &pfps);
            if (w < 16 || h < 16) continue;
            const int stride = FrameStride(sub, w);
            const int frameBytes = stride * h;

            IMediaSample* pSample = nullptr;
            HRESULT hr = pAlloc->GetBuffer(&pSample, nullptr, nullptr, 0);
            lastGetBufHr = hr;
            if (FAILED(hr) || !pSample)
            {
                if (hr == VFW_E_NOT_COMMITTED)
                {
                    gbNotCommitted++;
                    if ((gbNotCommitted % 30) == 1) pin->CommitAllocator();
                }
                else if (hr == VFW_E_NOT_CONNECTED) gbNotConnected++;
                else gbOther++;
                if ((++getBufFail % 30) == 1)
                    VcamLog("pin[%d] GetBuffer FAILED hr=0x%08X (occurrence %u)", i, (unsigned)hr, getBufFail);
                Sleep(1);
                continue;
            }
            BYTE* pData = nullptr;
            if (FAILED(pSample->GetPointer(&pData)) || !pData)
            {
                pSample->Release();
                continue;
            }
            const LONG sampleSize = pSample->GetSize();
            lastSampleSize = sampleSize;
            if (sampleSize < frameBytes)
            {
                // never deliver a truncated frame
                if ((loggedShortBuf++ % 30) == 1)
                    VcamLog("pin[%d] sample too small: %ld < %d (renderer allocator unusable)", i, (long)sampleSize, frameBytes);
                pSample->Release();
                Sleep(1);
                continue;
            }

            UINT32 frameIdx = 0;
            FillPinFrame(sub, pData, stride, w, h, &frameIdx);
            pSample->SetActualDataLength(frameBytes);
            pSample->SetSyncPoint(TRUE);
            pSample->SetPreroll(paused ? TRUE : FALSE);
            pSample->SetMediaTime(nullptr, nullptr);
            pSample->SetDiscontinuity((firstSample && !paused) ? TRUE : FALSE);
            if (paused)
            {
                // Preroll sample: lets a VMR9 renderless sink set itself up before Run(),
                // which is what makes first paint (and MediaOpened) fast.
                REFERENCE_TIME zero = 0;
                pSample->SetTime(&zero, &zero);
            }
            else
            {
                REFERENCE_TIME tStart = (REFERENCE_TIME)(next - startBase + m_rtStart);
                REFERENCE_TIME tStop = (REFERENCE_TIME)(tStart + 10000000LL / pfps);
                pSample->SetTime(&tStart, &tStop);
            }

            hr = pIn->Receive(pSample);
            lastReceiveHr = hr;
            pSample->Release();
            if (!loggedFirstRecv[i])
            {
                loggedFirstRecv[i] = true;
                VcamLog("pin[%d] first Receive -> hr=0x%08X (%dx%d@%d preroll=%d)",
                        i, (unsigned)hr, w, h, pfps, paused ? 1 : 0);
            }
            if (hr == S_OK) { m_framesDelivered++; deliveredAny = true; if (!paused) MarkFrameConsumed(); }
            else if (hr == S_FALSE) m_framesDropped++;
            else
            {
                recvOther++;
                if ((loggedRecvErr++ % 60) == 0)
                    VcamLog("pin[%d] Receive returned hr=0x%08X", i, (unsigned)hr);
            }
            // VFW_E_NOT_CONNECTED and friends: keep the thread alive; Stop() is the only exit.
        }
        UNREFERENCED_PARAMETER(deliveredAny);

        if (paused)
        {
            // Keep retrying the preroll until the renderer accepts it: the graph pauses
            // filters in an unspecified order, so the very first sample can arrive before
            // the renderer itself is paused and gets rejected. A real capture filter keeps
            // trying too. Bail out after ~3 s so a permanently mismatched allocator cannot
            // spin forever; Run()/Stop() also ends this path.
            if (lastReceiveHr == S_OK || lastReceiveHr == S_FALSE) m_prerollDone = true;
            else if (++prerollAttempts > 300) m_prerollDone = true;
            else Sleep(5);
            continue;
        }
        m_frameIdx.fetch_add(1);
        next += interval;
        firstSample = false;
    }
    m_threadExited = true;
    VcamLog("StreamThread exit delivered=%llu dropped=%llu placeholder=%llu shm=%llu",
            (unsigned long long)m_framesDelivered.load(), (unsigned long long)m_framesDropped.load(),
            (unsigned long long)m_framesPlaceholder.load(), (unsigned long long)m_framesFromShm.load());
    CoUninitialize();
}

STDMETHODIMP CVcamFilter::Run(REFERENCE_TIME tStart)
{
    EnterCriticalSection(&m_cs);
    const bool wasStopped = (m_state.load() == State_Stopped);
    m_rtStart = (LONGLONG)tStart;
    if (wasStopped)
    {
        m_resetTiming = true;
        LARGE_INTEGER c;
        QueryPerformanceCounter(&c);
        m_qpcBase = c.QuadPart;
    }
    m_state = State_Running;
    ResetEvent(m_hStopEvent);
    m_prerollDone = false;
    m_shmLastChange100ns = QpcNow100ns();   // do not treat a long Stop/Pause as producer death
    LeaveCriticalSection(&m_cs);

    // Commit BEFORE the worker thread starts: the worker calls GetBuffer on its very
    // first iteration and must never observe an uncommitted allocator (VFW_E_NOT_COMMITTED).
    CommitAllPins();
    StartThread();
    VcamLog("Run(tStart=%lld) done", (long long)tStart);
    return S_OK;
}

void CVcamFilter::StartThread()
{
    // The graph calls Pause() before Run(); the source thread must already exist at
    // Pause time so that a preroll sample can be delivered (fast first paint / C14).
    if (m_thread.joinable() && m_threadExited.load())
        m_thread.join();                     // reap a thread that ended on its own
    if (!m_thread.joinable())
    {
        m_threadExited = false;
        m_thread = std::thread(&CVcamFilter::StreamThread, this);
    }
}

STDMETHODIMP CVcamFilter::Pause()
{
    EnterCriticalSection(&m_cs);
    m_state = State_Paused;
    m_prerollDone = false;
    ResetEvent(m_hStopEvent);
    m_shmLastChange100ns = QpcNow100ns();
    LeaveCriticalSection(&m_cs);
    CommitAllPins();   // needed for the preroll sample; must precede the worker thread
    StartThread();
    VcamLog("Pause done");
    return S_OK;
}

STDMETHODIMP CVcamFilter::Stop()
{
    EnterCriticalSection(&m_cs);
    m_state = State_Stopped;
    SetEvent(m_hStopEvent);
    LeaveCriticalSection(&m_cs);
    DecommitAllPins();                           // unblock GetBuffer inside the thread
    if (m_thread.joinable()) m_thread.join();
    // NOTE: the allocators themselves survive Stop() so that Stop -> Run works without a
    // reconnect (C13). Disconnect()/the destructor release them.
    ResetStream();
    VcamLog("Stop done");
    return S_OK;
}

STDMETHODIMP CVcamFilter::GetState(DWORD dwMilliSecsTimeout, FILTER_STATE* State)
{
    UNREFERENCED_PARAMETER(dwMilliSecsTimeout);
    if (!State) return E_POINTER;
    *State = (FILTER_STATE)m_state.load();
    return S_OK;
}

STDMETHODIMP CVcamFilter::SetSyncSource(IReferenceClock* pClock)
{
    EnterCriticalSection(&m_cs);
    if (m_pClock) m_pClock->Release();
    m_pClock = pClock;
    if (m_pClock) m_pClock->AddRef();
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CVcamFilter::GetSyncSource(IReferenceClock** ppClock)
{
    if (!ppClock) return E_POINTER;
    EnterCriticalSection(&m_cs);
    *ppClock = m_pClock;
    if (m_pClock) m_pClock->AddRef();
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CVcamFilter::EnumPins(IEnumPins** ppEnum)
{
    if (!ppEnum) return E_POINTER;
    HRESULT hr = S_OK;
    CEnumPins* p = new CEnumPins(this, &hr);
    if (!p) return E_OUTOFMEMORY;
    *ppEnum = p;
    return S_OK;
}

STDMETHODIMP CVcamFilter::FindPin(LPCWSTR Id, IPin** ppPin)
{
    if (!Id || !ppPin) return E_POINTER;
    *ppPin = nullptr;
    int idx = -1;
    if (_wcsicmp(Id, L"Capture") == 0 || _wcsicmp(Id, L"Output") == 0) idx = VcamPinCapture;
    else if (_wcsicmp(Id, L"Preview") == 0) idx = VcamPinPreview;
    if (idx < 0 || !m_pPins[idx]) return VFW_E_NOT_FOUND;
    m_pPins[idx]->AddRef();
    *ppPin = static_cast<IPin*>(m_pPins[idx]);
    return S_OK;
}

STDMETHODIMP CVcamFilter::QueryFilterInfo(FILTER_INFO* pInfo)
{
    if (!pInfo) return E_POINTER;
    wcscpy_s(pInfo->achName, m_name);
    pInfo->pGraph = m_pGraph;
    if (m_pGraph) m_pGraph->AddRef();
    return S_OK;
}

STDMETHODIMP CVcamFilter::JoinFilterGraph(IFilterGraph* pGraph, LPCWSTR pName)
{
    EnterCriticalSection(&m_cs);
    if (m_pGraph) { m_pGraph->Release(); m_pGraph = nullptr; }
    m_pGraph = pGraph;
    if (m_pGraph) m_pGraph->AddRef();
    if (pName) wcscpy_s(m_name, pName);
    LeaveCriticalSection(&m_cs);
    return S_OK;
}

STDMETHODIMP CVcamFilter::QueryVendorInfo(LPWSTR* pVendorInfo)
{
    UNREFERENCED_PARAMETER(pVendorInfo);
    return E_NOTIMPL;
}

// ===========================================================================
// class factory + DLL exports
// ===========================================================================
class CVcamClassFactory : public IClassFactory
{
public:
    CVcamClassFactory() : m_ref(1) { InterlockedIncrement(&g_cDllRef); }
    ~CVcamClassFactory() { InterlockedDecrement(&g_cDllRef); }
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = static_cast<IClassFactory*>(this); AddRef(); return S_OK; }
        *ppv = nullptr; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }
    STDMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv) override
    {
        if (pUnkOuter) return CLASS_E_NOAGGREGATION;
        if (riid != IID_IBaseFilter && riid != IID_IUnknown) return E_NOINTERFACE;
        HRESULT hr = S_OK;
        CVcamFilter* p = new CVcamFilter(&hr);
        if (!p) return E_OUTOFMEMORY;
        if (FAILED(hr)) { p->Release(); return hr; }
        hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL bLock) override
    {
        if (bLock) InterlockedIncrement(&g_cDllRef); else InterlockedDecrement(&g_cDllRef);
        return S_OK;
    }
private:
    LONG m_ref;
};

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (!ppv) return E_POINTER;
    if (rclsid != CLSID_YoCameraVirtualCam) return CLASS_E_CLASSNOTAVAILABLE;
    CVcamClassFactory* p = new CVcamClassFactory();
    if (!p) return E_OUTOFMEMORY;
    HRESULT hr = p->QueryInterface(riid, ppv);
    p->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_cDllRef == 0) ? S_OK : S_FALSE;
}

// ---------------------------------------------------------------------------
// registry helpers
// ---------------------------------------------------------------------------
// Shared-memory name. The contract name is fixed, but tests may point the filter at an
// isolated mapping (YIOU_VCAM_SHM) so an orientation/producer probe does not disturb a
// real producer writing the contract mapping.
static const wchar_t* ShmName()
{
    static WCHAR buf[MAX_PATH] = { 0 };
    static bool init = false;
    if (!init)
    {
        init = true;
        const DWORD n = GetEnvironmentVariableW(L"YIOU_VCAM_SHM", buf, MAX_PATH);
        if (n == 0 || n >= MAX_PATH) { wcscpy_s(buf, VCAM_SHM_NAME); }
    }
    return buf;
}

static HRESULT WriteStringValue(HKEY hRoot, const std::wstring& path, LPCWSTR valueName, LPCWSTR data)
{
    HKEY hKey = nullptr;
    LONG lr = RegCreateKeyExW(hRoot, path.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &hKey, nullptr);
    if (lr != ERROR_SUCCESS) return HRESULT_FROM_WIN32(lr);
    lr = RegSetValueExW(hKey, valueName, 0, REG_SZ, reinterpret_cast<const BYTE*>(data),
                        static_cast<DWORD>((wcslen(data) + 1) * sizeof(wchar_t)));
    RegCloseKey(hKey);
    return HRESULT_FROM_WIN32(lr);
}

static HRESULT DeleteTreeInHive(bool perUser)
{
    HKEY hRoot = perUser ? HKEY_CURRENT_USER : HKEY_CLASSES_ROOT;
    const std::wstring base = perUser ? L"Software\\Classes\\" : L"";
    const std::wstring instanceKey = base + L"CLSID\\" VCAM_CATEGORY_CLSID_STR L"\\Instance\\" VCAM_CLSID_STR;
    const std::wstring clsidKey = base + L"CLSID\\" VCAM_CLSID_STR;
    const LONG a = RegDeleteTreeW(hRoot, instanceKey.c_str());
    const LONG b = RegDeleteTreeW(hRoot, clsidKey.c_str());
    // "not found" is success for an idempotent uninstall
    const bool ok = ((a == ERROR_SUCCESS) || (a == ERROR_FILE_NOT_FOUND)) &&
                    ((b == ERROR_SUCCESS) || (b == ERROR_FILE_NOT_FOUND));
    return ok ? S_OK : HRESULT_FROM_WIN32(a != ERROR_SUCCESS ? a : b);
}

// perUser == true  -> HKCU\Software\Classes (no elevation required)
// perUser == false -> HKCR                    (requires elevation)
//
// The Instance key carries DevicePath, which DirectShow exposes through the device
// moniker property bag -> DsDevice.DevicePath. Without it WPFMediaKit's
// AddFilterByDevicePath(path) matches every software camera whose DevicePath is null
// (OBS Virtual Camera, ...) and can bind the wrong device (PLAN section 13.1).
static HRESULT RegisterInHive(bool perUser, const std::wstring& friendlyName)
{
    HKEY hRoot = perUser ? HKEY_CURRENT_USER : HKEY_CLASSES_ROOT;
    const std::wstring base = perUser ? L"Software\\Classes\\" : L"";

    WCHAR dllPath[MAX_PATH] = { 0 };
    if (!GetModuleFileNameW(g_hInst, dllPath, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    const std::wstring clsidKey = base + L"CLSID\\" + VCAM_CLSID_STR;
    HRESULT hr = WriteStringValue(hRoot, clsidKey, nullptr, friendlyName.c_str());
    if (FAILED(hr)) return hr;
    hr = WriteStringValue(hRoot, clsidKey + L"\\InprocServer32", nullptr, dllPath);
    if (FAILED(hr)) return hr;
    hr = WriteStringValue(hRoot, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Both");
    if (FAILED(hr)) return hr;

    const std::wstring instanceKey = base + L"CLSID\\" VCAM_CATEGORY_CLSID_STR L"\\Instance\\" VCAM_CLSID_STR;
    hr = WriteStringValue(hRoot, instanceKey, L"FriendlyName", friendlyName.c_str());
    if (FAILED(hr)) return hr;
    hr = WriteStringValue(hRoot, instanceKey, L"CLSID", VCAM_CLSID_STR);
    if (FAILED(hr)) return hr;
    hr = WriteStringValue(hRoot, instanceKey, L"DevicePath", VCAM_DEVICE_PATH);   // C18
    return hr;
}

// DllInstall cmdline may carry a display-name override (used for future localisation):
//   regsvr32 /n /s /i:"name=Name shown in the host" vcam_filter.dll
//   regtool install vcam_filter.dll "Name shown in the host"
static std::wstring ResolveFriendlyName(LPCWSTR pszCmdLine)
{
    std::wstring name;
    if (pszCmdLine)
    {
        name = pszCmdLine;
        const size_t a = name.find_first_not_of(L" \t\"");
        const size_t b = name.find_last_not_of(L" \t\"");
        name = (a == std::wstring::npos) ? L"" : name.substr(a, b - a + 1);
        static const wchar_t kNamePrefix[] = L"name=";
        if (name.size() > 5 && _wcsnicmp(name.c_str(), kNamePrefix, 5) == 0)
            name = name.substr(5);
    }
    // regsvr32 may pass a bare "N"; treat junk as "use the default".
    if (name.empty() || name == L"n" || name == L"N") name = FILTER_FRIENDLY_NAME;
    return name;
}

STDAPI DllRegisterServer()
{
    // Machine-wide registration needs an administrator token. The shipping path is
    // DllInstall (per-user HKCU), see docs/CONTRACTS.md section 4/6.
    HRESULT hr = RegisterInHive(false, FILTER_FRIENDLY_NAME);
    if (FAILED(hr))
    {
        wchar_t buf[512];
        swprintf_s(buf, L"HKCR registration failed (0x%08X). Run elevated, or use DllInstall (regsvr32 /n /s /i) for per-user registration.",
                   (unsigned)hr);
        OutputDebugStringW(buf);
    }
    return hr;
}

STDAPI DllUnregisterServer()
{
    return DeleteTreeInHive(false);
}

// regsvr32 /n /s /i  ->  per-user install; no administrator token needed.
// This is the deployment path for the ICCCE plugin (the .icpx package cannot ship an
// installer). DllInstall(FALSE) removes both keys including DevicePath.
STDAPI DllInstall(BOOL bInstall, LPCWSTR pszCmdLine)
{
    if (bInstall)
        return RegisterInHive(true, ResolveFriendlyName(pszCmdLine));
    return DeleteTreeInHive(true);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(lpReserved);
    if (dwReason == DLL_PROCESS_ATTACH)
    {
        g_hInst = hModule;
        DisableThreadLibraryCalls(hModule);
        // NB: do not do any I/O here (loader lock). The trace file is opened lazily from
        // the first COM call; the lock is a plain critical section.
        InitializeCriticalSection(&g_logCs);
        g_logCsInit = true;
    }
    else if (dwReason == DLL_PROCESS_DETACH)
    {
        // No CRT I/O here (loader lock / CRT may already be torn down). The trace file
        // handle is process-owned and is released by the OS; the trace is debug-only.
        if (g_logCsInit)
        {
            DeleteCriticalSection(&g_logCs);
            g_logCsInit = false;
        }
    }
    return TRUE;
}
