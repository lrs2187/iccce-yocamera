# vcam-register.ps1 - per-user (no UAC) registration of the YiouCamera virtual camera.
#
# Uses vcam_filter.dll!DllInstall (HKCU\Software\Classes) - exactly the path the ICCCE
# plugin uses - and then VERIFIES:
#   1. the registry Instance key carries FriendlyName / CLSID / DevicePath
#   2. host-style enumeration (ICreateDevEnum + IPropertyBag) sees the device with the
#      expected FriendlyName, a NON-EMPTY and globally UNIQUE DevicePath, and can
#      BindToObject it.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\scripts\vcam-register.ps1
#   powershell -ExecutionPolicy Bypass -File .\scripts\vcam-register.ps1 -Dll C:\path\vcam_filter.dll
#   powershell -ExecutionPolicy Bypass -File .\scripts\vcam-register.ps1 -Name "驿欧无线展台"
param(
    [string]$Dll = '',
    [string]$Name = '驿欧无线展台',
    [string]$DevicePath = 'yocam:yiou-wireless-dc',
    [switch]$SkipVerify
)
$ErrorActionPreference = 'Stop'

$CLSID    = '{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}'
$CATEGORY = '{860BB310-5D01-11d0-BD3B-00A0C911CE86}'   # CLSID_VideoInputDeviceCategory
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo     = Split-Path -Parent $here
$defaultDeployed = Join-Path $env:LOCALAPPDATA 'ICC-CE\YiouCamera\vcam\0.1.0\vcam_filter.dll'
if (-not $Dll) {
    $Dll = if (Test-Path $defaultDeployed) { $defaultDeployed } else { Join-Path $repo 'build\out\vcam_filter.dll' }
}
if (-not (Test-Path $Dll)) { throw "vcam_filter.dll not found: $Dll" }
$Dll = (Resolve-Path $Dll).Path

$clsKey      = "HKCU:\Software\Classes\CLSID\$CLSID"
$instanceKey = "HKCU:\Software\Classes\CLSID\$CATEGORY\Instance\$CLSID"

function Find-RegTool {
    $candidates = @(
        (Join-Path $repo 'build\out\regtool.exe'),
        (Join-Path (Split-Path -Parent $Dll) 'regtool.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

function Write-RegistrationFallback {
    # Used only if DllInstall could not be invoked; keeps the registry layout identical.
    New-Item -Path "$clsKey\InprocServer32" -Force | Out-Null
    Set-ItemProperty -Path $clsKey -Name '(default)' -Value $Name
    Set-ItemProperty -Path "$clsKey\InprocServer32" -Name '(default)' -Value $Dll
    Set-ItemProperty -Path "$clsKey\InprocServer32" -Name 'ThreadingModel' -Value 'Both'
    New-Item -Path $instanceKey -Force | Out-Null
    Set-ItemProperty -Path $instanceKey -Name 'FriendlyName' -Value $Name
    Set-ItemProperty -Path $instanceKey -Name 'CLSID' -Value $CLSID
    Set-ItemProperty -Path $instanceKey -Name 'DevicePath' -Value $DevicePath
}

function Assert-Registration {
    if (-not (Test-Path $instanceKey)) { throw "registration missing: $instanceKey" }
    $inst = Get-ItemProperty -Path $instanceKey
    if ($inst.FriendlyName -ne $Name)       { throw "FriendlyName mismatch: '$($inst.FriendlyName)' != '$Name'" }
    if ($inst.CLSID -ne $CLSID)             { throw "Instance CLSID mismatch: '$($inst.CLSID)'" }
    if ($inst.DevicePath -ne $DevicePath)   { throw "DevicePath mismatch: '$($inst.DevicePath)' != '$DevicePath'" }
    $srv = Get-ItemProperty -Path "$clsKey\InprocServer32"
    if ($srv.'(default)' -ne $Dll)          { throw "InprocServer32 mismatch: '$($srv.'(default)')' != '$Dll'" }
    if ($srv.ThreadingModel -ne 'Both')     { throw "ThreadingModel mismatch: '$($srv.ThreadingModel)'" }
}

# ---- 1) per-user COM + device-category registration (DllInstall, no elevation) ----
$regtool = Find-RegTool
$registered = $false
if ($regtool) {
    & $regtool install $Dll $Name
    $rc = $LASTEXITCODE
    Write-Host "[register] regtool install exit=$rc"
    $registered = ($rc -eq 0)
}
else {
    Write-Warning "[register] regtool.exe not found; using direct HKCU writes"
}
if (-not $registered) {
    Write-Host "[register] falling back to direct HKCU registry writes" -ForegroundColor Yellow
    Write-RegistrationFallback
}

# ---- 2) verify the registry layout (this is where DevicePath lives) ----
try { Assert-Registration }
catch {
    Write-Warning "[register] $($_.Exception.Message); rewriting the registry"
    Write-RegistrationFallback
    Assert-Registration
}
Write-Host "[register] HKCU registration OK: '$Name' -> $Dll" -ForegroundColor Green
Write-Host "[register] DevicePath = $DevicePath (unique marker consumed by DsDevice.DevicePath)"

# ---- 3) verify host-style enumeration: name + non-empty unique DevicePath + BindToObject ----
if (-not $SkipVerify) {
    $verifier = Join-Path $repo 'build\out\verify-enum.exe'
    if (-not (Test-Path $verifier)) {
        $ps1 = Join-Path $repo 'src\YiouCamera.Vcam\verify\verify-enum.ps1'
        if (Test-Path $ps1) { & $ps1 -Name $Name -DevicePath $DevicePath; exit $LASTEXITCODE }
        throw "verify-enum.exe not found; build it (build.ps1) or pass -SkipVerify"
    }
    & $verifier $Name $DevicePath
    if ($LASTEXITCODE -ne 0) { throw 'enumeration verification FAILED (name / DevicePath / BindToObject)' }
    Write-Host "[register] enumeration verification PASSED (name + unique DevicePath + BindToObject)" -ForegroundColor Green
}
exit 0
