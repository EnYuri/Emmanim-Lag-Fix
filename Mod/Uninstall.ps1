# Emmanim Lag Fix - uninstaller.
#
# Removes the code loader from Cosmoteer\Bin, and optionally the installed mod
# folder. Every deletion is guarded by the manifest this mod wrote at install
# time, so a file the installer did not place is never removed.
#
# Run Uninstall.bat rather than calling this directly.

[CmdletBinding()]
param(
    [string]$GameBin,     # ...\Cosmoteer\Bin (auto-detected when omitted)
    [switch]$KeepMod,     # remove the loader only
    [switch]$Elevated     # internal: set on the self-elevated relaunch
)

$ErrorActionPreference = 'Stop'
$loaderFiles  = @('winmm.dll', 'ModLoader.dll')
$modId        = 'nayuri.emmanim_lag_fix'
$manifestName = 'emmanim_lag_fix_loader.json'

function Write-Step { param([string]$Text) Write-Host "  $Text" }

if (Get-Process -Name 'Cosmoteer' -ErrorAction SilentlyContinue) {
    throw 'Cosmoteer is running. Close the game completely and run this again.'
}

# The install manifest is what identifies our Bin folder, so look for it rather
# than for Cosmoteer.exe: an uninstall has nothing to do in an untouched copy.
function Find-InstalledCosmoteerBin {
    param([string]$ExplicitPath)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($ExplicitPath) { $candidates.Add($ExplicitPath) }
    $steamPath = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steamPath) {
        $candidates.Add((Join-Path $steamPath 'steamapps\common\Cosmoteer\Bin'))
        $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $library = $match.Groups[1].Value.Replace('\\', '\')
                $candidates.Add((Join-Path $library 'steamapps\common\Cosmoteer\Bin'))
            }
        }
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path -LiteralPath (Join-Path $candidate $manifestName)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Test-Writable {
    param([string]$Folder)
    $probe = Join-Path $Folder ('.emmanim_write_test_' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe -Force
        return $true
    }
    catch { return $false }
}

Write-Host ''
Write-Host 'Emmanim Lag Fix uninstaller' -ForegroundColor Cyan
Write-Host ''

$resolvedBin = Find-InstalledCosmoteerBin $GameBin
if (-not $resolvedBin) {
    Write-Step 'No loader install record found. The Bin folder was left untouched.'
}
else {
    if (-not (Test-Writable $resolvedBin)) {
        if ($Elevated) { throw "No write access to $resolvedBin." }
        Write-Step 'Administrator rights are required. Relaunching elevated...'
        $quote = [char]34
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit',
                     '-File', ($quote + $PSCommandPath + $quote), '-Elevated')
        if ($GameBin) { $argList += @('-GameBin', ($quote + $GameBin + $quote)) }
        if ($KeepMod) { $argList += '-KeepMod' }
        Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs | Out-Null
        return
    }

    $manifestPath = Join-Path $resolvedBin $manifestName
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.Mod -ne $modId) { throw 'The install record names a different mod ID.' }

    # Verify every hash before deleting anything, so a partial removal cannot
    # leave one half of the proxy pair behind.
    foreach ($name in $loaderFiles) {
        $target = Join-Path $resolvedBin $name
        if (-not (Test-Path -LiteralPath $target)) { continue }
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        $expected = $manifest.Files.PSObject.Properties[$name].Value
        if ($actual -ne $expected) {
            throw "$target changed since it was installed. It was left in place for safety."
        }
    }
    foreach ($name in $loaderFiles) {
        $target = Join-Path $resolvedBin $name
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    }
    Remove-Item -LiteralPath $manifestPath -Force
    Write-Step "Removed the code loader: $resolvedBin"

    if (-not $KeepMod -and $manifest.ModFolder) {
        $modFolder = [string]$manifest.ModFolder
        $modRules = Join-Path $modFolder 'mod.rules'
        if ((Test-Path -LiteralPath $modRules) -and
            (Get-Content -LiteralPath $modRules -Raw) -match [regex]::Escape($modId)) {
            # Not while the uninstaller is running from inside the folder it
            # would delete.
            $here = (Resolve-Path -LiteralPath $PSScriptRoot).Path.TrimEnd('\')
            if ($here -ieq $modFolder.TrimEnd('\')) {
                Write-Step "Left the mod folder in place because this script is running from it: $modFolder"
            }
            else {
                Remove-Item -LiteralPath $modFolder -Recurse -Force
                Write-Step "Removed the mod folder: $modFolder"
            }
        }
    }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host ''
