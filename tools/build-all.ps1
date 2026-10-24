# 一键构建：Core + Plugin → build\out\plugin\（3 文件：manifest.json / 插件 dll / deps.json）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\build-all.ps1 [-Configuration Release]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$plugOut = Join-Path $root "build\out\plugin"
New-Item -ItemType Directory -Force $plugOut | Out-Null

Write-Host "=== 1/3 构建 Core ===" -ForegroundColor Cyan
Push-Location (Join-Path $root "src\YiouCamera.Core")
dotnet build -c $Configuration -v q
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { throw "Core 构建失败 (exit=$code)" }

Write-Host "=== 2/3 构建 Plugin（Release 下 ILRepack 会把 Core 合并进入口 dll）===" -ForegroundColor Cyan
Push-Location (Join-Path $root "src\YiouCamera.Plugin")
dotnet build -c $Configuration -v q
$code = $LASTEXITCODE
Pop-Location
if ($code -ne 0) { throw "Plugin 构建失败 (exit=$code)" }

Write-Host "=== 3/3 汇总产物 ===" -ForegroundColor Cyan
$bin = Join-Path $root "src\YiouCamera.Plugin\bin\$Configuration\net10.0-windows10.0.19041.0"
$need = @("manifest.json", "YiouCamera.Plugin.dll", "YiouCamera.Plugin.deps.json")
foreach ($f in $need) {
    if (-not (Test-Path (Join-Path $bin $f))) { throw "缺少产物 $f（在 $bin）" }
}
Remove-Item (Join-Path $plugOut "*") -Force -ErrorAction SilentlyContinue
foreach ($f in $need) { Copy-Item (Join-Path $bin $f) $plugOut -Force }

foreach ($f in @("InkCanvas.PluginSdk.dll", "InkCanvas.Controls.dll", "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll")) {
    if (Test-Path (Join-Path $plugOut $f)) { throw "产物里出现不应分发的程序集：$f" }
}
$extra = Get-ChildItem $plugOut -File | Where-Object { $_.Name -notin $need }
if ($extra) { Write-Warning "输出目录出现预期之外的文件：$($extra.Name -join ', ')" }

Write-Host "`n[DONE] 产物：" -ForegroundColor Green
Get-ChildItem $plugOut -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host "下一步：pwsh -File tools\install.ps1 -HostDir <宿主目录>"
