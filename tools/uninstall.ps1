# 卸载插件（provider 模式零系统残留；同时可清理历史遗留的虚拟摄像头）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\uninstall.ps1 -HostDir D:\ICCCE-Patched [-CleanLegacyVcam]
param(
    [Parameter(Mandatory = $true)][string]$HostDir,
    [string]$PluginId = "com.yiou.boothcam",
    [switch]$CleanLegacyVcam
)
$ErrorActionPreference = "Stop"

Write-Host "=== 1/3 关闭宿主 ===" -ForegroundColor Cyan
Get-Process -Name "InkCanvasForClass" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Write-Host "=== 2/3 删除插件目录 ===" -ForegroundColor Cyan
$dst = Join-Path $HostDir "Plugins\$PluginId"
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force; Write-Host "  已删除 $dst" } else { Write-Host "  （插件目录不存在）" }
$cfg = Join-Path $HostDir "PluginConfigs\$PluginId"
if (Test-Path $cfg) { Remove-Item $cfg -Recurse -Force; Write-Host "  已删除配置 $cfg" }

Write-Host "=== 3/3 清理历史遗留虚拟摄像头（可选）===" -ForegroundColor Cyan
$vcamDir = Join-Path $env:LOCALAPPDATA "ICC-CE\YiouCamera\vcam"
$clsid = "HKCU:\Software\Classes\CLSID\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}"
if ((Test-Path $vcamDir) -or (Test-Path $clsid)) {
    if ($CleanLegacyVcam) {
        Get-ChildItem $vcamDir -Recurse -Filter "vcam_filter.dll" -ErrorAction SilentlyContinue |
            ForEach-Object { & regsvr32.exe /n /s /u /i $_.FullName }
        Start-Sleep -Milliseconds 500
        Remove-Item $vcamDir -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $clsid -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "HKCU:\Software\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance\{9A3E7D21-4C5B-4E8A-9B77-2D146CE05A31}" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  已清理历史虚拟摄像头" -ForegroundColor Green
    } else {
        Write-Warning "  检测到历史虚拟摄像头残留，如需清理请加 -CleanLegacyVcam"
    }
} else {
    Write-Host "  [OK] 无历史残留" -ForegroundColor Green
}

Write-Host "`n[DONE] 卸载完成。provider 模式本就零系统残留，删目录即彻底移除。" -ForegroundColor Green
