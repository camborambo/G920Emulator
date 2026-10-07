@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0src\G920Emulator.App\bin\Release\net8.0-windows\G920Emulator.exe"

tasklist /FI "IMAGENAME eq G920Emulator.exe" | find /I "G920Emulator.exe" >nul
if not errorlevel 1 (
  echo G920Emulator.exe is already running. Close it (including the tray icon^) so this bat can rebuild, then run it again.
  pause
  exit /b 1
)

echo Building Release...
dotnet build "%~dp0src\G920Emulator.App\G920Emulator.App.csproj" -c Release --nologo
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
