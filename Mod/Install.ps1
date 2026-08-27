# Emmanim Lag Fix - one-click installer.
#
# Installs two independent halves:
#   1. the mod folder itself, into the game's user Mods folder;
#   2. the optional code loader (winmm.dll + ModLoader.dll), into Cosmoteer\Bin.
#
# Run Install.bat rather than calling this directly; the batch wrapper supplies
# the execution policy that an unsigned downloaded script otherwise lacks.

[CmdletBinding()]
param(
    [string]$GameBin,       # ...\Cosmoteer\Bin        (auto-detected when omitted)
    [string]$ModsFolder,    # ...\Cosmoteer\<id>\Mods  (auto-detected when omitted)
    [switch]$NoLoader,      # install the .rules mod only
    [switch]$LoaderOnly,    # install the loader only
    [switch]$Elevated       # internal: set on the self-elevated relaunch
)

$ErrorActionPreference = 'Stop'
$packageRoot   = $PSScriptRoot
$loaderFiles   = @('winmm.dll', 'ModLoader.dll')
$modFolderName = 'emmanim_lag_fix'
$modId         = 'nayuri.emmanim_lag_fix'
$manifestName  = 'emmanim_lag_fix_loader.json'
$savedGamesGuid = '{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}'

function Write-Step { param([string]$Text) Write-Host "  $Text" }

# ---------------------------------------------------------------------------
# The game must not be running: it rewrites settings.rules on exit and holds
# the loader DLLs open.
# ---------------------------------------------------------------------------
if (Get-Process -Name 'Cosmoteer' -ErrorAction SilentlyContinue) {
    throw 'Cosmoteer is running. Close the game completely and run this again.'
}

# ---------------------------------------------------------------------------
# Package integrity, version, and Mark of the Web
# ---------------------------------------------------------------------------
$modRules = Join-Path $packageRoot 'mod.rules'
if (-not (Test-Path -LiteralPath $modRules)) {
    throw "mod.rules not found. Run this from inside the extracted folder: $packageRoot"
}
$version = 'unknown'
if ((Get-Content -LiteralPath $modRules -Raw) -match '(?m)^\s*Version\s*=\s*([^\r\n;/]+)') {
    $version = $Matches[1].Trim().Trim('"')
}

# Files extracted from a downloaded archive carry a zone marker. Bypass covers
# the scripts; clearing the marker outright also keeps the copied DLLs clean.
Get-ChildItem -LiteralPath $packageRoot -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue

# ---------------------------------------------------------------------------
# Locate Cosmoteer\Bin through Steam's own library registry
# ---------------------------------------------------------------------------
function Find-CosmoteerBin {
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
        if (Test-Path -LiteralPath (Join-Path $candidate 'Cosmoteer.exe')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw 'Could not find Cosmoteer\Bin. Pass -GameBin "...\Cosmoteer\Bin" from PowerShell.'
}

# ---------------------------------------------------------------------------
# Locate the user Mods folder.
#
# This mirrors Cosmoteer.Paths.GetDefaultRootSavePath exactly: the literal
# %USERPROFILE%\Saved Games path wins WHEN IT EXISTS, and only otherwise does
# the game fall back to the Saved Games known folder -- which Windows folder
# redirection may point at another drive entirely. Probing them in the other
# order would install into a folder the game does not read.
# ---------------------------------------------------------------------------
function Find-UserModsFolder {
    param([string]$ExplicitPath)

    if ($ExplicitPath) {
        if (-not (Test-Path -LiteralPath $ExplicitPath)) {
            New-Item -ItemType Directory -Path $ExplicitPath -Force | Out-Null
        }
        return (Resolve-Path -LiteralPath $ExplicitPath).Path
    }

    $roots = [System.Collections.Generic.List[string]]::new()
    $roots.Add((Join-Path $env:USERPROFILE 'Saved Games\Cosmoteer'))

    $shellKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders',
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders'
    )
    foreach ($key in $shellKeys) {
        $raw = (Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).$savedGamesGuid
        if ($raw) { $roots.Add((Join-Path ([Environment]::ExpandEnvironmentVariables($raw)) 'Cosmoteer')) }
    }

    foreach ($root in $roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $root)) { continue }

        # Steam builds append the SteamID64. Pick the profile whose settings
        # file was written most recently when several exist.
        $profiles = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d{17}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'settings.rules')) } |
            Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'settings.rules')).LastWriteTimeUtc } -Descending

        $base = $null
        if ($profiles) { $base = $profiles[0].FullName }
        elseif (Test-Path -LiteralPath (Join-Path $root 'settings.rules')) { $base = $root }
        if (-not $base) { continue }

        $mods = Join-Path $base 'Mods'
        if (-not (Test-Path -LiteralPath $mods)) { New-Item -ItemType Directory -Path $mods -Force | Out-Null }
        return (Resolve-Path -LiteralPath $mods).Path
    }

    throw 'Could not find the game user folder. Run Cosmoteer once, then try again, or pass -ModsFolder explicitly.'
}

# ---------------------------------------------------------------------------
# Elevation: needed only when the Steam library sits somewhere protected,
# such as a default install under Program Files.
# ---------------------------------------------------------------------------
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
Write-Host "Emmanim Lag Fix $version" -ForegroundColor Cyan
Write-Host ''

$resolvedBin = $null
if (-not $NoLoader) {
    $resolvedBin = Find-CosmoteerBin $GameBin
    if (-not (Test-Writable $resolvedBin)) {
        if ($Elevated) { throw "No write access to $resolvedBin." }
        Write-Step 'Administrator rights are required. Relaunching elevated...'
        $quote = [char]34
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit',
                     '-File', ($quote + $PSCommandPath + $quote), '-Elevated')
        if ($GameBin)    { $argList += @('-GameBin',    ($quote + $GameBin + $quote)) }
        if ($ModsFolder) { $argList += @('-ModsFolder', ($quote + $ModsFolder + $quote)) }
        if ($LoaderOnly) { $argList += '-LoaderOnly' }
        Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $argList -Verb RunAs | Out-Null
        return
    }
}

# ---------------------------------------------------------------------------
# 1. the mod folder
# ---------------------------------------------------------------------------
$installedModFolder = $null
if (-not $LoaderOnly) {
    $mods = Find-UserModsFolder $ModsFolder
    $target = Join-Path $mods $modFolderName
    $sourceFull = (Resolve-Path -LiteralPath $packageRoot).Path.TrimEnd('\')

    if ($sourceFull -ieq $target.TrimEnd('\')) {
        Write-Step "Mod folder is already in place: $target"
    }
    else {
        if (Test-Path -LiteralPath $target) {
            # Only ever replace a folder that is demonstrably this mod.
            $existing = Join-Path $target 'mod.rules'
            if (-not (Test-Path -LiteralPath $existing) -or
                (Get-Content -LiteralPath $existing -Raw) -notmatch [regex]::Escape($modId)) {
                throw "$target holds something else. Nothing was deleted."
            }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        Copy-Item -LiteralPath $sourceFull -Destination $target -Recurse -Force
        Write-Step "Installed the mod: $target"
    }
    $installedModFolder = $target
}

# ---------------------------------------------------------------------------
# 2. the code loader
# ---------------------------------------------------------------------------
if (-not $NoLoader) {
    $sourceDir = Join-Path $packageRoot 'Loader'
    foreach ($name in $loaderFiles) {
        $source = Join-Path $sourceDir $name
        $dest   = Join-Path $resolvedBin $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing distribution file: $source" }

        if (Test-Path -LiteralPath $dest) {
            $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
            $destHash   = (Get-FileHash -LiteralPath $dest   -Algorithm SHA256).Hash
            if ($sourceHash -eq $destHash) { continue }

            # Never clobber a winmm.dll this installer did not place: it is very
            # likely another mod loader or an unrelated proxy DLL.
            $manifestPath = Join-Path $resolvedBin $manifestName
            if (-not (Test-Path -LiteralPath $manifestPath)) {
                throw "$dest is a loader this installer does not own. Nothing was overwritten."
            }
            $owned = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).Files.$name
            if (-not $owned -or $owned -ne $destHash) {
                throw "$dest changed since it was installed. Nothing was overwritten."
            }
        }
        Copy-Item -LiteralPath $source -Destination $dest -Force
    }

    $manifest = @{ Mod = $modId; Version = $version; Files = @{} }
    if ($installedModFolder) { $manifest.ModFolder = $installedModFolder }
    foreach ($name in $loaderFiles) {
        $manifest.Files[$name] = (Get-FileHash -LiteralPath (Join-Path $resolvedBin $name) -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolvedBin $manifestName) -Encoding utf8
    Write-Step "Installed the code loader: $resolvedBin"
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if (-not $LoaderOnly) {
    Write-Host 'Start the game and enable Emmanim Lag Fix under Options > Mods.'
    Write-Host 'Multiplayer is lockstep: every player needs the same version of the .rules half.'
    Write-Host 'The loader is per-player and optional; you stay in sync with peers who skip it.'
}
Write-Host ''
