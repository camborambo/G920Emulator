# Builds a self-contained Windows x64 folder you can double-click:
#   dist\SteeringWheelEmulator\SteeringWheelEmulator.exe
#   dist-test\SteeringWheelEmulator\SteeringWheelEmulator.exe  (-TestBuild)
param(
    [switch]$OpenFolder,
    [switch]$TestBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$distRoot = if ($TestBuild) { Join-Path $root "dist-test" } else { Join-Path $root "dist" }
$outDir = Join-Path $distRoot "SteeringWheelEmulator"

$winuhid = Join-Path $root "native\winuhid"
if (-not (Test-Path (Join-Path $winuhid "WinUHid.dll"))) {
    Write-Warning "native\winuhid\WinUHid.dll missing. Run tools\build-winuhid.ps1 first if you can."
}

# Native OEM FFB COM driver (DirectInput -> shared memory)
$emuffbScript = Join-Path $root "tools\build-emuffb.ps1"
$emuffbDll = Join-Path $root "native\emuffb\bin\emuffb.dll"
if (Test-Path $emuffbScript) {
    Write-Host "Building emuffb.dll..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $emuffbScript
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "emuffb build failed (exit $LASTEXITCODE)"
    }
}

Write-Host "Building SimHub RPM plugin (net48)..." -ForegroundColor Cyan
dotnet build ".\src\SteeringWheelEmulator.SimHubPlugin\SteeringWheelEmulator.SimHubPlugin.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    throw "SteeringWheelEmulator.SimHubPlugin build failed (exit $LASTEXITCODE). Is SimHub installed under Program Files (x86)\SimHub?"
}
$pluginDll = Join-Path $root "src\SteeringWheelEmulator.SimHubPlugin\bin\Release\SteeringWheelEmulator.SimHubPlugin.dll"
if (-not (Test-Path $pluginDll)) {
    throw "SimHub plugin DLL missing after build: $pluginDll"
}

Write-Host "Publishing Steering Wheel Emulator (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish ".\src\SteeringWheelEmulator.App\SteeringWheelEmulator.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -o $outDir

# Ensure plugin is next to the External Sim definition for Register-with-SimHub install.
$simhubOut = Join-Path $outDir "simhub"
New-Item -ItemType Directory -Force -Path $simhubOut | Out-Null
Copy-Item $pluginDll $simhubOut -Force
Write-Host "Bundled SteeringWheelEmulator.SimHubPlugin.dll for SimHub Engine vibrations." -ForegroundColor Green

# Remove legacy SessionWatch leftovers from older publishes (no longer shipped).
$legacyWatchPaths = @(
    (Join-Path $outDir "G920Emulator.SessionWatch.exe"),
    (Join-Path $outDir "sessionwatch")
)
foreach ($p in $legacyWatchPaths) {
    if (Test-Path $p) {
        Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Get-ChildItem $outDir -Filter "G920Emulator.SessionWatch.*" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# Ensure bundled WinUHid package is present beside the EXE
$destWinuhid = Join-Path $outDir "winuhid"
New-Item -ItemType Directory -Force -Path $destWinuhid | Out-Null
if (Test-Path $winuhid) {
    Copy-Item (Join-Path $winuhid "*") $destWinuhid -Force -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $winuhid "WinUHid.dll")) {
        Copy-Item (Join-Path $winuhid "WinUHid.dll") $outDir -Force
    }
}

# Logitech Steering Wheel SDK runtime (cached on Start bridge; pinned only while session active)
$logisdk = Join-Path $root "native\logisdk"
if (Test-Path (Join-Path $logisdk "x64\LogitechSteeringWheel.dll")) {
    $destLogisdk = Join-Path $outDir "logisdk"
    New-Item -ItemType Directory -Force -Path $destLogisdk | Out-Null
    Copy-Item (Join-Path $logisdk "*") $destLogisdk -Recurse -Force
    Write-Host "Bundled Logitech Steering Wheel SDK (x64 + x86)."
} else {
    Write-Warning "native\logisdk missing - Logitech SDK games (NFS Heat, etc.) may not show a wheel layout."
}

if (Test-Path $emuffbDll) {
    # Games keep emuffb.dll loaded; skip the copy when it's already identical.
    $destFfb = Join-Path $outDir "emuffb.dll"
    if (-not ((Test-Path $destFfb) -and (Get-FileHash $destFfb).Hash -eq (Get-FileHash $emuffbDll).Hash)) {
        Copy-Item $emuffbDll $outDir -Force
    }
    # Remove legacy DLL name from older publishes so install folders don't keep both.
    Remove-Item (Join-Path $outDir "g920ffb.dll") -Force -ErrorAction SilentlyContinue
    Write-Host "Bundled emuffb.dll (OEM DirectInput FFB driver)."
} else {
    Write-Warning "emuffb.dll missing - in-game FFB ingress via OEM driver will not work."
}

if ($TestBuild) {
    # Side-by-side experiment - do not rewrite stable dist launchers.
    $launcher = Join-Path $distRoot "Launch Steering Wheel Emulator (ratio test).bat"
    Set-Content -Path $launcher -Encoding ASCII -Value @(
        '@echo off'
        'start "" "%~dp0SteeringWheelEmulator\SteeringWheelEmulator.exe"'
    )
    $rootLauncher = Join-Path $root "Launch Steering Wheel Emulator (ratio test).bat"
    Set-Content -Path $rootLauncher -Encoding ASCII -Value @(
        '@echo off'
        'set EXE=%~dp0dist-test\SteeringWheelEmulator\SteeringWheelEmulator.exe'
        'if exist "%EXE%" ('
        '  start "" "%EXE%"'
        '  exit /b 0'
        ')'
        'echo Ratio-test build not found. Building it now...'
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -TestBuild'
        'if exist "%EXE%" ('
        '  start "" "%EXE%"'
        ') else ('
        '  echo Publish failed.'
        '  pause'
        ')'
    )
} else {
    # Convenience launcher next to the folder
    $launcher = Join-Path $distRoot "Launch Steering Wheel Emulator.bat"
    Set-Content -Path $launcher -Encoding ASCII -Value @(
        '@echo off'
        'start "" "%~dp0SteeringWheelEmulator\SteeringWheelEmulator.exe"'
    )

    # Root-level quick launcher
    $rootLauncher = Join-Path $root "Launch Steering Wheel Emulator.bat"
    Set-Content -Path $rootLauncher -Encoding ASCII -Value @(
        '@echo off'
        'set EXE=%~dp0dist\SteeringWheelEmulator\SteeringWheelEmulator.exe'
        'if exist "%EXE%" ('
        '  start "" "%EXE%"'
        '  exit /b 0'
        ')'
        'echo SteeringWheelEmulator.exe not found. Building it now...'
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1"'
        'if exist "%EXE%" ('
        '  start "" "%EXE%"'
        ') else ('
        '  echo Publish failed.'
        '  pause'
        ')'
    )

    # Remove old launcher names if present
    foreach ($old in @(
        (Join-Path $root "Launch G920 Emulator.bat"),
        (Join-Path $root "Launch G920Emulator.bat"),
        (Join-Path $root "Launch G920 Emulator (ratio test).bat"),
        (Join-Path $distRoot "Launch G920 Emulator.bat"),
        (Join-Path $root "dist\Launch G920 Emulator.bat")
    )) {
        if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
    }
}

# Profiles are created at runtime in %AppData%\SteeringWheelEmulator - never ship profiles\,
# ffb-profiles\, or settings.json (publishing from a used dist\ used to bake personal
# binds into the zip and overwrite users on "unzip over install" updates).
$profilesOut = Join-Path $outDir "profiles"
if (Test-Path $profilesOut) { Remove-Item $profilesOut -Recurse -Force }
$ffbProfilesOut = Join-Path $outDir "ffb-profiles"
if (Test-Path $ffbProfilesOut) { Remove-Item $ffbProfilesOut -Recurse -Force }
Remove-Item (Join-Path $outDir "settings.json") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $outDir "portable.txt") -Force -ErrorAction SilentlyContinue

# Ship version notes next to the exe
foreach ($doc in @("CHANGELOG.md", "README.md")) {
    $srcDoc = Join-Path $root $doc
    if (Test-Path $srcDoc) {
        Copy-Item $srcDoc $outDir -Force
    }
}

$simhubSrc = Join-Path $root "simhub"
if (Test-Path $simhubSrc) {
    $simhubOut = Join-Path $outDir "simhub"
    New-Item -ItemType Directory -Force -Path $simhubOut | Out-Null
    Copy-Item (Join-Path $simhubSrc "*") $simhubOut -Force
}

# Zip with a single top-level folder: SteeringWheelEmulator\...
$zipName = if ($TestBuild) { "SteeringWheelEmulator-ratio-test-win-x64.zip" } else { "SteeringWheelEmulator-win-x64.zip" }
$zipPath = Join-Path $distRoot $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "Creating $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path $outDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Zip layout: SteeringWheelEmulator\ (folder) → app files"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Run:  $outDir\SteeringWheelEmulator.exe"
if ($TestBuild) {
    Write-Host "Or double-click:  Launch Steering Wheel Emulator (ratio test).bat"
} else {
    Write-Host "Or double-click:  Launch Steering Wheel Emulator.bat"
}
Write-Host "Zip:  $zipPath"
Write-Host ""
$bundledOk = (Test-Path (Join-Path $destWinuhid "WinUHidDriver.dll")) -and (Test-Path (Join-Path $destWinuhid "WinUHidDriver.inf"))
if ($bundledOk) {
    Write-Host "WinUHid is bundled in $outDir\winuhid - use Install WinUHid in the app (no download)."
} else {
    Write-Warning "WinUHid driver package incomplete in native\winuhid. Run tools\build-winuhid.ps1 then republish."
}

if ($OpenFolder) {
    Start-Process explorer.exe $outDir
}
