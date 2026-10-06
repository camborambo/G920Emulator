@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0src\G920Emulator.App\bin\Release\net8.0-windows\G920Emulator.exe"

if not exist "%EXE%" (
  echo Release build not found. Building...
  dotnet build "%~dp0src\G920Emulator.App\G920Emulator.App.csproj" -c Release --nologo
  if errorlevel 1 (
    echo Build failed.
    exit /b 1
  )
)

start "" "%EXE%"
