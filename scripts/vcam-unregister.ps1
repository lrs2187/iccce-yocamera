# vcam-unregister.ps1 - removes the YiouCamera virtual camera (per-user, no UAC).
#
# Removes the CLSID key and the CLSID_VideoInputDeviceCategory Instance key - the latter
# also removes the DevicePath value - then verifies that host-style enumeration no longer
# sees the device.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\scripts\vcam-unregister.ps1
#   powershell -ExecutionPolicy Bypass -File .\scripts\vcam-unregister.ps1 -Dll C:\path\vcam_filter.dll
param(
    [string]$Dll = '',
    [string]$Name = '驿欧无线展台',
    [string]$DevicePath = 'yocam:yiou-wireless-dc'
)
$ErrorActionPreference = 'Stop'

$CLSID    = '{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}'
$CATEGORY = '{860BB310-5D01-11d0-BD3B-00A0C911CE86}'
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo     = Split-Path -Parent $here
$defaultDeployed = Join-Path $env:LOCALAPPDATA 'ICC-CE\YiouCamera\vcam\0.1.0\vcam_filter.dll'
if (-not $Dll) {
    $Dll = if (Test-Path $defaultDeployed) { $defaultDeployed } else { Join-Path $repo 'build\out\vcam_filter.dll' }
}

$clsKey      = "HKCU:\Software\Classes\CLSID\$CLSID"
$instanceKey = "HKCU:\Software\Classes\CLSID\$CATEGORY\Instance\$CLSID"

# ---- 1) DllInstall(FALSE) removes both keys (HKCU) ----
$regtool = @(
    (Join-Path $repo 'build\out\regtool.exe'),
    (Join-Path (Split-Path -Parent $Dll) 'regtool.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($regtool -and (Test-Path $Dll)) {
    & $regtool uninstall $Dll
    Write-Host "[unregister] regtool uninstall exit=$LASTEXITCODE"
}
else {
    Write-Host "[unregister] regtool/dll not available; removing registry keys directly"
}

# ---- 2) idempotent safety net: delete the keys ourselves ----
if (Test-Path $instanceKey) { Remove-Item -Path $instanceKey -Recurse -Force }
if (Test-Path $clsKey)      { Remove-Item -Path $clsKey -Recurse -Force }

if ((Test-Path $instanceKey) -or (Test-Path $clsKey)) { throw 'unregister verification FAILED: registry keys still present' }
Write-Host "[unregister] HKCU registration removed (including DevicePath)" -ForegroundColor Green

# ---- 3) verify the device is gone from host-style enumeration ----
$verifier = Join-Path $repo 'build\out\verify-enum.exe'
if (Test-Path $verifier) {
    & $verifier $Name $DevicePath | Out-Host
    $rc = $LASTEXITCODE
    if ($rc -eq 0) { throw 'unregister verification FAILED: the virtual camera is still enumerated' }
    Write-Host "[unregister] verified: virtual camera is no longer enumerated (exit=$rc as expected)" -ForegroundColor Green
}
else {
    Write-Host "[unregister] verify-enum.exe not built; skipping enumeration check" -ForegroundColor Yellow
}
exit 0
