@echo off
setlocal
if not defined SRT_ISLAND_DATA set "SRT_ISLAND_DATA=%~dp0data"
if exist "%~dp0release\SRT Island.exe" (
  start "" "%~dp0release\SRT Island.exe" %*
) else (
  start "" "%~dp0publish\SRT Island.exe" %*
)
