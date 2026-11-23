<#
.SYNOPSIS
    Builds, tests, publishes and packages PortPilot.

.EXAMPLE
    ./build.ps1                 # test + publish + installer
    ./build.ps1 -SkipTests      # publish + installer
    ./build.ps1 -Screenshots    # also regenerate docs/screenshots
    ./build.ps1 -Gif            # also re-record docs/demo.gif (needs ffmpeg: winget install Gyan.FFmpeg)
#>
param(
    [string]$Version = '1.0.0',
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$Screenshots,
    [switch]$Gif
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

# The terminal app ships beside the GUI: it is the Claude Code hook, the background watcher and the MCP server.
# It lives in cli\ because Windows file names are case-insensitive: portpilot.exe would replace PortPilot.exe.
Write-Host '› Publishing cli\portpilot.exe (native AOT)' -ForegroundColor Cyan
& $dotnet publish src/PortPilot.Cli -c Release -r win-x64 -p:Version=$Version -o (Join-Path $publish 'cli') --nologo
if ($LASTEXITCODE) { throw 'CLI publish failed' }

if ($Screenshots) {
    Write-Host '› Rendering screenshots' -ForegroundColor Cyan
    $shots = Join-Path $PSScriptRoot 'docs/screenshots'
    New-Item -ItemType Directory -Force $shots | Out-Null
    $p = Start-Process (Join-Path $publish 'PortPilot.exe') -ArgumentList '--screenshot', $shots -PassThru
    if (-not $p.WaitForExit(240000)) { $p.Kill(); throw 'Screenshot run timed out' }
}

if ($Gif) {
    Write-Host '› Recording demo GIF' -ForegroundColor Cyan
    $ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source
    if (-not $ffmpeg) {
        $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $ffmpeg) { throw 'ffmpeg was not found. Install it with: winget install Gyan.FFmpeg' }

    $frames = Join-Path ([System.IO.Path]::GetTempPath()) 'PortPilot-recording'
    $p = Start-Process (Join-Path $publish 'PortPilot.exe') -ArgumentList '--record', $frames -PassThru
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw 'Recording timed out' }

    # Palette from frame differences, light ordered dither, and only changed regions per frame.
    $filter = 'scale=1000:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=160:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle'
    & $ffmpeg -y -loglevel error -f concat -safe 0 -i (Join-Path $frames 'frames.txt') -fps_mode vfr -vf $filter -loop 0 docs/demo.gif
    if ($LASTEXITCODE) { throw 'GIF encoding failed' }
}

if (-not $SkipInstaller) {
    Write-Host '› Building installer' -ForegroundColor Cyan
    & (Find-Iscc) "/DAppVersion=$Version" "/DPublishDir=$publish" installer/PortPilot.iss
    if ($LASTEXITCODE) { throw 'Installer build failed' }

    # The portable download trades ~0.4 s of startup for less than half the size. The installer
    # compresses on its own, so it keeps the faster uncompressed exe. This runs last because
    # publishing again makes MSBuild clean the previous publish folder.
    Write-Host '› Publishing compressed portable exe' -ForegroundColor Cyan
    $portable = Join-Path $PSScriptRoot 'artifacts/portable'
    & $dotnet publish src/PortPilot.App -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true `
        -p:EnableCompressionInSingleFile=true -p:Version=$Version -o $portable --nologo
    if ($LASTEXITCODE) { throw 'Portable publish failed' }
    Get-ChildItem $portable -Exclude PortPilot.exe | Remove-Item -Recurse -Force
    Rename-Item (Join-Path $portable 'PortPilot.exe') "PortPilot-$Version-portable.exe" -Force
}

Write-Host '✓ Done' -ForegroundColor Green
Get-ChildItem artifacts -Recurse -File -Include *.exe | ForEach-Object { '  {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB) }
