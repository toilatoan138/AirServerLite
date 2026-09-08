# Produces dist\AirServerLite.exe - one self-contained file with the .NET runtime,
# playfair.dll and FFmpeg all embedded.
#
#   powershell -ExecutionPolicy Bypass -File tools\pack.ps1
#   powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -FFmpegBin D:\ffmpeg\bin
#
# On first launch the exe unpacks its native libraries into
# %LOCALAPPDATA%\AirServerLite\native\<content-hash>\ and reuses them on every later run.

param(
    [string]$FFmpegBin = "",
    [string]$Configuration = "Release",
    [switch]$SkipPlayfair
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$native = Join-Path $root 'src\AirServerLite\native'
$dist = Join-Path $root 'dist'

New-Item -ItemType Directory -Force -Path $native, $dist | Out-Null

# FFmpeg libraries this app actually calls, plus what they pull in through the OS loader.
# Shipping the whole bin/ folder would roughly double the exe for codecs we never touch.
$ffmpegNeeded = @('avcodec', 'avutil', 'swscale', 'swresample', 'avformat')

# ---------------------------------------------------------------- 1. playfair.dll

if (-not $SkipPlayfair) {
    Write-Host "== Building playfair.dll ==" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'native\build-playfair.ps1')
    if ($LASTEXITCODE -ne 0) { throw "playfair build failed" }
}

if (-not (Test-Path (Join-Path $native 'playfair.dll'))) {
    Write-Host ""
    Write-Host "WARNING: playfair.dll is not present." -ForegroundColor Yellow
    Write-Host "The exe will build and run, and the device will appear in Control Centre," -ForegroundColor Yellow
    Write-Host "but /fp-setup will fail and mirroring will not start." -ForegroundColor Yellow
    Write-Host ""
}

# ---------------------------------------------------------------- 2. FFmpeg

Write-Host "== Collecting FFmpeg ==" -ForegroundColor Cyan

if (-not $FFmpegBin) {
    # Look in the native directory, where a previous run unpacked it, then next to the app.
    foreach ($c in @(
        $native,
        (Join-Path $root 'ffmpeg\bin'),
        (Join-Path $root 'src\AirServerLite\bin\Debug\net8.0-windows\win-x64\ffmpeg\bin')
    )) {
        if (Test-Path $c) {
            $hasDlls = Get-ChildItem $c -Filter "avcodec-*.dll" -ErrorAction SilentlyContinue
            if ($hasDlls) { $FFmpegBin = $c; break }
        }
    }
}

if (-not $FFmpegBin -or -not (Test-Path $FFmpegBin)) {
    throw @"
FFmpeg not found. Download an FFmpeg 9.0 *shared* build:

  https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip

unpack it, and re-run with:  -FFmpegBin <path-to-its-bin-folder>
"@
}

$copied = @()
$nativeFull = (Get-Item $native).FullName
foreach ($stem in $ffmpegNeeded) {
    $hit = Get-ChildItem $FFmpegBin -Filter "$stem-*.dll" -ErrorAction SilentlyContinue
    foreach ($f in $hit) {
        if ($f.DirectoryName -ne $nativeFull) {
            Copy-Item $f.FullName $native -Force
        }
        $copied += $f.Name
    }
}

if ($copied.Count -eq 0) { throw "No FFmpeg DLLs matched in $FFmpegBin" }
$copied | ForEach-Object { "  $_" }

# ---------------------------------------------------------------- 3. publish

Write-Host ""
Write-Host "== Publishing single file ==" -ForegroundColor Cyan

$publishDir = Join-Path $root 'dist\publish'
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

& dotnet publish (Join-Path $root 'src\AirServerLite\AirServerLite.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $publishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $publishDir 'AirServerLite.exe'
if (-not (Test-Path $exe)) { throw "publish completed but $exe is missing" }

Copy-Item $exe $dist -Force
Copy-Item (Join-Path $root 'src\AirServerLite\appsettings.json') $dist -Force
if (Test-Path (Join-Path $publishDir 'Assets')) {
    Copy-Item (Join-Path $publishDir 'Assets') $dist -Recurse -Force
}

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "=================================================" -ForegroundColor Green
Write-Host " dist\AirServerLite.exe   ($mb MB)" -ForegroundColor Green
Write-Host "=================================================" -ForegroundColor Green
Write-Host ""
Write-Host "appsettings.json sits next to it and is optional - the exe runs on built-in"
Write-Host "defaults if it is absent."
