# Builds native\emuffb\emuffb.dll (x64 DirectInput OEM FFB driver).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$proj = Join-Path $root "native\emuffb\emuffb.vcxproj"
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found" }

Write-Host "MSBuild: $msbuild"
& $msbuild $proj /p:Configuration=Release /p:Platform=x64 /v:m /nologo
if ($LASTEXITCODE -ne 0) { throw "emuffb build failed" }

$dll = Join-Path $root "native\emuffb\bin\emuffb.dll"
if (-not (Test-Path $dll)) { throw "Missing $dll" }
Write-Host "OK: $dll"
Get-Item $dll | Format-List FullName, Length, LastWriteTime
