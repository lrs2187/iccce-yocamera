// testhost.cpp - regression self-test for the production YiouCamera virtual camera.
//
// It exercises exactly the paths the ICCCE host (WPFMediaKit) uses, plus the C1-C20
// acceptance items from docs/PLAN.md section 8:
//
//   1. host-style enumeration (ICreateDevEnum + IMoniker + IPropertyBag):
//      FriendlyName == 驿欧无线展台, DevicePath == yocam:yiou-wireless-dc and unique,
//      BindToObject(IBaseFilter) works
//   2. pin surface: PIN_CATEGORY_CAPTURE via IKsPropertySet, IAMStreamConfig caps
//      (1280x720@30 + 1920x1080@30, AvgTimePerFrame != 0), NO IAMVideoProcAmp /
//      IAMCameraControl, SetFormat while UNCONNECTED (incl. the 0x0 call WPFMediaKit
//      makes when DesiredPixelWidth/Height are never set)
//   3. streaming: Pause preroll + Run first-sample latency (MediaOpened < 1.5 s proxy),
//      60/60 frames at 30 fps, monotonic timestamps, content classification
//      (gray placeholder vs shared-memory producer frames)
//   4. Stop -> Run on the same connection (allocator must survive, C13)
//   5. Stop -> Disconnect -> Connect -> Run and 3x repeated rebuild (C13)
//   6. two filter instances at the same time (multi-instance, shared SHM frame source)
//
// Usage:
//   testhost.exe                     expect NO producer running -> placeholder frames
//   testhost.exe --producer          expect shm_producer.exe running -> producer frames
//
// Exit code = number of failed checks (0 = PASS). Requires the filter to be registered.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <dshow.h>
#include <d3d9.h>
#include <vmr9.h>
#include <ocidl.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <vector>
#include <string>
#include <set>
#include <atomic>

#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "uuid.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "d3d9.lib")

static const GUID CLSID_YoCameraVirtualCam =
{ 0x9A3E7D21, 0x4C5B, 0x4E8A, { 0x9B, 0x77, 0x2D, 0x14, 0x6C, 0xE0, 0x5A, 0x31 } };
static const wchar_t* CLSID_STR = L"{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}";
static const wchar_t* FILTER_NAME = L"\u9A7F\u6B27\u65E0\u7EBF\u5C55\u53F0"; // 驿欧无线展台
static const wchar_t* DEVICE_PATH = L"yocam:yiou-wireless-dc";
static const wchar_t* SHM_NAME = L"Local\\YoCameraVcamFrames";

static int g_failures = 0;
static int g_checks = 0;

static bool g_expectProducer = false;
static bool g_producerPresent = false;
static bool g_orientCheck = false;
static int  g_vmr9Mode = 0;   // 0 = off, 1 = windowed VMR9, 2 = renderless VMR9 (our filter), 3 = renderless VMR9 (file source)
static std::wstring g_vmr9FilePath;

static int ReadShmReadSeq()
{
    WCHAR name[MAX_PATH] = { 0 };
    DWORD n = GetEnvironmentVariableW(L"YIOU_VCAM_SHM", name, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) wcscpy_s(name, SHM_NAME);
    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!h) return -1;
    const BYTE* p = (const BYTE*)MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0);
    int v = -1;
    if (p) { memcpy(&v, p + 48, sizeof(v)); UnmapViewOfFile(p); }
    CloseHandle(h);
    return v;
}

static void Check(bool ok, const char* what)
{
    g_checks++;
    if (ok) printf("[PASS] %s\n", what);
    else { printf("[FAIL] %s\n", what); g_failures++; }
}

static ULONGLONG NowMs()
{
    static LARGE_INTEGER f = { 0 };
    if (f.QuadPart == 0) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (ULONGLONG)(c.QuadPart * 1000ull / (ULONGLONG)f.QuadPart);
}

static void FreeMediaType(AM_MEDIA_TYPE& mt)
{
    if (mt.cbFormat && mt.pbFormat) CoTaskMemFree(mt.pbFormat);
    if (mt.pUnk) mt.pUnk->Release();
    ZeroMemory(&mt, sizeof(mt));
}

static HRESULT MakeRgb24(int w, int h, int fps, AM_MEDIA_TYPE* out)
{
    ZeroMemory(out, sizeof(*out));
    VIDEOINFOHEADER* vih = (VIDEOINFOHEADER*)CoTaskMemAlloc(sizeof(VIDEOINFOHEADER));
    if (!vih) return E_OUTOFMEMORY;
    ZeroMemory(vih, sizeof(*vih));
    const int stride = (w * 3 + 3) & ~3;
    vih->bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    vih->bmiHeader.biWidth = w;
    vih->bmiHeader.biHeight = h;
    vih->bmiHeader.biPlanes = 1;
    vih->bmiHeader.biBitCount = 24;
    vih->bmiHeader.biCompression = BI_RGB;
    vih->bmiHeader.biSizeImage = stride * h;
    vih->rcSource.right = w; vih->rcSource.bottom = h;
    vih->rcTarget = vih->rcSource;
    vih->AvgTimePerFrame = 10000000LL / fps;
    out->majortype = MEDIATYPE_Video;
    out->subtype = MEDIASUBTYPE_RGB24;
    out->bFixedSizeSamples = TRUE;
    out->bTemporalCompression = FALSE;
    out->lSampleSize = stride * h;
    out->formattype = FORMAT_VideoInfo;
    out->cbFormat = sizeof(VIDEOINFOHEADER);
    out->pbFormat = (BYTE*)vih;
    return S_OK;
}

static bool ReadBagString(IPropertyBag* bag, LPCWSTR prop, std::wstring* out)
{
    if (!bag) return false;
    VARIANT v;
    VariantInit(&v);
    HRESULT hr = bag->Read(prop, &v, nullptr);
    bool ok = false;
    if (SUCCEEDED(hr) && v.vt == VT_BSTR && v.bstrVal)
    {
        *out = v.bstrVal;
        ok = true;
    }
    VariantClear(&v);
    return ok;
}

static std::wstring Widen(const char* s) { return std::wstring(s, s + strlen(s)); }

static std::string Narrow(const std::wstring& s)
{
    if (s.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0, nullptr, nullptr);
    std::string r((size_t)(n > 0 ? n : 0), '\0');
    if (n > 0) WideCharToMultiByte(CP_UTF8, 0, s.c_str(), (int)s.size(), &r[0], n, nullptr, nullptr);
    return r;
}

// ---------------------------------------------------------------------------
// frame sink: what a renderer pin looks like to a source filter
// ---------------------------------------------------------------------------
struct FrameStats
{
    std::atomic<int> samples;
    std::atomic<int> badSamples;
    std::atomic<int> ptsRegressions;
    std::atomic<long long> lastPts;
    CRITICAL_SECTION cs;
    std::set<unsigned> fingerprints;
    // first-frame classification
    std::atomic<bool> firstSeen;
    BYTE firstPixel[3];
    BYTE topPixel[3];
    BYTE centerPixel[3];
    double grayFraction;      // grayVotes / votes over the first few samples
    int analyzedCount;        // samples classified so far
    int grayVotes;
    int votes;
    ULONGLONG firstSampleMs;
    int frameW, frameH;

    FrameStats()
    {
        samples = 0; badSamples = 0; ptsRegressions = 0; lastPts = -1;
        InitializeCriticalSection(&cs);
        firstSeen = false;
        firstPixel[0] = firstPixel[1] = firstPixel[2] = 0;
        topPixel[0] = topPixel[1] = topPixel[2] = 0;
        centerPixel[0] = centerPixel[1] = centerPixel[2] = 0;
        grayFraction = -1.0;
        analyzedCount = 0;
        grayVotes = 0;
        votes = 0;
        firstSampleMs = 0;
        frameW = frameH = 0;
    }
    ~FrameStats() { DeleteCriticalSection(&cs); }

    void Reset()
    {
        samples = 0; badSamples = 0; ptsRegressions = 0; lastPts = -1;
        EnterCriticalSection(&cs);
        fingerprints.clear();
        LeaveCriticalSection(&cs);
        firstSeen = false;
        grayFraction = -1.0;
        analyzedCount = 0;
        grayVotes = 0;
        votes = 0;
        firstSampleMs = 0;
        frameW = frameH = 0;
        topPixel[0] = topPixel[1] = topPixel[2] = 0;
    }
};

static bool IsYuy2Type(const AM_MEDIA_TYPE& mt) { return IsEqualGUID(mt.subtype, MEDIASUBTYPE_YUY2) != FALSE; }
static int Stride24(int w) { return (w * 3 + 3) & ~3; }
static int StrideYuy2(int w) { return (w * 2 + 3) & ~3; }

// Approximate BT.601 limited-range YUV -> RGB, only used to validate frame content.
static void YuvToRgb(BYTE y, BYTE u, BYTE v, BYTE rgb[3])
{
    const int c = (int)y - 16, d = (int)u - 128, e = (int)v - 128;
    const int r = (298 * c + 409 * e + 128) >> 8;
    const int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
    const int b = (298 * c + 516 * d + 128) >> 8;
    rgb[0] = (BYTE)(b < 0 ? 0 : (b > 255 ? 255 : b));
    rgb[1] = (BYTE)(g < 0 ? 0 : (g > 255 ? 255 : g));
    rgb[2] = (BYTE)(r < 0 ? 0 : (r > 255 ? 255 : r));
}

static bool YuvGray(BYTE /*y*/, BYTE u, BYTE v) { return (u >= 120 && u <= 136 && v >= 120 && v <= 136); }

class CSinkPin : public IPin, public IMemInputPin
{
public:
    explicit CSinkPin(FrameStats* stats)
        : m_ref(1), m_pConnected(nullptr), m_pAllocator(nullptr), m_stats(stats), m_prerollSeen(0)
    {
        ZeroMemory(&m_mt, sizeof(m_mt));
    }
    ~CSinkPin()
    {
        Disconnect();
        FreeMediaType(m_mt);
    }

    int prerollSamples() const { return m_prerollSeen; }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IPin) *ppv = static_cast<IPin*>(this);
        else if (riid == IID_IMemInputPin) *ppv = static_cast<IMemInputPin*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }

    STDMETHODIMP Connect(IPin*, const AM_MEDIA_TYPE*) override { return E_UNEXPECTED; }
    STDMETHODIMP ReceiveConnection(IPin* pConnector, const AM_MEDIA_TYPE* pmt) override
    {
        if (!pConnector || !pmt) return E_POINTER;
        if (m_pConnected) return VFW_E_ALREADY_CONNECTED;
        if (pmt->majortype != MEDIATYPE_Video) return VFW_E_TYPE_NOT_ACCEPTED;
        if (pmt->formattype != FORMAT_VideoInfo) return VFW_E_TYPE_NOT_ACCEPTED;
        if (!IsEqualGUID(pmt->subtype, MEDIASUBTYPE_RGB24) && !IsEqualGUID(pmt->subtype, MEDIASUBTYPE_YUY2))
            return VFW_E_TYPE_NOT_ACCEPTED;
        FreeMediaType(m_mt);
        m_mt = *pmt;
        m_mt.pbFormat = nullptr;
        if (pmt->pUnk) pmt->pUnk->AddRef();
        if (pmt->cbFormat)
        {
            m_mt.pbFormat = (BYTE*)CoTaskMemAlloc(pmt->cbFormat);
            if (!m_mt.pbFormat) return E_OUTOFMEMORY;
            memcpy(m_mt.pbFormat, pmt->pbFormat, pmt->cbFormat);
        }
        m_pConnected = pConnector;
        m_pConnected->AddRef();
        return S_OK;
    }
    STDMETHODIMP Disconnect() override
    {
        if (m_pAllocator) { m_pAllocator->Decommit(); m_pAllocator->Release(); m_pAllocator = nullptr; }
        if (m_pConnected) { m_pConnected->Release(); m_pConnected = nullptr; }
        return S_OK;
    }
    STDMETHODIMP ConnectedTo(IPin** ppPin) override
    {
        if (!ppPin) return E_POINTER;
        *ppPin = m_pConnected;
        if (m_pConnected) m_pConnected->AddRef();
        return m_pConnected ? S_OK : VFW_E_NOT_CONNECTED;
    }
    STDMETHODIMP ConnectionMediaType(AM_MEDIA_TYPE* pmt) override
    {
        if (!pmt) return E_POINTER;
        if (!m_pConnected) return VFW_E_NOT_CONNECTED;
        *pmt = m_mt;
        return S_OK;
    }
    STDMETHODIMP QueryPinInfo(PIN_INFO* pInfo) override
    {
        if (!pInfo) return E_POINTER;
        pInfo->pFilter = nullptr;
        pInfo->dir = PINDIR_INPUT;
        wcscpy_s(pInfo->achName, L"Sink");
        return S_OK;
    }
    STDMETHODIMP QueryDirection(PIN_DIRECTION* d) override { if (!d) return E_POINTER; *d = PINDIR_INPUT; return S_OK; }
    STDMETHODIMP QueryId(LPWSTR* Id) override
    {
        if (!Id) return E_POINTER;
        const wchar_t* n = L"Sink";
        size_t cb = (wcslen(n) + 1) * sizeof(wchar_t);
        *Id = (LPWSTR)CoTaskMemAlloc(cb);
        if (!*Id) return E_OUTOFMEMORY;
        memcpy(*Id, n, cb);
        return S_OK;
    }
    STDMETHODIMP QueryAccept(const AM_MEDIA_TYPE* pmt) override
    {
        return (pmt && pmt->majortype == MEDIATYPE_Video &&
                (IsEqualGUID(pmt->subtype, MEDIASUBTYPE_RGB24) || IsEqualGUID(pmt->subtype, MEDIASUBTYPE_YUY2)))
               ? S_OK : S_FALSE;
    }
    STDMETHODIMP EnumMediaTypes(IEnumMediaTypes**) override { return E_NOTIMPL; }
    STDMETHODIMP QueryInternalConnections(IPin**, ULONG*) override { return E_NOTIMPL; }
    STDMETHODIMP EndOfStream() override { return S_OK; }
    STDMETHODIMP BeginFlush() override { return S_OK; }
    STDMETHODIMP EndFlush() override { return S_OK; }
    STDMETHODIMP NewSegment(REFERENCE_TIME, REFERENCE_TIME, double) override { return S_OK; }

    STDMETHODIMP GetAllocator(IMemAllocator** ppAllocator) override
    {
        if (!ppAllocator) return E_POINTER;
        *ppAllocator = nullptr;
        if (!m_pAllocator)
        {
            if (FAILED(CoCreateInstance(CLSID_MemoryAllocator, nullptr, CLSCTX_INPROC_SERVER,
                                        IID_IMemAllocator, (void**)&m_pAllocator)))
                return VFW_E_NO_ALLOCATOR;
            ALLOCATOR_PROPERTIES req = { 4, (long)m_mt.lSampleSize, 1, 0 }, act = { 0, 0, 0, 0 };
            m_pAllocator->SetProperties(&req, &act);
            if (FAILED(m_pAllocator->Commit())) { m_pAllocator->Release(); m_pAllocator = nullptr; return VFW_E_NO_ALLOCATOR; }
        }
        m_pAllocator->AddRef();
        *ppAllocator = m_pAllocator;
        return S_OK;
    }
    STDMETHODIMP NotifyAllocator(IMemAllocator* pAllocator, BOOL) override
    {
        if (m_pAllocator) m_pAllocator->Release();
        m_pAllocator = pAllocator;
        if (m_pAllocator) m_pAllocator->AddRef();
        return S_OK;
    }
    STDMETHODIMP GetAllocatorRequirements(ALLOCATOR_PROPERTIES* pProps) override
    {
        if (!pProps) return E_POINTER;
        ZeroMemory(pProps, sizeof(*pProps));
        return E_NOTIMPL;
    }
    STDMETHODIMP Receive(IMediaSample* pSample) override
    {
        BYTE* p = nullptr;
        if (!pSample || FAILED(pSample->GetPointer(&p))) return E_POINTER;
        const long len = pSample->GetActualDataLength();
        REFERENCE_TIME t1 = 0, t2 = 0;
        pSample->GetTime(&t1, &t2);
        HRESULT pr = pSample->IsPreroll();
        if (pr == S_OK) InterlockedIncrement(&m_prerollSeen);

        const bool yuy2 = IsYuy2Type(m_mt);
        const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)m_mt.pbFormat;
        const int w = (int)vih->bmiHeader.biWidth;
        const int h = (int)vih->bmiHeader.biHeight;
        const int stride = yuy2 ? StrideYuy2(w) : Stride24(w);
        // Sampled pixel -> (B,G,R), using whatever layout was negotiated.
        // DirectShow with positive biHeight is bottom-up: buffer row 0 is the BOTTOM.
        auto pixelAt = [&](const BYTE* base, int row, int col, BYTE rgb[3])
        {
            const BYTE* r = base + (size_t)row * stride;
            if (yuy2)
            {
                const BYTE* q = r + (size_t)(col & ~1) * 2;
                YuvToRgb(q[0], q[1], q[3], rgb);
            }
            else
            {
                rgb[0] = r[(size_t)col * 3 + 0];
                rgb[1] = r[(size_t)col * 3 + 1];
                rgb[2] = r[(size_t)col * 3 + 2];
            }
        };

        const bool first = !m_stats->firstSeen.exchange(true);
        if (first)
        {
            m_stats->frameW = w; m_stats->frameH = h;
            m_stats->firstSampleMs = NowMs();
            if (w >= 4 && h >= 4)
            {
                pixelAt(p, 0, 0, m_stats->firstPixel);
                pixelAt(p, h - 1, w / 2, m_stats->topPixel);
                pixelAt(p, h / 2, w / 2, m_stats->centerPixel);
            }
        }
        // Classify the first few samples (a single torn frame at producer start-up must
        // not flip the verdict): placeholder = flat gray field, producer = color bars.
        if (m_stats->analyzedCount < 8 && w >= 16 && h >= 16)
        {
            int gray = 0, total = 0;
            for (int gy = h / 8; gy < 7 * h / 8; gy += (h / 32 > 0 ? h / 32 : 1))
            {
                const BYTE* row = p + (size_t)gy * stride;
                for (int gx = w / 8; gx < 7 * w / 8; gx += (w / 32 > 0 ? w / 32 : 1))
                {
                    if (yuy2)
                    {
                        const BYTE* q = row + (size_t)(gx & ~1) * 2;
                        if (YuvGray(q[0], q[1], q[3])) gray++;
                    }
                    else
                    {
                        const BYTE b = row[(size_t)gx * 3 + 0], gg = row[(size_t)gx * 3 + 1], r = row[(size_t)gx * 3 + 2];
                        if (b == gg && gg == r) gray++;
                    }
                    total++;
                }
            }
            if (total > 0)
            {
                const double gf = (double)gray / (double)total;
                if (gf > 0.80) m_stats->grayVotes++;
                m_stats->votes++;
                m_stats->grayFraction = (double)m_stats->grayVotes / (double)m_stats->votes;
            }
            m_stats->analyzedCount++;
        }

        long long prev = m_stats->lastPts.exchange((long long)t1);
        if (prev >= 0 && (long long)t1 <= prev) m_stats->ptsRegressions++;
        if (len < (long)vihLen()) m_stats->badSamples++;

        unsigned sum = 2166136261u;
        for (long i = 0; i < len; i += 997) sum = sum * 16777619u ^ (unsigned)p[i];
        EnterCriticalSection(&m_stats->cs);
        m_stats->fingerprints.insert(sum);
        LeaveCriticalSection(&m_stats->cs);

        m_stats->samples++;
        return S_OK;
    }
    STDMETHODIMP ReceiveMultiple(IMediaSample** pSamples, long n, long* nProcessed) override
    {
        for (long i = 0; i < n; ++i) Receive(pSamples[i]);
        if (nProcessed) *nProcessed = n;
        return S_OK;
    }
    STDMETHODIMP ReceiveCanBlock() override { return S_FALSE; }

private:
    long vihLen() const
    {
        const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)m_mt.pbFormat;
        const int w = (int)vih->bmiHeader.biWidth;
        const int stride = IsYuy2Type(m_mt) ? StrideYuy2(w) : Stride24(w);
        return (long)stride * vih->bmiHeader.biHeight;
    }

    LONG m_ref;
    IPin* m_pConnected;
    IMemAllocator* m_pAllocator;
    FrameStats* m_stats;
    volatile LONG m_prerollSeen;
    AM_MEDIA_TYPE m_mt;
};

// ---------------------------------------------------------------------------
// helpers around our filter
// ---------------------------------------------------------------------------
static IBaseFilter* CreateFilter()
{
    IBaseFilter* f = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_YoCameraVirtualCam, nullptr, CLSCTX_INPROC_SERVER,
                                  IID_IBaseFilter, (void**)&f);
    if (FAILED(hr) || !f) return nullptr;
    return f;
}

static void GetPins(IBaseFilter* f, std::vector<IPin*>* out)
{
    out->clear();
    IEnumPins* e = nullptr;
    if (FAILED(f->EnumPins(&e)) || !e) return;
    IPin* p = nullptr;
    ULONG n = 0;
    while (e->Next(1, &p, &n) == S_OK && p) { out->push_back(p); p = nullptr; }
    e->Release();
}

static void ReleasePins(std::vector<IPin*>* pins)
{
    for (size_t i = 0; i < pins->size(); ++i) (*pins)[i]->Release();
    pins->clear();
}

static bool PinCategoryOf(IPin* pin, GUID* cat)
{
    if (!pin) return false;
    IKsPropertySet* ps = nullptr;
    if (FAILED(pin->QueryInterface(IID_IKsPropertySet, (void**)&ps)) || !ps) return false;
    DWORD cb = 0;
    ZeroMemory(cat, sizeof(*cat));
    const HRESULT hr = ps->Get(AMPROPSETID_Pin, AMPROPERTY_PIN_CATEGORY, nullptr, 0, cat, sizeof(*cat), &cb);
    ps->Release();
    return SUCCEEDED(hr);
}

// returns a pin with one reference for the caller, or nullptr
static IPin* GetPinByCategory(IBaseFilter* f, const GUID& want)
{
    std::vector<IPin*> pins;
    GetPins(f, &pins);
    for (size_t i = 0; i < pins.size(); ++i)
    {
        GUID cat = { 0 };
        if (PinCategoryOf(pins[i], &cat) && IsEqualGUID(cat, want))
        {
            IPin* hit = pins[i];
            for (size_t j = 0; j < pins.size(); ++j)
                if (j != i) pins[j]->Release();
            return hit;
        }
    }
    ReleasePins(&pins);
    return nullptr;
}

static bool WaitForSamples(FrameStats* st, int target, ULONGLONG timeoutMs)
{
    const ULONGLONG deadline = NowMs() + timeoutMs;
    while (st->samples.load() < target)
    {
        if (NowMs() > deadline) return false;
        Sleep(2);
    }
    return true;
}

static bool DetectShmProducer()
{
    WCHAR name[MAX_PATH] = { 0 };
    DWORD n = GetEnvironmentVariableW(L"YIOU_VCAM_SHM", name, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) wcscpy_s(name, SHM_NAME);
    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, name);
    if (!h) return false;
    CloseHandle(h);
    return true;
}

struct DeviceEntry
{
    std::wstring display;
    std::wstring friendly;
    std::wstring devicePath;
};

static bool EnumerateDevices(std::vector<DeviceEntry>* out)
{
    out->clear();
    ICreateDevEnum* dev = nullptr;
    if (FAILED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER,
                                IID_ICreateDevEnum, (void**)&dev)) || !dev)
        return false;
    IEnumMoniker* e = nullptr;
    HRESULT hr = dev->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &e, 0);
    dev->Release();
    if (hr != S_OK || !e)
    {
        if (e) e->Release();
        return true;   // no devices at all is a valid (empty) result
    }
    IMoniker* m = nullptr;
    ULONG fetched = 0;
    while (e->Next(1, &m, &fetched) == S_OK && m)
    {
        DeviceEntry d;
        LPOLESTR disp = nullptr;
        if (SUCCEEDED(m->GetDisplayName(nullptr, nullptr, &disp)) && disp)
        {
            d.display = disp;
            CoTaskMemFree(disp);
        }
        IPropertyBag* bag = nullptr;
        if (SUCCEEDED(m->BindToStorage(nullptr, nullptr, IID_IPropertyBag, (void**)&bag)) && bag)
        {
            ReadBagString(bag, L"FriendlyName", &d.friendly);
            // This is the exact value WPFMediaKit's DsDevice.DevicePath reads.
            ReadBagString(bag, L"DevicePath", &d.devicePath);
            bag->Release();
        }
        out->push_back(d);
        m->Release();
        m = nullptr;
    }
    e->Release();
    return true;
}

static int TestEnumeration(IBaseFilter** ppFilter)
{
    *ppFilter = nullptr;
    std::vector<DeviceEntry> devs;
    Check(EnumerateDevices(&devs), "ICreateDevEnum(VideoInputDevice) enumerates");
    printf("[enum] %zu video input device(s)\n", devs.size());
    for (size_t i = 0; i < devs.size(); ++i)
    {
        printf("[enum]   %zu) FriendlyName='%s' DevicePath='%s'\n", i + 1,
               Narrow(devs[i].friendly).c_str(),
               devs[i].devicePath.empty() ? "<NULL>" : Narrow(devs[i].devicePath).c_str());
    }

    int ours = -1;
    for (size_t i = 0; i < devs.size(); ++i)
    {
        if (devs[i].display.find(CLSID_STR) != std::wstring::npos) { ours = (int)i; break; }
    }
    Check(ours >= 0, "our filter appears in the VideoInputDevice category (C1)");
    if (ours < 0) return g_failures;

    Check(devs[ours].friendly == FILTER_NAME, "FriendlyName is 驿欧无线展台 (C17)");
    Check(devs[ours].devicePath == DEVICE_PATH, "DevicePath is yocam:yiou-wireless-dc (C18)");

    int samePath = 0;
    for (size_t i = 0; i < devs.size(); ++i)
        if (!devs[i].devicePath.empty() && devs[i].devicePath == DEVICE_PATH) samePath++;
    Check(samePath == 1, "DevicePath is globally unique (exactly one device)");

    int nullPaths = 0;
    for (size_t i = 0; i < devs.size(); ++i) if (devs[i].devicePath.empty()) nullPaths++;
    if (nullPaths > 0)
        printf("[info] %d other device(s) still have DevicePath=<NULL> (e.g. OBS); our path is unique so no collision\n", nullPaths);

    // BindToObject through the moniker, like DsDevice.Moniker.BindToObject
    IBaseFilter* viaMoniker = nullptr;
    ICreateDevEnum* dev = nullptr;
    IEnumMoniker* e = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_SystemDeviceEnum, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_ICreateDevEnum, (void**)&dev)) && dev &&
        SUCCEEDED(dev->CreateClassEnumerator(CLSID_VideoInputDeviceCategory, &e, 0)) && e)
    {
        IMoniker* m = nullptr;
        ULONG n = 0;
        while (e->Next(1, &m, &n) == S_OK && m)
        {
            LPOLESTR disp = nullptr;
            bool isOurs = false;
            if (SUCCEEDED(m->GetDisplayName(nullptr, nullptr, &disp)) && disp)
            {
                isOurs = std::wstring(disp).find(CLSID_STR) != std::wstring::npos;
                CoTaskMemFree(disp);
            }
            if (isOurs)
            {
                HRESULT hr = m->BindToObject(nullptr, nullptr, IID_IBaseFilter, (void**)&viaMoniker);
                Check(SUCCEEDED(hr) && viaMoniker != nullptr, "moniker BindToObject(IBaseFilter) works (C2)");
                if (viaMoniker) { viaMoniker->Release(); viaMoniker = nullptr; }
                m->Release();
                break;
            }
            m->Release();
            m = nullptr;
        }
        e->Release();
    }
    if (dev) dev->Release();

    *ppFilter = CreateFilter();   // direct CoCreateInstance also validates InprocServer32
    Check(*ppFilter != nullptr, "CoCreateInstance(our CLSID) works (in-proc activation, C3)");
    return g_failures;
}

static void TestCapsAndFormat(IBaseFilter* f)
{
    std::vector<IPin*> pins;
    GetPins(f, &pins);
    Check(pins.size() == 2, "filter exposes TWO output pins: Capture + Preview (C4)");
    if (pins.size() != 2) { ReleasePins(&pins); return; }

    GUID c0 = { 0 }, c1 = { 0 };
    const bool ok0 = PinCategoryOf(pins[0], &c0);
    const bool ok1 = PinCategoryOf(pins[1], &c1);
    Check(ok0 && ok1, "both pins report a pin category through IKsPropertySet");
    if (!(ok0 && ok1)) { ReleasePins(&pins); return; }

    const bool cap0 = IsEqualGUID(c0, PIN_CATEGORY_CAPTURE);
    const bool cap1 = IsEqualGUID(c1, PIN_CATEGORY_CAPTURE);
    const bool prev0 = IsEqualGUID(c0, PIN_CATEGORY_PREVIEW);
    const bool prev1 = IsEqualGUID(c1, PIN_CATEGORY_PREVIEW);
    Check((cap0 && prev1) || (cap1 && prev0),
          "one pin reports PIN_CATEGORY_CAPTURE, the other PIN_CATEGORY_PREVIEW (WPFMediaKit RenderStream(Preview))");

    IPin* capPin = cap0 ? pins[0] : pins[1];
    IPin* prevPin = cap0 ? pins[1] : pins[0];
    UNREFERENCED_PARAMETER(prevPin);

    // FindPin by id, the way CaptureGraphBuilder2 also locates pins
    IPin* byName = nullptr;
    Check(SUCCEEDED(f->FindPin(L"Capture", &byName)) && byName != nullptr, "FindPin(\"Capture\") resolves");
    if (byName) { byName->Release(); byName = nullptr; }
    Check(SUCCEEDED(f->FindPin(L"Preview", &byName)) && byName != nullptr, "FindPin(\"Preview\") resolves");
    if (byName) { byName->Release(); byName = nullptr; }
    IPin* bogus = nullptr;
    Check(FAILED(f->FindPin(L"Nope", &bogus)) && bogus == nullptr, "FindPin(unknown) fails");

    // deliberately not implemented: the device has no image controls
    IUnknown* unk = nullptr;
    Check(FAILED(capPin->QueryInterface(IID_IAMVideoProcAmp, (void**)&unk)) || unk == nullptr,
          "IAMVideoProcAmp is NOT exposed (PLAN section 8)");
    unk = nullptr;
    Check(FAILED(capPin->QueryInterface(IID_IAMCameraControl, (void**)&unk)) || unk == nullptr,
          "IAMCameraControl is NOT exposed (PLAN section 8)");

    IAMStreamConfig* capCfg = nullptr;
    Check(SUCCEEDED(capPin->QueryInterface(IID_IAMStreamConfig, (void**)&capCfg)) && capCfg != nullptr,
          "capture pin exposes IAMStreamConfig (C6)");
    IAMStreamConfig* prevCfg = nullptr;
    Check(SUCCEEDED(prevPin->QueryInterface(IID_IAMStreamConfig, (void**)&prevCfg)) && prevCfg != nullptr,
          "preview pin exposes IAMStreamConfig too");
    if (!capCfg) { if (prevCfg) prevCfg->Release(); ReleasePins(&pins); return; }

    int count = 0, size = 0;
    Check(SUCCEEDED(capCfg->GetNumberOfCapabilities(&count, &size)) && count == 4 &&
          size == (int)sizeof(VIDEO_STREAM_CONFIG_CAPS),
          "GetNumberOfCapabilities => 4 x VIDEO_STREAM_CONFIG_CAPS (YUY2 + RGB24)");

    bool yuy2_720 = false, yuy2_1080 = false, rgb24_720 = false, rgb24_1080 = false, allValid = true;
    for (int i = 0; i < count; ++i)
    {
        AM_MEDIA_TYPE* pmt = nullptr;
        BYTE* scc = (BYTE*)CoTaskMemAlloc(size);
        ZeroMemory(scc, size);
        if (SUCCEEDED(capCfg->GetStreamCaps(i, &pmt, scc)) && pmt)
        {
            const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)pmt->pbFormat;
            const VIDEO_STREAM_CONFIG_CAPS* pc = (const VIDEO_STREAM_CONFIG_CAPS*)scc;
            const int w = (int)vih->bmiHeader.biWidth;
            const int h = (int)vih->bmiHeader.biHeight;
            const bool yuy2 = IsYuy2Type(*pmt);
            const double fps = vih->AvgTimePerFrame ? 10000000.0 / (double)vih->AvgTimePerFrame : 0.0;
            printf("[caps] #%d: %s %dx%d @ %.3f fps (minInterval=%lld)\n", i, yuy2 ? "YUY2" : "RGB24",
                   w, h, fps, (long long)pc->MinFrameInterval);
            if (yuy2 && w == 1280 && h == 720) yuy2_720 = true;
            if (yuy2 && w == 1920 && h == 1080) yuy2_1080 = true;
            if (!yuy2 && w == 1280 && h == 720) rgb24_720 = true;
            if (!yuy2 && w == 1920 && h == 1080) rgb24_1080 = true;
            const bool layoutOk = yuy2 ? (vih->bmiHeader.biBitCount == 16 &&
                                          vih->bmiHeader.biCompression == MAKEFOURCC('Y', 'U', 'Y', '2'))
                                       : (vih->bmiHeader.biBitCount == 24 &&
                                          vih->bmiHeader.biCompression == BI_RGB);
            if (pmt->formattype != FORMAT_VideoInfo || !layoutOk ||
                vih->bmiHeader.biHeight <= 0 || vih->AvgTimePerFrame == 0 || pc->MinFrameInterval == 0)
                allValid = false;
            FreeMediaType(*pmt);
            CoTaskMemFree(pmt);
        }
        else allValid = false;
        CoTaskMemFree(scc);
    }
    Check(yuy2_720 && yuy2_1080, "capability list contains YUY2 1280x720 and 1920x1080");
    Check(rgb24_720 && rgb24_1080, "capability list contains RGB24 1280x720 and 1920x1080");
    Check(allValid, "all caps are FORMAT_VideoInfo with correct 16bpp YUY2 / 24bpp RGB24 layout (C8)");

    // GetFormat default must be the VMR9/WPFMediaKit-friendly YUY2 (Lead's requirement)
    {
        AM_MEDIA_TYPE* got = nullptr;
        if (SUCCEEDED(capCfg->GetFormat(&got)) && got)
        {
            const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)got->pbFormat;
            Check(IsYuy2Type(*got) && vih->bmiHeader.biWidth == 1280 && vih->bmiHeader.biHeight == 720,
                  "GetFormat default == YUY2 1280x720@30");
            FreeMediaType(*got);
            CoTaskMemFree(got);
        }
    }

    // SetFormat while UNCONNECTED (C10)
    AM_MEDIA_TYPE mt;
    if (SUCCEEDED(MakeRgb24(1920, 1080, 30, &mt)))
    {
        Check(SUCCEEDED(capCfg->SetFormat(&mt)), "SetFormat(1920x1080@30) works while unconnected (C10)");
        FreeMediaType(mt);
        AM_MEDIA_TYPE* got = nullptr;
        if (SUCCEEDED(capCfg->GetFormat(&got)) && got)
        {
            const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)got->pbFormat;
            Check(vih->bmiHeader.biWidth == 1920 && vih->bmiHeader.biHeight == 1080,
                  "GetFormat reflects the unconnected SetFormat");
            FreeMediaType(*got);
            CoTaskMemFree(got);
        }
    }
    // WPFMediaKit calls GetFormat then overwrites the size with DesiredPixelWidth/Height,
    // which default to 0 when the host never sets them -> SetFormat(0x0) must not fail.
    if (SUCCEEDED(MakeRgb24(0, 0, 30, &mt)))
    {
        Check(SUCCEEDED(capCfg->SetFormat(&mt)), "SetFormat(0x0) tolerated (WPFMediaKit default DesiredPixel*)");
        FreeMediaType(mt);
    }
    if (SUCCEEDED(MakeRgb24(1280, 720, 30, &mt)))
    {
        Check(SUCCEEDED(capCfg->SetFormat(&mt)), "SetFormat(1280x720@30) works while unconnected");
        FreeMediaType(mt);
    }
    capCfg->Release();
    if (prevCfg) prevCfg->Release();
    ReleasePins(&pins);
}

static void CheckPlaceholderContent(FrameStats* st, const char* label)
{
    char buf[256];
    sprintf_s(buf, "%s: gray placeholder content (no producer) - grayFraction=%.2f", label, st->grayFraction);
    Check(st->grayFraction >= 0.80, buf);
    sprintf_s(buf, "%s: 60/60 frames keep flowing without a producer", label);
    Check(st->samples.load() >= 60, buf);
    sprintf_s(buf, "%s: placeholder first pixel is the gray border", label);
    Check(st->firstPixel[0] == st->firstPixel[1] && st->firstPixel[1] == st->firstPixel[2], buf);
    sprintf_s(buf, "%s: timestamps are monotonic", label);
    Check(st->ptsRegressions.load() == 0, buf);
}

static void CheckProducerContent(FrameStats* st, const char* label)
{
    char buf[256];
    sprintf_s(buf, "%s: frames come from the shared-memory producer (not the placeholder)", label);
    Check(st->grayFraction >= 0.0 && st->grayFraction < 0.60, buf);
    sprintf_s(buf, "%s: producer test pattern is animating", label);
    Check(st->fingerprints.size() >= 5, buf);
    sprintf_s(buf, "%s: 60/60 frames received", label);
    Check(st->samples.load() >= 60, buf);
    sprintf_s(buf, "%s: timestamps are monotonic", label);
    Check(st->ptsRegressions.load() == 0, buf);
}

// Real renderer check: build a graph with our filter + VideoMixingRenderer9 (the filter
// WPFMediaKit renders into) and see whether frames actually travel. This is the path that
// "renderer-like sink" cannot prove.  Mode 2 additionally mimics WPFMediaKit by using VMR9
// renderless mode.
// Minimal IVMRImagePresenter9 + IVMRSurfaceAllocator9 for VMR9 renderless mode. This is
// what WPFMediaKit's Vmr9Allocator does (minus the WPF D3DImage plumbing): create a D3D9Ex
// device, hand it to the VMR, and let the VMR allocate its own surfaces through
// AllocateSurfaceHelper. It lets us test the real renderless contract without WPF.
class CVmr9SurfaceAllocator : public IVMRSurfaceAllocator9, public IVMRImagePresenter9
{
public:
    CVmr9SurfaceAllocator()
        : m_ref(1), m_notify(nullptr), m_d3dEx(nullptr), m_dev(nullptr),
          m_surfaces(nullptr), m_count(0), m_present(0), m_started(0),
          m_deviceHr(E_FAIL), m_allocHr(E_FAIL) {}
    ~CVmr9SurfaceAllocator()
    {
        ReleaseSurfaces();
        if (m_dev) { m_dev->Release(); m_dev = nullptr; }
        if (m_d3dEx) { m_d3dEx->Release(); m_d3dEx = nullptr; }
        if (m_notify) { m_notify->Release(); m_notify = nullptr; }
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IVMRSurfaceAllocator9)
            *ppv = static_cast<IVMRSurfaceAllocator9*>(this);
        else if (riid == IID_IVMRImagePresenter9)
            *ppv = static_cast<IVMRImagePresenter9*>(this);
        else { *ppv = nullptr; return E_NOINTERFACE; }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG r = InterlockedDecrement(&m_ref);
        if (r == 0) delete this;
        return (ULONG)r;
    }

    int InitCalls() const { return m_initCalls; }
    HRESULT DeviceHr() const { return m_deviceHr; }
    HRESULT AllocHr() const { return m_allocHr; }
    int PresentCount() const { return m_present; }
    int StartedCount() const { return m_started; }

    STDMETHODIMP AdviseNotify(IVMRSurfaceAllocatorNotify9* notify) override
    {
        if (!notify) return E_POINTER;
        if (m_notify) m_notify->Release();
        m_notify = notify;
        m_notify->AddRef();
        if (!m_d3dEx)
        {
            if (FAILED(Direct3DCreate9Ex(D3D_SDK_VERSION, &m_d3dEx)) || !m_d3dEx)
            {
                m_deviceHr = E_FAIL;
                return m_deviceHr;
            }
        }
        D3DPRESENT_PARAMETERS pp;
        ZeroMemory(&pp, sizeof(pp));
        pp.Windowed = TRUE;
        pp.hDeviceWindow = GetDesktopWindow();
        pp.BackBufferWidth = 1;
        pp.BackBufferHeight = 1;
        pp.BackBufferFormat = D3DFMT_X8R8G8B8;
        pp.BackBufferCount = 1;
        pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
        m_deviceHr = m_d3dEx->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, GetDesktopWindow(),
                                           D3DCREATE_SOFTWARE_VERTEXPROCESSING, &pp, &m_dev);
        if (SUCCEEDED(m_deviceHr) && m_dev)
            m_deviceHr = m_notify->SetD3DDevice(m_dev, m_d3dEx->GetAdapterMonitor(D3DADAPTER_DEFAULT));
        return m_deviceHr;
    }
    STDMETHODIMP TerminateDevice(DWORD_PTR) override { ReleaseSurfaces(); return S_OK; }
    STDMETHODIMP GetSurface(DWORD_PTR, DWORD index, DWORD, IDirect3DSurface9** surf) override
    {
        if (!surf) return E_POINTER;
        *surf = nullptr;
        if (!m_surfaces || index >= m_count) return E_FAIL;
        *surf = m_surfaces[index];
        if (*surf) (*surf)->AddRef();
        return S_OK;
    }
    STDMETHODIMP InitializeDevice(DWORD_PTR, VMR9AllocationInfo* info, DWORD* count) override
    {
        if (!info || !count || *count == 0) return E_POINTER;
        InterlockedIncrement(&m_initCalls);
        printf("[vmr9-alloc] InitializeDevice: w=%lu h=%lu fmt=%lu(0x%lX) flags=0x%lX pool=%lu bufMin=%lu count=%lu\n",
               (unsigned long)info->dwWidth, (unsigned long)info->dwHeight,
               (unsigned long)info->Format, (unsigned long)info->Format,
               (unsigned long)info->dwFlags, (unsigned long)info->Pool,
               (unsigned long)info->MinBuffers, (unsigned long)*count);
        ReleaseSurfaces();
        const DWORD want = *count;
        m_surfaces = new IDirect3DSurface9*[want];
        ZeroMemory(m_surfaces, sizeof(IDirect3DSurface9*) * want);
        m_count = want;
        if (!m_notify) { m_allocHr = E_FAIL; return m_allocHr; }
        m_allocHr = m_notify->AllocateSurfaceHelper(info, count, m_surfaces);
        m_count = *count;
        printf("[vmr9-alloc] AllocateSurfaceHelper hr=0x%08X count=%lu\n",
               (unsigned)m_allocHr, (unsigned long)m_count);
        return m_allocHr;
    }
    STDMETHODIMP StartPresenting(DWORD_PTR) override { InterlockedIncrement(&m_started); return S_OK; }
    STDMETHODIMP StopPresenting(DWORD_PTR) override { return S_OK; }
    STDMETHODIMP PresentImage(DWORD_PTR, VMR9PresentationInfo*) override
    {
        InterlockedIncrement(&m_present);
        return S_OK;
    }

private:
    void ReleaseSurfaces()
    {
        if (m_surfaces)
        {
            for (DWORD i = 0; i < m_count; ++i)
                if (m_surfaces[i]) m_surfaces[i]->Release();
            delete[] m_surfaces;
            m_surfaces = nullptr;
        }
        m_count = 0;
    }

    LONG m_ref;
    volatile LONG m_initCalls = 0;
    IVMRSurfaceAllocatorNotify9* m_notify;
    IDirect3D9Ex* m_d3dEx;
    IDirect3DDevice9* m_dev;
    IDirect3DSurface9** m_surfaces;
    DWORD m_count;
    volatile LONG m_present;
    volatile LONG m_started;
    HRESULT m_deviceHr;
    HRESULT m_allocHr;
};

// Decisive control experiment (Lead request): a plain local video file as the graph source
// into a real VMR9 renderless renderer with our own IVMRSurfaceAllocator9. If THIS graph
// cannot InitializeDevice/Commit either, then VMR9 renderless surface allocation is broken
// on this machine (virtual display / remote stack), independent of our filter.
static void RunVmr9FileTest(const std::wstring& path)
{
    printf("[vmr9-file] source: %s\n", Narrow(path).c_str());
    IGraphBuilder* graph = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IGraphBuilder, (void**)&graph)) || !graph)
    {
        Check(false, "file graph: CLSID_FilterGraph created");
        return;
    }
    IBaseFilter* src = nullptr;
    HRESULT hr = graph->AddSourceFilter(path.c_str(), L"FileSource", &src);
    Check(SUCCEEDED(hr) && src != nullptr, "file graph: AddSourceFilter(video file)");
    if (!src) { graph->Release(); return; }

    IBaseFilter* vmr = nullptr;
    if (FAILED(CoCreateInstance(CLSID_VideoMixingRenderer9, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IBaseFilter, (void**)&vmr)) || !vmr)
    {
        Check(false, "file graph: VideoMixingRenderer9 created");
        src->Release(); graph->Release(); return;
    }
    IVMRFilterConfig9* cfg = nullptr;
    if (SUCCEEDED(vmr->QueryInterface(IID_IVMRFilterConfig9, (void**)&cfg)) && cfg)
    {
        const HRESULT hr1 = cfg->SetNumberOfStreams(1);
        const HRESULT hr2 = cfg->SetRenderingMode(4 /* VMR9Mode_Renderless */);
        Check(SUCCEEDED(hr1) && SUCCEEDED(hr2), "file graph: VMR9 renderless mode set");
        cfg->Release();
    }
    Check(SUCCEEDED(graph->AddFilter(vmr, L"Renderer: VMR9")), "file graph: VMR9 added");

    CVmr9SurfaceAllocator* alloc = new CVmr9SurfaceAllocator();
    IVMRSurfaceAllocatorNotify9* notify = nullptr;
    if (SUCCEEDED(vmr->QueryInterface(IID_IVMRSurfaceAllocatorNotify9, (void**)&notify)) && notify)
    {
        const HRESULT ha = notify->AdviseSurfaceAllocator(0, alloc);
        const HRESULT hb = alloc->AdviseNotify(notify);
        printf("[vmr9-file] AdviseSurfaceAllocator hr=0x%08X, allocator device hr=0x%08X\n",
               (unsigned)ha, (unsigned)hb);
        Check(SUCCEEDED(ha) && SUCCEEDED(hb), "file graph: custom surface allocator advised (D3D device created)");
        notify->Release();
    }

    ICaptureGraphBuilder2* cgb = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_CaptureGraphBuilder2, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_ICaptureGraphBuilder2, (void**)&cgb)) && cgb)
    {
        cgb->SetFiltergraph(graph);
        const HRESULT hc = cgb->RenderStream(nullptr, nullptr, src, nullptr, vmr);
        Check(SUCCEEDED(hc), "file graph: RenderStream(file source -> VMR9 renderless)");
        cgb->Release();
    }

    IMediaControl* mc = nullptr;
    if (SUCCEEDED(graph->QueryInterface(IID_IMediaControl, (void**)&mc)) && mc)
    {
        const HRESULT hrRun = mc->Run();
        printf("[vmr9-file] Run hr=0x%08X\n", (unsigned)hrRun);
        Sleep(3000);
        printf("[vmr9-file] InitializeDevice hr=0x%08X, StartPresenting=%d, PresentImage=%d\n",
               (unsigned)alloc->AllocHr(), alloc->StartedCount(), alloc->PresentCount());
        Check(alloc->AllocHr() != E_FAIL && alloc->AllocHr() != E_OUTOFMEMORY,
              "file source: VMR9 renderless InitializeDevice did not fail");
        Check(alloc->PresentCount() > 0, "file source: VMR9 renderless actually presented frames");
        mc->Stop();
        mc->Release();
    }
    alloc->Release();
    vmr->Release();
    src->Release();
    graph->Release();
}

static void RunVmr9Test()
{
    IGraphBuilder* graph = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FilterGraph, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IGraphBuilder, (void**)&graph)) || !graph)
    {
        Check(false, "VMR9 graph: CLSID_FilterGraph created");
        return;
    }
    IBaseFilter* vcam = CreateFilter();
    Check(vcam != nullptr, "VMR9 graph: vcam filter instance created");
    if (!vcam) { graph->Release(); return; }
    Check(SUCCEEDED(graph->AddFilter(vcam, L"YiouCamera Vcam")), "VMR9 graph: vcam added");

    IBaseFilter* vmr = nullptr;
    if (FAILED(CoCreateInstance(CLSID_VideoMixingRenderer9, nullptr, CLSCTX_INPROC_SERVER,
                                IID_IBaseFilter, (void**)&vmr)) || !vmr)
    {
        Check(false, "VMR9 graph: VideoMixingRenderer9 created");
        vcam->Release(); graph->Release(); return;
    }
    Check(SUCCEEDED(graph->AddFilter(vmr, L"Renderer: VMR9")), "VMR9 graph: renderer added");

    CVmr9SurfaceAllocator* alloc = new CVmr9SurfaceAllocator();
    if (g_vmr9Mode == 2)
    {
        IVMRFilterConfig9* cfg = nullptr;
        if (SUCCEEDED(vmr->QueryInterface(IID_IVMRFilterConfig9, (void**)&cfg)) && cfg)
        {
            const HRESULT hr1 = cfg->SetNumberOfStreams(1);
            const HRESULT hr2 = cfg->SetRenderingMode(4 /* VMR9Mode_Renderless */);
            Check(SUCCEEDED(hr1) && SUCCEEDED(hr2), "VMR9 renderless mode set (SetRenderingMode(4))");
            cfg->Release();
        }
        // Same contract WPFMediaKit uses: a custom IVMRSurfaceAllocator9/IVMRImagePresenter9.
        IVMRSurfaceAllocatorNotify9* notify = nullptr;
        if (SUCCEEDED(vmr->QueryInterface(IID_IVMRSurfaceAllocatorNotify9, (void**)&notify)) && notify)
        {
            const HRESULT ha = notify->AdviseSurfaceAllocator(0, alloc);
            const HRESULT hb = alloc->AdviseNotify(notify);
            printf("[vmr9] AdviseSurfaceAllocator hr=0x%08X, D3D device hr=0x%08X\n",
                   (unsigned)ha, (unsigned)hb);
            Check(SUCCEEDED(ha) && SUCCEEDED(hb), "VMR9 renderless: custom surface allocator advised");
            notify->Release();
        }
    }

    ICaptureGraphBuilder2* cgb = nullptr;
    if (SUCCEEDED(CoCreateInstance(CLSID_CaptureGraphBuilder2, nullptr, CLSCTX_INPROC_SERVER,
                                   IID_ICaptureGraphBuilder2, (void**)&cgb)) && cgb)
    {
        cgb->SetFiltergraph(graph);
        const HRESULT hr = cgb->RenderStream(&PIN_CATEGORY_PREVIEW, &MEDIATYPE_Video,
                                             vcam, nullptr, vmr);
        Check(SUCCEEDED(hr), "VMR9 graph: RenderStream(PinCategory.Preview) connected");
        cgb->Release();
    }

    IMediaControl* mc = nullptr;
    if (SUCCEEDED(graph->QueryInterface(IID_IMediaControl, (void**)&mc)) && mc)
    {
        const int before = ReadShmReadSeq();
        const HRESULT hr = mc->Run();
        Check(SUCCEEDED(hr), "VMR9 graph: IMediaControl::Run");
        Sleep(2500);
        const int after = ReadShmReadSeq();
        printf("[vmr9] mode=%d readSeq %d -> %d (delta=%d)\n", g_vmr9Mode, before, after, after - before);
        printf("[vmr9] allocator: InitializeDevice hr=0x%08X, StartPresenting=%d, PresentImage=%d\n",
               (unsigned)alloc->AllocHr(), alloc->StartedCount(), alloc->PresentCount());
        Check(before >= 0 && after > before, "VMR9 graph: filter really consumed SHM frames");
        mc->Stop();
        mc->Release();
    }
    alloc->Release();
    vmr->Release();
    vcam->Release();
    graph->Release();
}

int wmain(int argc, wchar_t** argv)
{
    for (int i = 1; i < argc; ++i)
    {
        if (_wcsicmp(argv[i], L"--producer") == 0) g_expectProducer = true;
        else if (_wcsicmp(argv[i], L"--orient") == 0) { g_orientCheck = true; g_expectProducer = true; }
        else if (_wcsicmp(argv[i], L"--vmr9-windowed") == 0) g_vmr9Mode = 1;
        else if (_wcsicmp(argv[i], L"--vmr9-renderless") == 0) g_vmr9Mode = 2;
        else if (_wcsicmp(argv[i], L"--vmr9-file") == 0 && i + 1 < argc)
        {
            g_vmr9Mode = 3;
            g_vmr9FilePath = argv[++i];
        }
        else if (_wcsicmp(argv[i], L"--help") == 0 || _wcsicmp(argv[i], L"-h") == 0)
        {
            printf("usage: testhost.exe [--producer] [--orient] [--vmr9-windowed|--vmr9-renderless]\n"
                   "                   [--vmr9-file <video-file>]\n"
                   "  --producer  expect shm_producer.exe running (producer content)\n"
                   "  --orient    orientation probe: shm_producer.exe orient (top=red, rest=blue)\n"
                   "  --vmr9-*    build a real VMR9 graph and check frames travel (needs windowed desktop)\n"
                   "  --vmr9-file control experiment: plain video file -> VMR9 renderless\n");
            return 0;
        }
        else { printf("unknown argument: %ls\n", argv[i]); return 2; }
    }

    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);

    g_producerPresent = DetectShmProducer();
    printf("[mode] shm producer present=%s, expectation=%s\n",
           g_producerPresent ? "yes" : "no", g_expectProducer ? "producer" : "no producer");
    if (g_vmr9Mode == 3)
    {
        // file-source control experiment does not care about SHM at all
    }
    else if (g_expectProducer && !g_producerPresent)
    {
        printf("[FAIL] --producer given but Local\\YoCameraVcamFrames does not exist; start shm_producer.exe first\n");
        CoUninitialize();
        return 2;
    }
    else if (!g_expectProducer && g_producerPresent)
    {
        printf("[FAIL] a frame producer is running; stop shm_producer.exe or pass --producer\n");
        CoUninitialize();
        return 2;
    }

    IBaseFilter* filter = nullptr;
    TestEnumeration(&filter);
    if (!filter)
    {
        printf("RESULT: %d/%d checks failed\n", g_failures, g_checks);
        CoUninitialize();
        return g_failures ? g_failures : 1;
    }
    TestCapsAndFormat(filter);

    if (g_vmr9Mode)
    {
        if (g_vmr9Mode == 3) RunVmr9FileTest(g_vmr9FilePath);
        else RunVmr9Test();
        filter->Release();
        CoUninitialize();
        printf("\nRESULT: %d checks, %d failed\n", g_checks, g_failures);
        return g_failures;
    }

    // ---- main 60-frame streaming phase (720p default, capture pin) ---------
    IPin* pin = GetPinByCategory(filter, PIN_CATEGORY_CAPTURE);
    FrameStats* st = new FrameStats();
    CSinkPin* sink = new CSinkPin(st);
    if (!pin)
    {
        Check(false, "capture pin available for streaming");
        goto cleanup;
    }
    {
        HRESULT hr = pin->Connect(static_cast<IPin*>(sink), nullptr);
        Check(SUCCEEDED(hr), "Connect(output pin -> renderer-like sink) negotiates RGB24");
        AM_MEDIA_TYPE negotiated;
        ZeroMemory(&negotiated, sizeof(negotiated));
        if (SUCCEEDED(pin->ConnectionMediaType(&negotiated)))
        {
            const VIDEOINFOHEADER* vih = (const VIDEOINFOHEADER*)negotiated.pbFormat;
            printf("[conn] negotiated %ldx%ld @ %.3f fps\n", vih->bmiHeader.biWidth, vih->bmiHeader.biHeight,
                   vih->AvgTimePerFrame ? 10000000.0 / (double)vih->AvgTimePerFrame : 0.0);
            FreeMediaType(negotiated);
        }

        const ULONGLONG tPause = NowMs();
        filter->Pause();
        const bool prerollOk = WaitForSamples(st, 1, 1500);
        const ULONGLONG prerollLatency = st->samples.load() > 0 ? (st->firstSampleMs - tPause) : 0;
        printf("[run:preroll] first preroll sample after %llums\n", prerollLatency);
        Check(prerollOk && prerollLatency < 1500, "Pause deliver a preroll frame in < 1.5 s (C14)");

        st->Reset();
        const ULONGLONG tRun = NowMs();
        filter->Run(0);
        const bool got60 = WaitForSamples(st, 60, 3000);
        const ULONGLONG runFirstLatency = st->samples.load() > 0 ? (st->firstSampleMs - tRun) : 0;
        const ULONGLONG runElapsed = NowMs() - tRun;
        printf("[run:1] 60 frames in %llums, first frame after %llums, distinct=%zu\n",
               runElapsed, runFirstLatency, st->fingerprints.size());
        Check(got60, "60 frames delivered (60/60 at 30 fps)");
        Check(runElapsed <= 2600, "60 frames took <= 2.6 s (>~28 fps)");
        Check(runFirstLatency < 1500, "Run delivers the first frame in < 1.5 s (C14 / MediaOpened proxy)");
        Check(st->ptsRegressions.load() == 0, "presentation timestamps are monotonic");
        filter->Stop();

        if (g_orientCheck)
        {
            printf("[orient] buffer row 0 (bottom)=(%u,%u,%u) buffer row h-1 (top)=(%u,%u,%u)\n",
                   st->firstPixel[0], st->firstPixel[1], st->firstPixel[2],
                   st->topPixel[0], st->topPixel[1], st->topPixel[2]);
            // shm_producer orient mode: top 100 SHM rows RED (BGR 0,0,255), rest BLUE (255,0,0).
            // The filter flips top-down SHM rows into the bottom-up DirectShow buffer, so the
            // BOTTOM of the image must arrive in buffer row 0 and the TOP in row h-1.
            Check(st->firstPixel[0] > 200 && st->firstPixel[2] < 70,
                  "orientation: image is upright (bottom of frame = BLUE)");
            Check(st->topPixel[2] > 200 && st->topPixel[0] < 70,
                  "orientation: image is upright (top of frame = RED)");
        }
        else if (g_expectProducer) CheckProducerContent(st, "main");
        else CheckPlaceholderContent(st, "main");
    }

    // ---- Stop -> Run again on the SAME connection (C13) --------------------
    {
        st->Reset();
        const ULONGLONG t0 = NowMs();
        filter->Run(0);
        const bool ok = WaitForSamples(st, 30, 1500);
        printf("[run:stop-run] %d frames in %llums\n", st->samples.load(), NowMs() - t0);
        Check(ok, "Stop -> Run on the same connection keeps delivering frames (C13, no residue)");
        filter->Stop();
    }

    // ---- Stop -> Disconnect -> Connect -> Run (graph rebuild) --------------
    {
        filter->Stop();
        pin->Disconnect();
        static_cast<IPin*>(sink)->Disconnect();
        HRESULT hr = pin->Connect(static_cast<IPin*>(sink), nullptr);
        Check(SUCCEEDED(hr), "reconnect after Disconnect succeeds");
        st->Reset();
        const ULONGLONG t0 = NowMs();
        filter->Run(0);
        const bool ok = WaitForSamples(st, 30, 1500);
        printf("[run:rebuild] %d frames in %llums\n", st->samples.load(), NowMs() - t0);
        Check(ok, "Stop -> rebuild -> Run delivers frames (C13)");
        filter->Stop();
    }

    // ---- 3x repeated rebuild, quick cycles --------------------------------
    {
        bool allOk = true;
        for (int i = 0; i < 3; ++i)
        {
            pin->Disconnect();
            static_cast<IPin*>(sink)->Disconnect();
            if (FAILED(pin->Connect(static_cast<IPin*>(sink), nullptr))) { allOk = false; break; }
            st->Reset();
            filter->Run(0);
            if (!WaitForSamples(st, 15, 1200)) allOk = false;
            filter->Stop();
            printf("[run:cycle%d] %d frames\n", i + 1, st->samples.load());
        }
        Check(allOk, "3x repeated Stop/rebuild cycles keep working (C13)");
    }

    // ---- Preview pin only: exactly the WPFMediaKit RenderStream path ------
    // WPFMediaKit.VideoCapturePlayer.SetupGraph() renders
    //   RenderStream(PinCategory.Preview, MediaType.Video, filter, null, renderer)
    // so a filter without a Preview pin fails with VFW_E_CANNOT_CONNECT.
    {
        IPin* previewPin = GetPinByCategory(filter, PIN_CATEGORY_PREVIEW);
        Check(previewPin != nullptr, "PIN_CATEGORY_PREVIEW pin available for RenderStream(Preview)");
        if (previewPin)
        {
            filter->Stop();
            pin->Disconnect();
            static_cast<IPin*>(sink)->Disconnect();

            FrameStats* stPrev = new FrameStats();
            CSinkPin* sinkPrev = new CSinkPin(stPrev);
            HRESULT hr = previewPin->Connect(static_cast<IPin*>(sinkPrev), nullptr);
            Check(SUCCEEDED(hr), "preview pin connects to a renderer-like sink (WPFMediaKit path)");
            if (SUCCEEDED(hr))
            {
                stPrev->Reset();
                const ULONGLONG tRun = NowMs();
                filter->Run(0);
                const bool ok = WaitForSamples(stPrev, 60, 3000);
                const ULONGLONG firstLatency = stPrev->samples.load() > 0 ? (stPrev->firstSampleMs - tRun) : 9999;
                printf("[run:preview] 60 frames in %llums (first %llums), distinct=%zu grayFraction=%.2f\n",
                       NowMs() - tRun, firstLatency, stPrev->fingerprints.size(), stPrev->grayFraction);
                Check(ok, "Preview pin delivers 60/60 frames at 30 fps");
                Check(firstLatency < 1500, "Preview pin first frame in < 1.5 s (MediaOpened proxy)");
                Check(stPrev->ptsRegressions.load() == 0, "preview pin timestamps are monotonic");
                if (!g_orientCheck)
                {
                    if (g_expectProducer) CheckProducerContent(stPrev, "preview");
                    else CheckPlaceholderContent(stPrev, "preview");
                }
                filter->Stop();
                previewPin->Disconnect();
            }
            static_cast<IPin*>(sinkPrev)->Disconnect();
            sinkPrev->Release();
            delete stPrev;
            previewPin->Release();
        }
        // reconnect the capture pin for the remaining phases
        HRESULT hr2 = pin->Connect(static_cast<IPin*>(sink), nullptr);
        Check(SUCCEEDED(hr2), "capture pin reconnects after the preview-only phase");
    }

    // ---- two instances at once (shared SHM frame source) ------------------
    {
        IBaseFilter* f2 = CreateFilter();
        Check(f2 != nullptr, "second filter instance can be created");
        if (f2)
        {
            IPin* p2 = GetPinByCategory(f2, PIN_CATEGORY_CAPTURE);
            FrameStats* st2 = new FrameStats();
            CSinkPin* sink2 = new CSinkPin(st2);
            if (!p2 || FAILED(p2->Connect(static_cast<IPin*>(sink2), nullptr)))
            {
                Check(false, "second instance connects");
            }
            else
            {
                // start both instances at the same time, fresh counters
                st->Reset();
                st2->Reset();
                filter->Run(0);
                f2->Run(0);
                const bool ok1 = WaitForSamples(st, 30, 1800);
                const bool ok2 = WaitForSamples(st2, 30, 1800);
                printf("[run:two] instance1=%d frames instance2=%d frames\n",
                       st->samples.load(), st2->samples.load());
                Check(ok1 && ok2, "two filter instances stream at the same time (multi-instance)");
                if (g_expectProducer)
                {
                    // both must show the producer's color bars, i.e. the same SHM source
                    printf("[run:two] instance1 grayFraction=%.3f instance2 grayFraction=%.3f\n",
                           st->grayFraction, st2->grayFraction);
                    Check(st->grayFraction >= 0.0 && st->grayFraction < 0.60 &&
                          st2->grayFraction >= 0.0 && st2->grayFraction < 0.60,
                          "both instances consume the same shared-memory frame source");
                }
                filter->Stop();
                f2->Stop();
            }
            if (p2) p2->Release();
            f2->Release();      // destroys the filter, which releases its sink reference
            sink2->Release();   // our reference -> deletes the sink
            delete st2;
        }
    }

cleanup:
    if (pin) pin->Release();
    filter->Stop();
    filter->Release();      // destroys the filter, which releases its sink reference
    if (sink) sink->Release();
    delete st;

    CoUninitialize();
    printf("\nRESULT: %d checks, %d failed\n", g_checks, g_failures);
    return g_failures;
}
