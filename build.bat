@echo off
setlocal
cd /d "%~dp0"
rem Portable x86 and x64 folders + zip in artifacts\ (same as CI package.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\package.ps1"
if errorlevel 1 exit /b 1
echo.
echo Done: artifacts\iwo-Calendar-Bridge-x86 and artifacts\iwo-Calendar-Bridge-x64
exit /b 0
