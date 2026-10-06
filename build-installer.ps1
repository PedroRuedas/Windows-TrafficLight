# Builds a self-contained TrafficLight.exe and packs it into dist\installer\WindowsTrafficLight-Setup-<version>.exe
# Usage: .\build-installer.ps1 [-Version 1.2.3]
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not $Version) {
    [xml]$proj = Get-Content "$root\TrafficLight.csproj"
    $Version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

Write-Host "Publishing TrafficLight $Version (self-contained, win-x64)..."
Remove-Item "$root\publish" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\TrafficLight.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Compiling installer..."
& $iscc /Q "/DAppVersion=$Version" "$root\installer\TrafficLight.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-Item "$root\dist\installer\WindowsTrafficLight-Setup-$Version.exe"
$hash = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
Write-Host "SHA256: $hash"
