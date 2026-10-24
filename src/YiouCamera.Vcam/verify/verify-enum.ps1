# verify-enum.ps1 - compiles and runs the C# device-enumeration verifier (host-style
# DsDevice.GetDevicesOfCat path) including the DevicePath non-empty + uniqueness checks.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\verify\verify-enum.ps1
#   powershell -ExecutionPolicy Bypass -File .\verify\verify-enum.ps1 -Name "驿欧无线展台" -DevicePath yocam:yiou-wireless-dc
param(
    [string]$Name = '',
    [string]$DevicePath = '',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path          # src\YiouCamera.Vcam\verify
$vcam = Split-Path -Parent $here                                  # src\YiouCamera.Vcam
$repo = Split-Path -Parent (Split-Path -Parent $vcam)             # repo root
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
if (-not $Out) { $Out = Join-Path $repo 'build\out\verify-enum.exe' }
$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

& $csc /nologo /platform:x64 /optimize+ "/out:$Out" (Join-Path $here 'verify-enum.cs')
if ($LASTEXITCODE -ne 0) { throw 'csc failed' }

$argv = @()
if ($Name) { $argv += $Name }
if ($DevicePath) { $argv += $DevicePath }
& $Out @argv
exit $LASTEXITCODE
