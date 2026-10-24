# 一键回归：验证虚拟摄像头能否在 ICCCE 同款渲染管线下出图
#
# 用法:
#   powershell -ExecutionPolicy Bypass -File run-harness.ps1
#   powershell -ExecutionPolicy Bypass -File run-harness.ps1 -DeviceName "YoCamera Virtual Camera"
#   powershell -ExecutionPolicy Bypass -File run-harness.ps1 -NoProducer   # 用真实 RTSP 会话喂帧时
#
# 判定：harness.log 里出现 "PASS(device-path)" 且 readDelta>0 即通过。
param(
    [string]$DeviceName = "驿欧无线展台",
    [string]$ProducerExe = "C:\Users\lrs2187\Projects\iccce-yocamera\_research\poc-vcam\out\shm_producer.exe",
    [switch]$NoProducer,
    [int]$ScreenshotDelaySec = 8
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "..\..")
$outDir = Join-Path $here "bin\Release\net10.0-windows10.0.19041.0"
$exe = Join-Path $outDir "YiouCamera.Harness.exe"
$log = Join-Path $outDir "harness.log"
$shot = Join-Path $root "_research\harness-vmr9.png"

Write-Host "== 1/5 清理旧进程 ==" -ForegroundColor Cyan
Get-Process -Name "YiouCamera.Harness", "shm_producer" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (-not $NoProducer) {
    if (-not (Test-Path $ProducerExe)) {
        Write-Warning "找不到测试图案生产者: $ProducerExe（若已有真实 RTSP 会话在喂帧，可加 -NoProducer）"
    } else {
        Write-Host "== 2/5 启动共享内存测试图案生产者 ==" -ForegroundColor Cyan
        $prod = Start-Process -FilePath $ProducerExe -ArgumentList "1920", "1080", "30", "600" -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 2
    }
}

Write-Host "== 3/5 重新构建 harness ==" -ForegroundColor Cyan
Push-Location $here
dotnet build -c Release -v q | Select-Object -Last 3
Pop-Location

if (-not (Test-Path $exe)) { throw "构建失败，找不到 $exe" }

Write-Host "== 4/5 运行 harness（设备: $DeviceName） ==" -ForegroundColor Cyan
if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
$proc = Start-Process -FilePath $exe -ArgumentList "`"$DeviceName`"" -PassThru
Start-Sleep -Seconds $ScreenshotDelaySec

# 截图：把 harness 窗口提到前台，按窗口矩形抓屏（VMR9/D3DImage 内容不被 PrintWindow 捕获，只能抓屏）
try {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    Add-Type @"
using System;using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@
    $hwnd = [W]::FindWindow($null, "YiouCamera Harness")
    if ($hwnd -ne [IntPtr]::Zero) {
        [W]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 400
        $r = New-Object W+RECT
        [W]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.R - $r.L; $h = $r.B - $r.T
        if ($w -gt 0 -and $h -gt 0) {
            $bmp = New-Object System.Drawing.Bitmap($w, $h)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
            $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Host "   截图: $shot ($w x $h @ $($r.L),$($r.T))"
        }
    } else { Write-Warning "未找到 harness 窗口，跳过截图" }
} catch { Write-Warning "截图失败: $_" }

$proc.WaitForExit(90000) | Out-Null
$code = $proc.ExitCode

Write-Host "== 5/5 结果 ==" -ForegroundColor Cyan
Get-Content $log -ErrorAction SilentlyContinue

if ($null -ne $prod) { Stop-Process -Id $prod.Id -Force -ErrorAction SilentlyContinue }

$passed = (Get-Content $log -ErrorAction SilentlyContinue) -match "PASS\(device-path\)"
if ($passed) {
    Write-Host "`n[PASS] 虚拟摄像头在宿主同款 VMR9 管线下出图，且滤波器在消费共享内存帧。" -ForegroundColor Green
    exit 0
}
Write-Host "`n[FAIL] 未通过（exit=$code）。看上面日志：`n  - 'Capture device  not found' = 首次建图走空名字分支，属预期，会自动重试`n  - '未找到可用于建立连接的介质筛选器组合' = pin 连不上 VMR9，通常是缺 PinCategory.Preview`n  - readDelta=0 = 滤波器没在消费共享内存" -ForegroundColor Red
exit 1
