# Builds WinUHid and copies binaries into native\winuhid for bundling.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$src = Join-Path $root "third_party\WinUHid"
$out = Join-Path $root "native\winuhid"
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1

if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio 2022 C++ workload." }
if (-not (Test-Path $src)) {
    git clone --depth 1 https://github.com/cgutman/WinUHid.git $src
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Building WinUHid.dll..."
& $msbuild (Join-Path $src "WinUHid\WinUHid.vcxproj") /p:Configuration=Release /p:Platform=x64 /m /verbosity:minimal
Copy-Item (Join-Path $src "WinUHid\build\Release\x64\WinUHid.dll") $out -Force

Write-Host "Building WinUHidDriver..."
# Match installed WDK (10.0.26100) — VS may default WDKBuildFolder to an older Windows SDK.
$wdkFolder = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\build" -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path (Join-Path $_.FullName "WindowsDriver.Default.props") } |
    Sort-Object Name -Descending |
    Select-Object -First 1
$driverProps = @("/p:Configuration=Release", "/p:Platform=x64", "/m", "/verbosity:minimal")
if ($wdkFolder) {
    $driverProps += "/p:WDKBuildFolder=$($wdkFolder.Name)"
    $driverProps += "/p:WindowsTargetPlatformVersion=$($wdkFolder.Name)"
    Write-Host "Using WDK build folder $($wdkFolder.Name)"
}
& $msbuild (Join-Path $src "WinUHid Driver\WinUHid Driver.vcxproj") @driverProps
if ($LASTEXITCODE -ne 0) {
    throw @"
Driver build failed. Install:
  - Windows Driver Kit (WDK)
  - Visual Studio component: Windows Driver Kit (Component.Microsoft.Windows.DriverKit)
  - Matching Windows SDK (e.g. Windows 11 SDK 10.0.26100)
  - MSVC Spectre-mitigated libs (x86/x64)
"@
}

$driverOut = Join-Path $src "WinUHid Driver\build\Release\x64"
if (-not (Test-Path $driverOut)) {
    $driverOut = Join-Path $src "build\Release\x64\WinUHid Driver"
}

Get-ChildItem $driverOut -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $($_.Name)" }

$copyMap = @{
    "WinUHidDriver.dll" = "WinUHidDriver.dll"
    "WinUHidDriver.inf" = "WinUHidDriver.inf"
    "winuhiddriver.cat" = "WinUHidDriver.cat"
    "WinUHidDriver.cat" = "WinUHidDriver.cat"
}
foreach ($pair in $copyMap.GetEnumerator()) {
    $found = Get-ChildItem $driverOut -Recurse -Filter $pair.Key -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) {
        $found = Get-ChildItem $src -Recurse -Filter $pair.Key -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "Release" } |
            Select-Object -First 1
    }
    if ($found) {
        Copy-Item $found.FullName (Join-Path $out $pair.Value) -Force
        Write-Host "Copied $($pair.Value)"
    }
}

$cer = Join-Path $src "Installer\WinUHid Package\WinUHidCertificate.cer"
if (Test-Path $cer) { Copy-Item $cer $out -Force }

Write-Host ""
Write-Host "Bundled package:" -ForegroundColor Green
Get-ChildItem $out | Format-Table Name, Length
Write-Host "Run .\publish.ps1 to ship these with the EXE."
