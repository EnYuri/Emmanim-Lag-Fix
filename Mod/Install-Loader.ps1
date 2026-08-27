param([string]$GameBin)

$ErrorActionPreference = 'Stop'
$sourceDir = Join-Path $PSScriptRoot 'Loader'
$required = @('winmm.dll', 'ModLoader.dll')

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
    throw 'Cosmoteer\Bin을 찾지 못했습니다. -GameBin "...\Cosmoteer\Bin"으로 지정해 주십시오.'
}

$resolvedBin = Find-CosmoteerBin $GameBin
foreach ($name in $required) {
    $source = Join-Path $sourceDir $name
    $target = Join-Path $resolvedBin $name
    if (-not (Test-Path -LiteralPath $source)) { throw "배포 파일이 없습니다: $source" }
    if (Test-Path -LiteralPath $target) {
        $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            $manifestPath = Join-Path $resolvedBin 'emmanim_lag_fix_loader.json'
            if (-not (Test-Path -LiteralPath $manifestPath)) {
                throw "$target 에 소유권을 확인할 수 없는 다른 로더가 있습니다. 자동으로 덮어쓰지 않았습니다."
            }
            $oldManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $ownedHash = $oldManifest.Files.$name
            if (-not $ownedHash -or $ownedHash -ne $targetHash) {
                throw "$target 이 설치 후 변경되었습니다. 자동으로 덮어쓰지 않았습니다."
            }
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    } else {
        Copy-Item -LiteralPath $source -Destination $target
    }
}

$manifest = @{
    Mod = 'nayuri.emmanim_lag_fix'
    Version = '2.0.5'
    Files = @{}
}
foreach ($name in $required) {
    $manifest.Files[$name] = (Get-FileHash -LiteralPath (Join-Path $resolvedBin $name) -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $resolvedBin 'emmanim_lag_fix_loader.json') -Encoding utf8
Write-Host "Emmanim 전용 로더를 설치했습니다: $resolvedBin"
