# 打包 patched 宿主为可分发的 zip（已内置插件，解压即用）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\pack-host.ps1 [-HostDir <dir>] [-Name <zip名>]
param(
    [string]$HostDir = "",
    [string]$Name = "",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $HostDir) { $HostDir = Join-Path $root "build\patched-host" }
$outDir = Join-Path $root "build\out"
New-Item -ItemType Directory -Force $outDir | Out-Null

$exe = Join-Path $HostDir "InkCanvasForClass.exe"
if (-not (Test-Path $exe)) { throw "HostDir 无效（找不到 InkCanvasForClass.exe）: $HostDir" }

$vi = (Get-Item $exe).VersionInfo
$hashFile = Join-Path $HostDir "InkCanvasForClass.dll"
$dllSha = if (Test-Path $hashFile) { (Get-FileHash $hashFile -Algorithm SHA256).Hash.ToLowerInvariant() } else { "n/a" }
if (-not $Name) { $Name = "ICCCE-Patched-$($vi.FileVersion)-camera-provider-win-x64" }
$zip = Join-Path $outDir "$Name.zip"

# 运行期状态目录/文件：不打包，保证分发的是干净首次启动
$excludeDirs = @("Configs", "Logs", "PluginLogs", "PluginConfigs", "Saves", "Backups",
                 "Crashes", "PluginMarketCache", "Automations", "FloatingBarThemes")
$excludeFiles = @("device_id.dat", "usage_stats.enc")

Write-Host "=== 1/4 生成干净 staging ===" -ForegroundColor Cyan
$staging = Join-Path $env:TEMP ("iccce_host_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    Get-ChildItem $HostDir -Force | Where-Object {
        ($_.PSIsContainer -and $excludeDirs -notcontains $_.Name) -or
        (-not $_.PSIsContainer -and $excludeFiles -notcontains $_.Name)
    } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $staging $_.Name) -Recurse -Force
    }

    # 附带文档与入口说明
    $docs = @("HANDOFF.md", "release-notes-v0.3.1.md", "release-notes-v0.3.0.md")
    foreach ($d in $docs) {
        $p = Join-Path $root "docs\$d"
        if (Test-Path $p) { Copy-Item $p (Join-Path $staging "docs-$d") -Force }
    }
    $readme = @'
ICCCE（打了 camera provider 补丁）+ 驿欧无线展台插件
=====================================================

快速开始
  1) 双击 InkCanvasForClass.exe（首次会走官方引导，正常走完）
  2) 底部浮动栏点「驿欧展台」→ 自动发现设备并打开视频展台
  3) 也可在「视频展台」摄像头下拉框里选「驿欧无线展台（插件）」

前置条件
  - Windows 10/11 x64 + .NET 10 Desktop Runtime
  - 驿欧无线展台已开机并与电脑在同一网络
  - ffmpeg.exe：优先自动探测（含驿欧白板套件自带的 4.0.1）；
    找不到时把 ffmpeg.exe 放到 Plugins\com.yiou.boothcam\ 下

回滚（零残留）
  关掉程序，删掉本目录即可。不写注册表、不装服务、不装驱动。

详细步骤/排障见 docs-HANDOFF.md
'@
    Set-Content -Path (Join-Path $staging "使用说明.txt") -Value $readme -Encoding UTF8

    # 覆盖 BUILD-INFO，记录打包信息
    $packInfo = @(
        "打包时间      : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')",
        "宿主版本      : $($vi.FileVersion) / $($vi.ProductVersion)",
        "InkCanvasForClass.dll SHA256 : $dllSha",
        "内置插件      : $(if (Test-Path (Join-Path $staging 'Plugins\com.yiou.boothcam\manifest.json')) { 'com.yiou.boothcam（camera provider 模式）' } else { '无' })",
        "说明          : 基于上游 net10 的 camera provider 补丁（见 PR #739）"
    ) -join "`r`n"
    Set-Content -Path (Join-Path $staging "PACKAGE-INFO.txt") -Value $packInfo -Encoding UTF8

    Write-Host "=== 2/4 压缩（约 170MB，需要 1-3 分钟）===" -ForegroundColor Cyan
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -CompressionLevel Optimal

    Write-Host "=== 3/4 计算校验值 ===" -ForegroundColor Cyan
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $Name.zip" | Set-Content (Join-Path $outDir "$Name.zip.sha256") -Encoding ASCII

    Write-Host "=== 4/4 结果 ===" -ForegroundColor Cyan
    $sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "`n[DONE] $zip" -ForegroundColor Green
    Write-Host "  大小   : $sizeMB MB"
    Write-Host "  SHA256 : $hash"
    Write-Host "  文件数 : $((Get-ChildItem $staging -Recurse -File | Measure-Object).Count)"
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
