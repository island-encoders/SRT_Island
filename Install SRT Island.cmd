@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Dependencies.ps1"
if errorlevel 1 (
  pause
  exit /b 1
)
if exist "%~dp0SRT Island.exe" start "" "%~dp0SRT Island.exe"
