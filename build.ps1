# build.ps1 - repo-root convenience wrapper.
# The canonical virtual-camera build lives in src\YiouCamera.Vcam\build.ps1 and writes its
# artifacts to <repo>\build\out\.
& (Join-Path $PSScriptRoot 'src\YiouCamera.Vcam\build.ps1') @args
exit $LASTEXITCODE
