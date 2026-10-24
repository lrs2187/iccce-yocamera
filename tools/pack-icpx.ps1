# 打包为标准 ICCCE 插件包 .icpx（本质是 zip，含且仅含 3 个文件）
# 用法: pwsh -NoProfile -ExecutionPolicy Bypass -File tools\pack-icpx.ps1 [-Configuration Release]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$bin = Join-Path $root "src\YiouCamera.Plugin\bin\$Configuration\net10.0-windows10.0.19041.0"
$outDir = Join-Path $root "build\out"
New-Item -ItemType Directory -Force $outDir | Out-Null

$manifestPath = Join-Path $bin "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "未找到构建产物，请先跑 tools\build-all.ps1" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$entry = $manifest.EntranceAssembly
$deps = ($entry -replace '\.dll$', '.deps.json')

$files = @("manifest.json", $entry, $deps)
foreach ($f in $files) {
    if (-not (Test-Path (Join-Path $bin $f))) { throw "缺少产物: $f" }
}

$staging = Join-Path $env:TEMP ("icpx_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $staging | Out-Null
try {
    foreach ($f in $files) { Copy-Item (Join-Path $bin $f) $staging -Force }

    # 严格校验：包内只能有这 3 个文件，且不能含宿主已提供的程序集
    $entries = Get-ChildItem $staging -File | Select-Object -ExpandProperty Name
    $unexpected = $entries | Where-Object { $_ -notin $files }
    if ($unexpected) { throw "包内出现意外条目：$($unexpected -join ', ')" }
    foreach ($forbidden in @("InkCanvas.PluginSdk.dll", "InkCanvas.Controls.dll", "WinRT.Runtime.dll", "Microsoft.Windows.SDK.NET.dll")) {
        if ($entries -contains $forbidden) { throw "包内出现不应分发的程序集：$forbidden" }
    }

    $zip = Join-Path $outDir "$($manifest.Id).zip"
    $icpx = Join-Path $outDir "$($manifest.Id)-$($manifest.Version).icpx"
    Remove-Item $zip, $icpx -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip -CompressionLevel Optimal
    Move-Item $zip $icpx -Force

    $hash = (Get-FileHash $icpx -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $icpx -Leaf)" | Set-Content (Join-Path $outDir "$(Split-Path $icpx -Leaf).sha256") -Encoding ASCII

    Write-Host "`n[DONE] 插件包：$icpx" -ForegroundColor Green
    Write-Host "  大小   : $([math]::Round((Get-Item $icpx).Length/1KB,1)) KB"
    Write-Host "  SHA256 : $hash"
    Write-Host "  包内   : $($files -join ', ')"
    Write-Host "`n安装：宿主设置 → 插件 → 从本地安装（或把 .icpx 放进 <宿主>\PluginPackages\）"
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
