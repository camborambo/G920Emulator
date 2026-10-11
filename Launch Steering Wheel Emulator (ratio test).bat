@echo off
set EXE=%~dp0dist-test\SteeringWheelEmulator\SteeringWheelEmulator.exe
if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)
echo Ratio-test build not found. Building it now...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -TestBuild
if exist "%EXE%" (
  start "" "%EXE%"
) else (
  echo Publish failed.
  pause
)
