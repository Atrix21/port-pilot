<#
.SYNOPSIS
    Builds, tests, publishes and packages PortPilot.

.EXAMPLE
    ./build.ps1                 # test + publish + installer
    ./build.ps1 -SkipTests      # publish + installer
    ./build.ps1 -Screenshots    # also regenerate docs/screenshots
#>
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$Screenshots
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Find-Dotnet {
    # Prefer an SDK that supports net10.0; a user-local install is common on machines with an older global one.
    foreach ($candidate in @("$env:USERPROFILE\.dotnet\dotnet.exe", (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source)) {
        if ($candidate -and (Test-Path $candidate)) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($sdks -match '^10\.') { return $candidate }
        }
    }
    throw 'The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}

function Find-Iscc {
    foreach ($candidate in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

$dotnet = Find-Dotnet
$env:DOTNET_ROOT = Split-Path $dotnet
Write-Host "Using $dotnet" -ForegroundColor DarkGray

if (-not $SkipTests) {
    Write-Host '› Running tests' -ForegroundColor Cyan
    & $dotnet test tests/PortPilot.Core.Tests -c Release --nologo
    if ($LASTEXITCODE) { throw 'Tests failed' }
}

Write-Host '› Publishing self-contained release build' -ForegroundColor Cyan
$publish = Join-Path $PSScriptRoot 'artifacts/publish'
if (Test-Path $publish) { Get-ChildItem $publish | Remove-Item -Recurse -Force }
& $dotnet publish src/PortPilot.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true `
    -p:Version=$Version -o $publish --nologo
if ($LASTEXITCODE) { throw 'Publish failed' }

if ($Screenshots) {
    Write-Host '› Rendering screenshots' -ForegroundColor Cyan
    $shots = Join-Path $PSScriptRoot 'docs/screenshots'
    New-Item -ItemType Directory -Force $shots | Out-Null
    $p = Start-Process (Join-Path $publish 'PortPilot.exe') -ArgumentList '--screenshot', $shots -PassThru
    if (-not $p.WaitForExit(240000)) { $p.Kill(); throw 'Screenshot run timed out' }
}

if (-not $SkipInstaller) {
    Write-Host '› Building installer' -ForegroundColor Cyan
    & (Find-Iscc) "/DAppVersion=$Version" "/DPublishDir=$publish" installer/PortPilot.iss
    if ($LASTEXITCODE) { throw 'Installer build failed' }
}

Write-Host '✓ Done' -ForegroundColor Green
Get-ChildItem artifacts -Recurse -File -Include *.exe | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
