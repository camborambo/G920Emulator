@echo off
set EXE=%~dp0dist\G920Emulator\G920Emulator.exe
if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)
echo G920Emulator.exe not found. Building it now...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"
if exist "%EXE%" (
  start "" "%EXE%"
) else (
  echo Publish failed.
  pause
)
