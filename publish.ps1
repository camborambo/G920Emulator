# Builds a self-contained Windows x64 folder you can double-click:
#   dist\G920Emulator\G920Emulator.exe
#   dist-test\G920Emulator\G920Emulator.exe  (-TestBuild)
param(
    [switch]$OpenFolder,
    [switch]$TestBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$distRoot = if ($TestBuild) { Join-Path $root "dist-test" } else { Join-Path $root "dist" }
$outDir = Join-Path $distRoot "G920Emulator"

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

Write-Host "Building SimHub RPM plugin (net48)..." -ForegroundColor Cyan
dotnet build ".\src\G920Emulator.SimHubPlugin\G920Emulator.SimHubPlugin.csproj" -c Release
if ($LASTEXITCODE -ne 0) {
    throw "G920Emulator.SimHubPlugin build failed (exit $LASTEXITCODE). Is SimHub installed under Program Files (x86)\SimHub?"
}
$pluginDll = Join-Path $root "src\G920Emulator.SimHubPlugin\bin\Release\G920Emulator.SimHubPlugin.dll"
if (-not (Test-Path $pluginDll)) {
    throw "SimHub plugin DLL missing after build: $pluginDll"
}

Write-Host "Publishing G920 Emulator (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish ".\src\G920Emulator.App\G920Emulator.App.csproj" `
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
Write-Host "Bundled G920Emulator.SimHubPlugin.dll for SimHub Engine vibrations." -ForegroundColor Green

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

if (Test-Path $g920ffbDll) {
    # Games keep g920ffb.dll loaded; skip the copy when it's already identical.
    $destFfb = Join-Path $outDir "g920ffb.dll"
    if (-not ((Test-Path $destFfb) -and (Get-FileHash $destFfb).Hash -eq (Get-FileHash $g920ffbDll).Hash)) {
        Copy-Item $g920ffbDll $outDir -Force
    }
    Write-Host "Bundled g920ffb.dll (OEM DirectInput FFB driver)."
} else {
    Write-Warning "g920ffb.dll missing - in-game FFB ingress via OEM driver will not work."
}

if ($TestBuild) {
    # Side-by-side experiment — do not rewrite stable dist launchers.
    $launcher = Join-Path $distRoot "Launch G920 Emulator (ratio test).bat"
    Set-Content -Path $launcher -Encoding ASCII -Value @(
        '@echo off'
        'start "" "%~dp0G920Emulator\G920Emulator.exe"'
    )
    $rootLauncher = Join-Path $root "Launch G920 Emulator (ratio test).bat"
    Set-Content -Path $rootLauncher -Encoding ASCII -Value @(
        '@echo off'
        'set EXE=%~dp0dist-test\G920Emulator\G920Emulator.exe'
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
    $launcher = Join-Path $distRoot "Launch G920 Emulator.bat"
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
    $oldDist = Join-Path $root "dist\Launch G920 Emulator.bat"
    if (Test-Path $oldRoot) { Remove-Item $oldRoot -Force }
    if (Test-Path $oldDist) { Remove-Item $oldDist -Force }
}

# Profiles are created at runtime in %AppData%\G920Emulator — never ship profiles\,
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

# Zip with a single top-level folder: G920Emulator\...
$zipName = if ($TestBuild) { "G920Emulator-ratio-test-win-x64.zip" } else { "G920Emulator-win-x64.zip" }
$zipPath = Join-Path $distRoot $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "Creating $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path $outDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Zip layout: G920Emulator\ (folder) → app files"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "Run:  $outDir\G920Emulator.exe"
if ($TestBuild) {
    Write-Host "Or double-click:  Launch G920 Emulator (ratio test).bat"
} else {
    Write-Host "Or double-click:  Launch G920 Emulator.bat"
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
