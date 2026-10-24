// shm_producer.cpp - standalone producer that publishes frames into the named shared
// memory ring consumed by vcam_filter.dll. Stands in for the real .NET/FFmpeg producer
// so the cross-process frame contract can be tested end to end.
//
// Usage: shm_producer.exe [width height fps seconds]
//        shm_producer.exe 1920 1080 30 20
//        shm_producer.exe orient      -> orientation probe: top 100 rows RED, rest BLUE
//                                        (RGB24/BGR, top-down, 1280x720, 30 fps, 20 s)
//
// Frame row order: TOP-DOWN (row 0 = top of the image), per docs/CONTRACTS.md section 1.
// The filter is responsible for flipping into DirectShow's bottom-up RGB24 buffer.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <vector>

#include "vcam_shm.h"

// Tests may use an isolated mapping (YIOU_VCAM_SHM) so they do not fight a real producer.
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

int wmain(int argc, wchar_t** argv)
{
    bool orientationMode = false;
    int w = 1920, h = 1080, fps = 30, seconds = 20;
    if (argc > 1 && _wcsicmp(argv[1], L"orient") == 0)
    {
        orientationMode = true;
        w = 1280; h = 720; fps = 30; seconds = 20;
        if (argc > 2) seconds = _wtoi(argv[2]);
    }
    else
    {
        if (argc > 1) w = _wtoi(argv[1]);
        if (argc > 2) h = _wtoi(argv[2]);
        if (argc > 3) fps = _wtoi(argv[3]);
        if (argc > 4) seconds = _wtoi(argv[4]);
    }
    if (w < 16 || h < 16 || fps < 1 || fps > 120) { printf("bad args\n"); return 1; }

    const int stride = (w * 3 + 3) & ~3;
    const uint32_t slotBytes = (uint32_t)stride * (uint32_t)h;
    const uint32_t slotCount = VCAM_SHM_SLOTS;
    const size_t total = VCAM_SHM_HEADER_BYTES + (size_t)slotCount * slotBytes;

    HANDLE hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                     (DWORD)(total >> 32), (DWORD)(total & 0xFFFFFFFFu), ShmName());
    if (!hMap)
    {
        printf("CreateFileMapping failed: %lu\n", GetLastError());
        return 1;
    }
    BYTE* view = (BYTE*)MapViewOfFile(hMap, FILE_MAP_ALL_ACCESS, 0, 0, total);
    if (!view)
    {
        printf("MapViewOfFile failed: %lu\n", GetLastError());
        CloseHandle(hMap);
        return 1;
    }

    VcamShmHeader* hdr = (VcamShmHeader*)view;
    ZeroMemory(hdr, sizeof(*hdr));
    hdr->magic = VCAM_SHM_MAGIC;
    hdr->version = VCAM_SHM_VERSION;
    hdr->headerBytes = VCAM_SHM_HEADER_BYTES;
    hdr->slotCount = slotCount;
    hdr->slotBytes = slotBytes;
    hdr->width = (uint32_t)w;
    hdr->height = (uint32_t)h;
    hdr->stride = (uint32_t)stride;
    hdr->format = VCAM_FMT_RGB24;
    hdr->fpsNum = (uint32_t)fps;
    hdr->fpsDen = 1;
    hdr->writeSeq = 0;
    hdr->readSeq = 0;
    hdr->timestamp100ns = 0;
    hdr->frameIndex = 0;
    hdr->flags = 1;   // producer alive

    printf("producer: %s %dx%d @%d fps, %u slots x %u bytes = %.1f MB, mapping '%ls'\n",
           orientationMode ? "ORIENTATION(top=red,rest=blue)" : "bars",
           w, h, fps, slotCount, slotBytes, total / 1048576.0, ShmName());
    printf("producer: running %d s (Ctrl+C to stop)\n", seconds);

    // one reusable bar template, then a moving marker per frame
    std::vector<BYTE> tpl((size_t)stride * h, 0);
    if (orientationMode)
    {
        // top 100 rows RED (BGR: 0,0,255), everything else BLUE (BGR: 255,0,0)
        for (int y = 0; y < h; ++y)
        {
            BYTE* row = &tpl[(size_t)y * stride];
            const BYTE b = (y < 100) ? 0 : 255;
            const BYTE r = (y < 100) ? 255 : 0;
            for (int x = 0; x < w; ++x) { row[x * 3 + 0] = b; row[x * 3 + 1] = 0; row[x * 3 + 2] = r; }
        }
    }
    else
    {
        for (int b = 0; b < 8; ++b)
        {
            BYTE c[3] = { 0, 0, 0 };
            switch (b)
            {
            case 0: c[0] = 250; c[1] = 250; c[2] = 250; break;   // distinct from the filter's own placeholder
            case 1: c[0] = 0;   c[1] = 235; c[2] = 235; break;
            case 2: c[0] = 235; c[1] = 235; c[2] = 0;   break;
            case 3: c[0] = 0;   c[1] = 235; c[2] = 0;   break;
            case 4: c[0] = 235; c[1] = 0;   c[2] = 235; break;
            case 5: c[0] = 0;   c[1] = 0;   c[2] = 235; break;
            case 6: c[0] = 235; c[1] = 0;   c[2] = 0;   break;
            default: c[0] = 16; c[1] = 16; c[2] = 16;   break;
            }
            const int x0 = b * w / 8, x1 = (b + 1) * w / 8;
            for (int y = 0; y < h; ++y)
            {
                BYTE* row = &tpl[(size_t)y * stride];
                for (int x = x0; x < x1; ++x) { row[x * 3 + 0] = c[0]; row[x * 3 + 1] = c[1]; row[x * 3 + 2] = c[2]; }
            }
        }
    }

    LARGE_INTEGER freq, start, now;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&start);
    now = start;
    const LONGLONG interval = 10000000LL / fps;
    LONGLONG nextTick = 0;
    UINT64 frame = 0;
    const UINT64 totalFrames = (UINT64)seconds * fps;

    while (frame < totalFrames)
    {
        LONGLONG elapsed100ns = (now.QuadPart - start.QuadPart) * 10000000LL / freq.QuadPart;
        nextTick = (LONGLONG)(frame * interval);
        if (elapsed100ns < nextTick) { Sleep(1); QueryPerformanceCounter(&now); continue; }
        QueryPerformanceCounter(&now);

        const uint32_t slot = (uint32_t)(frame % slotCount);
        BYTE* dst = view + VCAM_SHM_HEADER_BYTES + (size_t)slot * slotBytes;
        memcpy(dst, tpl.data(), (size_t)stride * h);

        if (!orientationMode)
        {
            int size = h / 8; if (size < 8) size = 8;
            const int span = w > size ? w - size : 1;
            const int x = (int)((frame * 13u) % (UINT32)span);
            const int y0 = (h - size) / 2;
            for (int y = y0; y < y0 + size && y < h; ++y)
                memset(dst + (size_t)y * stride + x * 3, 0xFF, (size_t)size * 3);
        }

        hdr->frameIndex = frame;
        hdr->timestamp100ns = elapsed100ns;
        InterlockedIncrement((LONG*)&hdr->writeSeq);   // odd  -> frame is being published
        InterlockedIncrement((LONG*)&hdr->writeSeq);   // even -> frame is stable
        frame++;
    }

    hdr->flags = 0;
    printf("producer: published %llu frames, exiting\n", (unsigned long long)frame);
    UnmapViewOfFile(view);
    CloseHandle(hMap);
    return 0;
}
