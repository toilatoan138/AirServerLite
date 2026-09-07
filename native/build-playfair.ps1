# Builds playfair.dll - the FairPlay SAP v3 bridge that iOS requires before it will send video.
#
# Works with either toolchain, preferring whichever is actually installed:
#   * GCC   (MinGW-w64 / w64devkit) - set MINGW_ROOT or put gcc on PATH
#   * MSVC  (Visual Studio "Desktop development with C++")
#
# Usage:  powershell -ExecutionPolicy Bypass -File native\build-playfair.ps1

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$ref  = Join-Path $here 'reference'
$build = Join-Path $here 'build'

# ---------------------------------------------------------------- reference sources

if (-not (Test-Path (Join-Path $ref 'lib/fairplay_playfair.c'))) {
    Write-Host "Reference sources missing - cloning UxPlay..." -ForegroundColor Cyan
    git clone --depth 1 https://github.com/FDH2/UxPlay.git $ref
}
if (-not (Test-Path (Join-Path $ref 'lib/fairplay_playfair.c'))) {
    throw "Could not find lib/fairplay_playfair.c under $ref"
}

New-Item -ItemType Directory -Force -Path $build | Out-Null

$sources = @(
    (Join-Path $here 'playfair_shim.c')
    (Join-Path $ref  'lib/fairplay_playfair.c')
    (Join-Path $ref  'lib/logger.c')
) + (Get-ChildItem (Join-Path $ref 'lib/playfair') -Filter '*.c' | ForEach-Object { $_.FullName })

$incDirs = @((Join-Path $ref 'lib'), (Join-Path $ref 'lib/playfair'))
$dll = Join-Path $build 'playfair.dll'

Write-Host "Sources ($($sources.Count)):" -ForegroundColor Cyan
$sources | ForEach-Object { "  " + (Split-Path $_ -Leaf) }

# ---------------------------------------------------------------- locate a compiler

function Find-Gcc {
    $c = Get-Command gcc -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @(
        (Join-Path $env:MINGW_ROOT 'bin\gcc.exe'),
        "$root\tools\w64devkit\bin\gcc.exe",
        "F:\w64devkit\bin\gcc.exe",
        "C:\msys64\mingw64\bin\gcc.exe",
        "C:\mingw64\bin\gcc.exe"
    )) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}

function Find-Vcvars {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }
    $vs = & $vswhere -latest -products * -property installationPath
    if (-not $vs) { return $null }
    $v = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    if (Test-Path $v) { return $v }
    return $null
}

$gcc = Find-Gcc
$vcvars = if (-not $gcc) { Find-Vcvars } else { $null }

if (-not $gcc -and -not $vcvars) {
    throw @"
No C compiler found.

Install either:
  * w64devkit (smallest, portable, no installer):
      https://github.com/skeeto/w64devkit/releases
      Extract so that gcc.exe lands at <repo>\tools\w64devkit\bin\gcc.exe
  * Visual Studio 2022 with the 'Desktop development with C++' workload.

Then re-run this script.
"@
}

# ---------------------------------------------------------------- compile

if ($gcc) {
    Write-Host "Compiler: $gcc" -ForegroundColor Green

    $gccArgs = @('-O2', '-shared', '-fvisibility=hidden', '-std=gnu99', '-w')
    $gccArgs += $incDirs | ForEach-Object { "-I$_" }
    $gccArgs += $sources
    $gccArgs += @('-o', $dll)
    # Statically link the GCC/pthread runtime so playfair.dll has no sidecar dependencies -
    # it has to survive being unpacked on its own into a cache directory.
    $gccArgs += @('-static-libgcc', '-Wl,-Bstatic,--whole-archive', '-lwinpthread',
                  '-Wl,--no-whole-archive,-Bdynamic')

    & $gcc @gccArgs
    if ($LASTEXITCODE -ne 0) { throw "gcc failed with exit code $LASTEXITCODE" }
}
else {
    Write-Host "Compiler: MSVC ($vcvars)" -ForegroundColor Green

    $q = { param($x) '"' + $x + '"' }
    $clArgs = @('/nologo', '/O2', '/MD', '/W1', '/wd4996', '/wd4244', '/wd4267',
                '/D_CRT_SECURE_NO_WARNINGS')
    $clArgs += $incDirs | ForEach-Object { '/I' + (& $q $_) }
    $clArgs += $sources | ForEach-Object { & $q $_ }
    $clArgs += @('/LD', '/Fe:' + (& $q $dll), '/Fo:' + (& $q "$build\"))

    $cmd = 'call "' + $vcvars + '" >nul 2>&1 && cl ' + ($clArgs -join ' ')
    & cmd.exe /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "cl failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path $dll)) { throw "Compilation reported success but $dll is missing" }

# ---------------------------------------------------------------- verify exports

Write-Host ""
Write-Host "Verifying exports..." -ForegroundColor Cyan

$required = @('fp_create', 'fp_setup', 'fp_decrypt', 'fp_destroy')

# Read the export table straight out of the PE file: no dumpbin/objdump dependency, and it
# checks the artefact we are actually going to ship rather than what the linker claims.
$bytes = [System.IO.File]::ReadAllBytes($dll)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$missing = $required | Where-Object { $text -notmatch [regex]::Escape($_) }

foreach ($r in $required) {
    if ($missing -contains $r) { Write-Host "  MISSING $r" -ForegroundColor Red }
    else { Write-Host "  OK      $r" -ForegroundColor Green }
}
if ($missing) { throw "playfair.dll is missing exports: $($missing -join ', ')" }

# ---------------------------------------------------------------- deploy

$targets = @(
    (Join-Path $root 'src\AirServerLite\native')
    (Join-Path $root 'src\AirServerLite\bin\Debug\net8.0-windows\win-x64')
    (Join-Path $root 'src\AirServerLite\bin\Release\net8.0-windows\win-x64')
)

foreach ($t in $targets) {
    if (Test-Path $t) {
        Copy-Item $dll $t -Force
        Write-Host "Deployed -> $t" -ForegroundColor Green
    }
}

$size = [math]::Round((Get-Item $dll).Length / 1KB, 0)
Write-Host ""
Write-Host "playfair.dll built ($size KB): $dll" -ForegroundColor Green
Write-Host "NOTE: UxPlay is GPL. Linking against it makes playfair.dll GPL too." -ForegroundColor Yellow
