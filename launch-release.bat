@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0src\SteeringWheelEmulator.App\bin\Release\net8.0-windows\SteeringWheelEmulator.exe"

tasklist /FI "IMAGENAME eq SteeringWheelEmulator.exe" | find /I "SteeringWheelEmulator.exe" >nul
if not errorlevel 1 (
  echo SteeringWheelEmulator.exe is already running. Close it (including the tray icon^) so this bat can rebuild, then run it again.
  pause
  exit /b 1
)

echo Building Release...
dotnet build "%~dp0src\SteeringWheelEmulator.App\SteeringWheelEmulator.App.csproj" -c Release --nologo
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

if not exist "%EXE%" (
  echo Release EXE not found: %EXE%
  pause
  exit /b 1
)

start "" "%EXE%"
