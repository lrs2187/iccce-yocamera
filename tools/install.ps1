# 安装插件到 ICCCE 宿主（camera provider 模式：只复制 3 个文件，无任何系统级注册）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -HostDir D:\ICCCE-Patched
param(
    [Parameter(Mandatory = $true)][string]$HostDir,
    [string]$PluginId = "com.yiou.boothcam"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src = Join-Path $root "build\out\plugin"

if (-not (Test-Path (Join-Path $HostDir "InkCanvasForClass.exe"))) { throw "HostDir 无效（找不到 InkCanvasForClass.exe）: $HostDir" }
if (-not (Test-Path (Join-Path $src "manifest.json"))) { throw "未找到构建产物，请先跑 tools\build-all.ps1" }

Write-Host "=== 0/3 关闭宿主（文件会被占用）===" -ForegroundColor Cyan
Get-Process -Name "InkCanvasForClass" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "=== 1/3 复制插件 ===" -ForegroundColor Cyan
$dst = Join-Path $HostDir "Plugins\$PluginId"
New-Item -ItemType Directory -Force $dst | Out-Null
foreach ($f in @("manifest.json", "YiouCamera.Plugin.dll", "YiouCamera.Plugin.deps.json")) {
    Copy-Item (Join-Path $src $f) $dst -Force
}
Get-ChildItem $dst -File | Select-Object Name, Length | Format-Table -AutoSize

Write-Host "=== 2/3 校验宿主是否带 camera provider（API >= 1.14.0）===" -ForegroundColor Cyan
$sdkXml = Join-Path $HostDir "InkCanvas.PluginSdk.xml"
$hasProvider = $false
if (Test-Path $sdkXml) { $hasProvider = (Select-String -Path $sdkXml -Pattern "ICameraProviderService" -Quiet) }
if ($hasProvider) { Write-Host "  [OK] 宿主包含 camera provider 接口" -ForegroundColor Green }
else { Write-Warning "  未在宿主里检测到 camera provider 接口。若这是官方原版宿主，插件能加载但不会工作（日志会明确提示）。" }

Write-Host "=== 3/3 校验系统无虚拟摄像头残留 ===" -ForegroundColor Cyan
$clsid = "HKCU:\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}"
if (Test-Path $clsid) { Write-Warning "  检测到旧版虚拟摄像头注册，建议清理：见 docs\HANDOFF.md" }
else { Write-Host "  [OK] 无虚拟摄像头注册（provider 模式本就不需要）" -ForegroundColor Green }

Write-Host "`n[DONE] 插件已安装到：$dst" -ForegroundColor Green
Write-Host "启动宿主 → 浮动栏点「驿欧展台」，或视频展台摄像头下拉框选「驿欧无线展台（插件）」"
