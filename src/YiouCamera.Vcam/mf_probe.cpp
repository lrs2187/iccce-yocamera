// mf_probe.cpp - experiment: create a Windows 11 Media Foundation virtual camera via
// MFCreateVirtualCamera and check whether it becomes visible to
//   (a) Media Foundation device enumeration (MFEnumDeviceSources), and
//   (b) DirectShow VideoInputDevice enumeration (ICreateDevEnum) - the path ICCCE uses.
//
// The camera is created with MFVirtualCameraLifetime_Session, so it disappears when this
// process exits or Shutdown() is called. It is NOT registered by our filter's registry keys.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <dshow.h>
#include <stdio.h>
#include <string>
#include <vector>

#pragma comment(lib, "mfsensorgroup.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "strmiids.lib")
#pragma comment(lib, "uuid.lib")

static void ListMfCameras()
{
    IMFAttributes* attr = nullptr;
    if (FAILED(MFCreateAttributes(&attr, 1))) return;
    attr->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFEnumDeviceSources(attr, &devices, &count);
    attr->Release();
    if (FAILED(hr)) { printf("[mf] MFEnumDeviceSources failed 0x%08X\n", (unsigned)hr); return; }
    printf("[mf] Media Foundation video capture devices: %u\n", count);
    for (UINT32 i = 0; i < count; ++i)
    {
        WCHAR name[256] = { 0 };
        UINT32 len = 0;
        devices[i]->GetString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, name, 256, &len);
        wprintf(L"[mf]   %u) %s\n", i + 1, name);
        devices[i]->Release();
    }
    CoTaskMemFree(devices);
}

int wmain(int argc, wchar_t** argv)
{
    const int seconds = (argc > 1) ? _wtoi(argv[1]) : 20;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    GuidClsid:
    static const GUID CLSID_ProbeSource =
    { 0x1D2E3F40, 0x5566, 0x7788, { 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x01 } };
    WCHAR sourceId[64] = { 0 };
    StringFromGUID2(CLSID_ProbeSource, sourceId, 64);

    printf("[mf] MFCreateVirtualCamera(name='MF Probe Camera', lifetime=Session, access=CurrentUser)\n");
    IMFVirtualCamera* cam = nullptr;
    HRESULT hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                                       MFVirtualCameraLifetime_Session,
                                       MFVirtualCameraAccess_CurrentUser,
                                       L"MF Probe Camera", sourceId, nullptr, 0, &cam);
    printf("[mf] MFCreateVirtualCamera hr=0x%08X\n", (unsigned)hr);
    if (FAILED(hr) || !cam) { printf("[mf] aborting\n"); MFShutdown(); CoUninitialize(); return 1; }

    hr = cam->Start(nullptr);
    printf("[mf] IMFVirtualCamera::Start hr=0x%08X\n", (unsigned)hr);

    ListMfCameras();

    printf("[mf] staying alive %d s so other processes can enumerate...\n", seconds);
    Sleep((DWORD)seconds * 1000);

    printf("[mf] Shutdown/Remove\n");
    cam->Shutdown();
    cam->Remove();
    cam->Release();
    MFShutdown();
    CoUninitialize();
    return 0;
}
