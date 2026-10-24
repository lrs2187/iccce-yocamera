// regtool.cpp - registration helper / diagnostics for vcam_filter.dll.
//
//   regtool install   <dll> [friendlyName] -> LoadLibrary + DllInstall(TRUE, name)  (per-user HKCU, no elevation)
//   regtool uninstall <dll>                -> LoadLibrary + DllInstall(FALSE)
//   regtool regserver <dll>                -> LoadLibrary + DllRegisterServer  (HKCR, needs elevation)
//   regtool unregserver <dll>              -> LoadLibrary + DllUnregisterServer
//
// DllInstall (not regsvr32) is the shipping path: regsvr32 /i also invokes DllInstall but
// prints a dialog and is harder to script from a plugin. The optional friendlyName is
// forwarded as the DllInstall command line (display-name override / localisation hook).
// Prints the raw HRESULT so a failed elevation is distinguishable from a bug.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>

typedef HRESULT(WINAPI* PFN_DLLINSTALL)(BOOL, LPCWSTR);
typedef HRESULT(WINAPI* PFN_DLLREGSERVER)(void);

static void usage()
{
    printf("usage: regtool install|uninstall|regserver|unregserver <path-to-vcam_filter.dll> [friendlyName]\n");
}

int wmain(int argc, wchar_t** argv)
{
    const bool isInstall = (argc >= 2 && _wcsicmp(argv[1], L"install") == 0);
    const bool isUninstall = (argc >= 2 && _wcsicmp(argv[1], L"uninstall") == 0);
    const bool isReg = (argc >= 2 && _wcsicmp(argv[1], L"regserver") == 0);
    const bool isUnreg = (argc >= 2 && _wcsicmp(argv[1], L"unregserver") == 0);
    if (argc < 3 || !(isInstall || isUninstall || isReg || isUnreg))
    {
        usage();
        return 2;
    }

    HMODULE h = LoadLibraryW(argv[2]);
    if (!h)
    {
        printf("LoadLibrary failed: %lu\n", GetLastError());
        return 3;
    }
    HRESULT hr = E_FAIL;
    if (isInstall || isUninstall)
    {
        PFN_DLLINSTALL pfn = (PFN_DLLINSTALL)GetProcAddress(h, "DllInstall");
        if (!pfn) { printf("DllInstall not exported\n"); FreeLibrary(h); return 4; }
        const wchar_t* cmd = (isInstall && argc > 3) ? argv[3] : L"";
        hr = pfn(isInstall ? TRUE : FALSE, cmd);
    }
    else
    {
        PFN_DLLREGSERVER pfn = (PFN_DLLREGSERVER)GetProcAddress(
            h, isReg ? "DllRegisterServer" : "DllUnregisterServer");
        if (!pfn) { printf("entry point not exported\n"); FreeLibrary(h); return 4; }
        hr = pfn();
    }
    printf("%ls: HRESULT=0x%08X (%s)\n", argv[1], (unsigned)hr, SUCCEEDED(hr) ? "OK" : "FAILED");
    FreeLibrary(h);
    return SUCCEEDED(hr) ? 0 : 1;
}
