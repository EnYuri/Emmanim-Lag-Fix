# Builds the GitHub release archive for Emmanim Lag Fix.
#
#   .\Pack.ps1                     package Mod/ as it stands
#   .\Pack.ps1 -RefreshBinaries    first copy freshly built DLLs into Mod/
#
# The archive is the whole distribution: mod data, loader, code module, the
# bundled source tree that satisfies the loader's LGPL obligation, and the
# one-click installer. Extract it anywhere and run Install.bat.

[CmdletBinding()]
param(
    [switch]$RefreshBinaries,
    [string]$OutputDir = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$mod  = Join-Path $repo 'Mod'
$tfm  = 'net10.0-windows7.0'

function Write-Step { param([string]$Text) Write-Host "  $Text" }

# ---------------------------------------------------------------------------
# Version comes from mod.rules, so the archive name can never disagree with
# what the game and the install manifest report.
# ---------------------------------------------------------------------------
$modRules = Join-Path $mod 'mod.rules'
if ((Get-Content -LiteralPath $modRules -Raw) -notmatch '(?m)^\s*Version\s*=\s*([^\r\n;/]+)') {
    throw "Could not read Version from $modRules"
}
$version = $Matches[1].Trim().Trim('"')

Write-Host ''
Write-Host "Packing Emmanim Lag Fix $version" -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# 1. optionally refresh the shipped binaries from the build output
# ---------------------------------------------------------------------------
if ($RefreshBinaries) {
    $binaries = @(
        @{ From = "ModLoader\bin\Release\$tfm\ModLoader.dll";                To = 'Loader\ModLoader.dll' },
        @{ From = 'build\windows\x64\release\winmm.dll';                    To = 'Loader\winmm.dll' },
        @{ From = "EmmanimLagFix.Code\bin\Release\$tfm\EmmanimLagFix.Code.dll"; To = 'Code\EmmanimLagFix.Code.dll' },
        @{ From = "EmmanimLagFix.Code\bin\Release\$tfm\0Harmony.dll";       To = 'Code\0Harmony.dll' }
    )
    foreach ($b in $binaries) {
        $src = Join-Path $repo $b.From
        if (-not (Test-Path -LiteralPath $src)) { throw "Build output missing: $src" }
        Copy-Item -LiteralPath $src -Destination (Join-Path $mod $b.To) -Force
        Write-Step "refreshed $($b.To)"
    }
}

# ---------------------------------------------------------------------------
# 2. regenerate the bundled source tree
#
# LGPL-2.1 obliges us to ship the modified loader source alongside the binary.
# Mirroring it here rather than maintaining a second copy by hand is what keeps
# the bundle from silently drifting behind the code that was actually built.
# ---------------------------------------------------------------------------
$sourceOut = Join-Path $mod 'Source'
if (Test-Path -LiteralPath $sourceOut) { Remove-Item -LiteralPath $sourceOut -Recurse -Force }
New-Item -ItemType Directory -Path $sourceOut -Force | Out-Null

$sourceDirs  = @('ModLoader', 'ModPreLoader', 'EmmanimLagFix.Code', 'CosmoDoorstop')
$sourceFiles = @('ModLoader.sln', 'Directory.Build.props', 'EMMANIM_FORK.md', 'LICENSE.txt')
$excluded    = @('bin', 'obj', '.vs', '.xmake', 'build', 'tools')

foreach ($dir in $sourceDirs) {
    $from = Join-Path $repo $dir
    if (-not (Test-Path -LiteralPath $from)) { throw "Source directory missing: $from" }
    Copy-Item -LiteralPath $from -Destination (Join-Path $sourceOut $dir) -Recurse -Force
    foreach ($drop in $excluded) {
        Get-ChildItem -LiteralPath (Join-Path $sourceOut $dir) -Directory -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq $drop } |
            ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
foreach ($file in $sourceFiles) {
    $from = Join-Path $repo $file
    if (-not (Test-Path -LiteralPath $from)) { throw "Source file missing: $from" }
    Copy-Item -LiteralPath $from -Destination $sourceOut -Force
}
Write-Step 'regenerated Source/'

# ---------------------------------------------------------------------------
# 3. stage and compress
# ---------------------------------------------------------------------------
$required = @(
    'mod.rules', 'README.md', 'logo.png',
    'Install.bat', 'Install.ps1', 'Uninstall.bat', 'Uninstall.ps1',
    'Loader\winmm.dll', 'Loader\ModLoader.dll', 'Loader\LICENSE.LGPL-2.1.txt',
    'Code\EmmanimLagFix.Code.dll', 'Code\0Harmony.dll', 'Code\LICENSE.Harmony.txt'
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $mod $name))) { throw "Package file missing: $name" }
}

if (-not (Test-Path -LiteralPath $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$staging = Join-Path $OutputDir 'package'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$payload = Join-Path $staging 'emmanim_lag_fix'
Copy-Item -LiteralPath $mod -Destination $payload -Recurse -Force

# .workshop pins a Steam Workshop item id and means nothing in a GitHub release.
$workshop = Join-Path $payload '.workshop'
if (Test-Path -LiteralPath $workshop) { Remove-Item -LiteralPath $workshop -Force }

$zip = Join-Path $OutputDir "Emmanim-Lag-Fix-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $payload -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$size = [Math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 2)

Write-Host ''
Write-Host "$zip" -ForegroundColor Green
Write-Host "  $size MB"
Write-Host "  SHA-256 $hash"
Write-Host ''
Write-Host 'Publish with:'
Write-Host "  git tag -a v$version -m ""Emmanim Lag Fix $version"""
Write-Host "  git push origin v$version"
Write-Host "  gh release create v$version ""$zip"" --title ""Emmanim Lag Fix $version"" --notes-file CHANGELOG.md"
Write-Host ''
