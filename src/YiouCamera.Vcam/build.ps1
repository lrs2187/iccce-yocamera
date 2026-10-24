# build.ps1 - builds the YiouCamera virtual camera (x64, static CRT, system DLLs only).
#
# Output goes to the REPOSITORY-ROOT build\out\ so tools\build-all.ps1 and the plugin
# packaging scripts have one canonical location:
#
#   build\out\vcam_filter.dll   the DirectShow virtual camera (the shippable artifact)
#   build\out\testhost.exe      C1-C20 regression self-test
#   build\out\shm_producer.exe  shared-memory frame producer (stand-in for Core)
#   build\out\regtool.exe       per-user register/unregister helper
#   build\out\verify-enum.exe   host-style DsDevice enumeration + DevicePath verifier
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\src\YiouCamera.Vcam\build.ps1
#   powershell -ExecutionPolicy Bypass -File .\src\YiouCamera.Vcam\build.ps1 -Clean
#   powershell -ExecutionPolicy Bypass -File .\src\YiouCamera.Vcam\build.ps1 -SkipVerifyTool
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$SkipVerifyTool
)
$ErrorActionPreference = 'Stop'

$vcam = $PSScriptRoot                                              # src\YiouCamera.Vcam (sources are flat here)
$repo = Split-Path -Parent (Split-Path -Parent $vcam)              # repo root
$src  = $vcam
$out  = Join-Path $repo 'build\out'
$obj  = Join-Path $repo 'build\obj'

if ($Clean) {
    foreach ($p in @($out, $obj)) {
        if (Test-Path $p) { Remove-Item -Path $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
New-Item -ItemType Directory -Path $out, $obj -Force | Out-Null

# ---- locate the VS2022 x64 toolchain --------------------------------------
$candidates = @(
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
)
$vcvars = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vcvars) { throw "vcvars64.bat (VS2022 x64) not found; checked: $($candidates -join ', ')" }

# ---- native build (driven through a .cmd so vcvars64 can set the environment) ----
$cmdFile = Join-Path $obj 'build-native.cmd'
$native = @"
@echo off
setlocal
call "$vcvars" >nul
if errorlevel 1 exit /b 1
cd /d "$vcam"
set CL_OPTS=/nologo /EHsc /std:c++17 /O2 /MT /W3 /utf-8 /D_CRT_SECURE_NO_WARNINGS /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00

rem --- the virtual camera filter (shippable artifact) ---
cl %CL_OPTS% /Fo:"$obj\vcam_filter.obj" /Fd:"$obj\vcam_filter.pdb" /Fe:"$out\vcam_filter.dll" /LD "$src\vcam_filter.cpp" /link /DLL /DEF:"$src\vcam_filter.def" /SUBSYSTEM:WINDOWS /INCREMENTAL:NO /MAP:"$obj\vcam_filter.map" /MAPINFO:EXPORTS
if errorlevel 1 exit /b 1

rem --- regression self-test ---
cl %CL_OPTS% /Fo:"$obj\testhost.obj" /Fd:"$obj\testhost.pdb" /Fe:"$out\testhost.exe" "$src\testhost.cpp"
if errorlevel 1 exit /b 1

rem --- shared-memory frame producer (stands in for the .NET Core side) ---
cl %CL_OPTS% /Fo:"$obj\shm_producer.obj" /Fd:"$obj\shm_producer.pdb" /Fe:"$out\shm_producer.exe" "$src\shm_producer.cpp"
if errorlevel 1 exit /b 1

rem --- registration helper ---
cl %CL_OPTS% /Fo:"$obj\regtool.obj" /Fd:"$obj\regtool.pdb" /Fe:"$out\regtool.exe" "$src\regtool.cpp"
if errorlevel 1 exit /b 1
echo NATIVE_BUILD_OK
"@
Set-Content -Path $cmdFile -Value $native -Encoding ASCII
& cmd.exe /c "`"$cmdFile`""
if ($LASTEXITCODE -ne 0) { throw "native build failed (exit code $LASTEXITCODE), see $cmdFile" }

# ---- C# enumeration verifier (same API path as the ICCE host) -------------
if (-not $SkipVerifyTool) {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path $csc) {
        & $csc /nologo /platform:x64 /optimize+ "/out:$out\verify-enum.exe" (Join-Path $vcam 'verify\verify-enum.cs')
        if ($LASTEXITCODE -ne 0) { throw 'verify-enum.cs failed to compile' }
    }
    else {
        Write-Warning "csc.exe not found; skipping verify-enum.exe ($csc)"
    }
}

# ---- report ---------------------------------------------------------------
$artifacts = Get-ChildItem $out -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.dll', '.exe' } | Sort-Object Name
Write-Host "`nBuilt into $out" -ForegroundColor Green
$artifacts | ForEach-Object { Write-Host ("  {0,-20} {1,10:N0} bytes" -f $_.Name, $_.Length) }

$dll = Join-Path $out 'vcam_filter.dll'
if (-not (Test-Path $dll)) { throw "build failed: $dll missing" }
$size = (Get-Item $dll).Length
Write-Host ("`nvcam_filter.dll OK ({0:N0} bytes, static CRT, x64)" -f $size) -ForegroundColor Green
exit 0
