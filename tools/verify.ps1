# 校验安装状态：宿主能力、插件文件、插件日志（provider 模式）、系统残留
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1 -HostDir D:\ICCCE-Patched
param(
    [Parameter(Mandatory = $true)][string]$HostDir,
    [string]$PluginId = "com.yiou.boothcam"
)
$ErrorActionPreference = "Stop"
$fail = 0
function Check($cond, $msg) {
    if ($cond) { Write-Host "  [OK]   $msg" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $msg" -ForegroundColor Red; $script:fail++ }
}

Write-Host "=== 1/4 宿主 ===" -ForegroundColor Cyan
$exe = Join-Path $HostDir "InkCanvasForClass.exe"
Check (Test-Path $exe) "宿主存在: $exe"
if (Test-Path $exe) {
    $vi = (Get-Item $exe).VersionInfo
    Write-Host "  FileVersion = $($vi.FileVersion)  ProductVersion = $($vi.ProductVersion)"
}
$dll = Join-Path $HostDir "InkCanvasForClass.dll"
if (Test-Path $dll) {
    $hasProvider = $false
    try {
        $bytes = [System.IO.File]::ReadAllBytes($dll)
        $u = [System.Text.Encoding]::Unicode.GetString($bytes)
        $hasProvider = $u.Contains("ICameraProviderService")
    } catch { }
    Check $hasProvider "宿主二进制包含 camera provider 接口（1.14.0）"
}

Write-Host "=== 2/4 插件文件 ===" -ForegroundColor Cyan
$pd = Join-Path $HostDir "Plugins\$PluginId"
Check (Test-Path (Join-Path $pd "manifest.json")) "manifest.json"
Check (Test-Path (Join-Path $pd "YiouCamera.Plugin.dll")) "YiouCamera.Plugin.dll"
Check (Test-Path (Join-Path $pd "YiouCamera.Plugin.deps.json")) "YiouCamera.Plugin.deps.json"
Check (-not (Test-Path (Join-Path $pd "InkCanvas.PluginSdk.dll"))) "没有混入 InkCanvas.PluginSdk.dll"
if (Test-Path $pd) { Get-ChildItem $pd -File | Select-Object Name, Length | Format-Table -AutoSize }

Write-Host "=== 3/4 系统零残留 ===" -ForegroundColor Cyan
Check (-not (Test-Path "HKCU:\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}")) "无虚拟摄像头 CLSID 注册"
Check (-not (Test-Path "$env:LOCALAPPDATA\ICC-CE\YiouCamera\vcam")) "无虚拟摄像头 DLL 目录"

Write-Host "=== 4/4 插件日志 ===" -ForegroundColor Cyan
$logDir = Join-Path $HostDir "PluginLogs\$PluginId"
if (Test-Path $logDir) {
    $l = Get-ChildItem $logDir -File | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if ($l) {
        $tail = Get-Content $l.FullName -Tail 30
        $tail
        Check (($tail -join "`n") -match "camera provider") "日志中出现 camera provider 接入记录"
    }
} else { Write-Warning "  尚无插件日志（宿主未启动过该插件）" }

if ($fail -ne 0) { Write-Host "`n[FAIL] $fail 项未通过" -ForegroundColor Red; exit 1 }
Write-Host "`n[PASS] 校验全部通过" -ForegroundColor Green
