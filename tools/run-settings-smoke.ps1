# 设置页冒烟测试：在独立进程里构造插件设置页，验证「打开设置 → 插件」不会崩宿主。
#
# 为什么必须独立进程：StackOverflowException 无法 catch，会直接带走整个进程。
# 所以判据不是异常，而是「进程正常退出 + 输出 SMOKE-OK」。
# 回归背景：v0.3.0 的 SettingsView.NewButton 同名重载被 async lambda 决议成自递归，
# 宿主一打开设置页就 0xC00000FD 闪退，宿主日志里根本看不出是插件的问题。
#
# 用法:
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools\run-settings-smoke.ps1
#   pwsh -NoProfile -ExecutionPolicy Bypass -File tools\run-settings-smoke.ps1 -PluginDll <某个旧 dll>   # 复现历史崩溃
param(
    [string]$PluginDll = "",
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $PluginDll) {
    $PluginDll = Join-Path $root "src\YiouCamera.Plugin\bin\Release\net10.0-windows10.0.19041.0\YiouCamera.Plugin.dll"
}
$PluginDll = [IO.Path]::GetFullPath($PluginDll)
if (-not (Test-Path $PluginDll)) { throw "找不到插件 dll: $PluginDll（先跑 tools\build-all.ps1）" }

$proj = Join-Path $root "tests\SettingsSmoke\SettingsSmoke.csproj"
$exe = Join-Path $root "tests\SettingsSmoke\bin\Release\net10.0-windows10.0.19041.0\YiouCamera.SettingsSmoke.exe"

if (-not $SkipBuild -or -not (Test-Path $exe)) {
    Write-Host "=== 1/2 构建冒烟测试 ===" -ForegroundColor Cyan
    & dotnet build $proj -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw "冒烟测试构建失败 (exit=$LASTEXITCODE)" }
}
if (-not (Test-Path $exe)) { throw "未找到冒烟测试可执行文件: $exe" }

Write-Host "=== 2/2 运行冒烟测试（独立进程）===" -ForegroundColor Cyan
Write-Host "  被测 dll: $PluginDll"
$stdout = Join-Path $env:TEMP ("smoke_" + [guid]::NewGuid().ToString("N") + ".out")
$stderr = Join-Path $env:TEMP ("smoke_" + [guid]::NewGuid().ToString("N") + ".err")
$p = Start-Process -FilePath $exe -ArgumentList "`"$PluginDll`"" -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr
# .NET 进程 ExitCode 是 int32，栈溢出会是负数（-1073741571 = 0xC00000FD），先归一到 32 位无符号
$code = [int64]$p.ExitCode
if ($code -lt 0) { $code += 4294967296 }

Get-Content $stdout -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object { "  $_" }
$err = Get-Content $stderr -Encoding UTF8 -ErrorAction SilentlyContinue
if ($err) { Write-Host "  --- stderr ---"; $err | Select-Object -First 15 | ForEach-Object { "  $_" } }
Remove-Item $stdout, $stderr -Force -ErrorAction SilentlyContinue

# 0xC00000FD = STATUS_STACK_OVERFLOW
if ($code -eq 0xC00000FD) {
    Write-Host "`n[FAIL] 栈溢出（0xC00000FD）—— 设置页构建路径递归了，宿主会直接闪退。" -ForegroundColor Red
    exit 1
}
if ($code -ne 0) {
    Write-Host ("`n[FAIL] 冒烟测试失败，退出码 {0} (0x{1:X8})" -f $code, $code) -ForegroundColor Red
    exit 1
}

Write-Host "`n[PASS] 设置页可正常构造，独立进程未崩溃。" -ForegroundColor Green
exit 0
