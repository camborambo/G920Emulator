# Builds a self-contained Windows x64 folder you can double-click:
#   dist\G920Emulator\G920Emulator.exe
param(
    [switch]$OpenFolder
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$outDir = Join-Path $root "dist\G920Emulator"

$winuhid = Join-Path $root "native\winuhid"
if (-not (Test-Path (Join-Path $winuhid "WinUHid.dll"))) {
    Write-Warning "native\winuhid\WinUHid.dll missing. Run tools\build-winuhid.ps1 first if you can."
}

# Native OEM FFB COM driver (DirectInput -> shared memory)
$g920ffbScript = Join-Path $root "tools\build-g920ffb.ps1"
$g920ffbDll = Join-Path $root "native\g920ffb\bin\g920ffb.dll"
if (Test-Path $g920ffbScript) {
    Write-Host "Building g920ffb.dll..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $g920ffbScript
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "g920ffb build failed (exit $LASTEXITCODE)"
    }
}

Write-Host "Publishing G920 Emulator (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish ".\src\G920Emulator.App\G920Emulator.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -o $outDir

# Ensure bundled WinUHid package is present beside the EXE
$destWinuhid = Join-Path $outDir "winuhid"
New-Item -ItemType Directory -Force -Path $destWinuhid | Out-Null
if (Test-Path $winuhid) {
    Copy-Item (Join-Path $winuhid "*") $destWinuhid -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $winuhid "WinUHid.dll")) {
        Copy-Item (Join-Path $winuhid "WinUHid.dll") $outDir -Force
    }
}

if (Test-Path $g920ffbDll) {
    Copy-Item $g920ffbDll $outDir -Force
    Write-Host "Bundled g920ffb.dll (OEM DirectInput FFB driver)."
} else {
    Write-Warning "g920ffb.dll missing - in-game FFB ingress via OEM driver will not work."
}

# Convenience launcher next to the folder
$launcher = Join-Path $root "dist\Launch G920 Emulator.bat"
Set-Content -Path $launcher -Encoding ASCII -Value @(
    '@echo off'
    'start "" "%~dp0G920Emulator\G920Emulator.exe"'
)

# Root-level quick launcher
$rootLauncher = Join-Path $root "Launch G920 Emulator.bat"
Set-Content -Path $rootLauncher -Encoding ASCII -Value @(
    '@echo off'
    'set EXE=%~dp0dist\G920Emulator\G920Emulator.exe'
    'if exist "%EXE%" ('
    '  start "" "%EXE%"'
    '  exit /b 0'
    ')'
    'echo G920Emulator.exe not found. Building it now...'
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"'
    'if exist "%EXE%" ('
    '  start "" "%EXE%"'
    ') else ('
    '  echo Publish failed.'
    '  pause'
    ')'
)

# Remove old launcher name if present
$oldRoot = Join-Path $root "Launch G920Emulator.bat"
$oldDist = Join-Path $root "dist\Launch G920Emulator.bat"
if (Test-Path $oldRoot) { Remove-Item $oldRoot -Force }
if (Test-Path $oldDist) { Remove-Item $oldDist -Force }

# Zip with a single top-level folder: G920Emulator-win-x64.zip → G920Emulator\...
$zipPath = Join-Path $root "dist\G920Emulator-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "Creating $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path $outDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Zip layout: G920Emulator\ (folder) → app files"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Run:  $outDir\G920Emulator.exe"
Write-Host "Or double-click:  Launch G920 Emulator.bat"
Write-Host "Zip:  $zipPath"
Write-Host ""
$bundledOk = (Test-Path (Join-Path $destWinuhid "WinUHidDriver.dll")) -and (Test-Path (Join-Path $destWinuhid "WinUHidDriver.inf"))
if ($bundledOk) {
    Write-Host "WinUHid is bundled in dist\G920Emulator\winuhid - use Install WinUHid in the app (no download)."
} else {
    Write-Warning "WinUHid driver package incomplete in native\winuhid. Run tools\build-winuhid.ps1 then republish."
}

if ($OpenFolder) {
    Start-Process explorer.exe $outDir
}
