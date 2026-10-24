@echo off
rem build.cmd - one-shot build of the YiouCamera virtual camera (calls build.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
