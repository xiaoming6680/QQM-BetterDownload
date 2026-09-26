@echo off
if not exist "%~dp0build\BetterDownload.exe" (
  echo Build first: powershell -ExecutionPolicy Bypass -File scripts\build.ps1
  pause
  exit /b 1
)
start "" "%~dp0build\BetterDownload.exe" --card-demo
