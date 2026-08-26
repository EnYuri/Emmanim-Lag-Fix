param([string]$GameBin)

$ErrorActionPreference = 'Stop'

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
        if (Test-Path -LiteralPath (Join-Path $candidate 'emmanim_lag_fix_loader.json')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw 'Emmanim 로더 설치 기록을 찾지 못했습니다. -GameBin "...\Cosmoteer\Bin"으로 지정해 주십시오.'
}
$resolvedBin = Find-InstalledCosmoteerBin $GameBin
$manifestPath = Join-Path $resolvedBin 'emmanim_lag_fix_loader.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Emmanim 로더 설치 기록을 찾지 못했습니다. 다른 파일을 지우지 않았습니다.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Mod -ne 'nayuri.emmanim_lag_fix') { throw '설치 기록의 모드 ID가 다릅니다.' }

foreach ($name in @('winmm.dll', 'ModLoader.dll')) {
    $target = Join-Path $resolvedBin $name
    if (Test-Path -LiteralPath $target) {
        $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        $expected = $manifest.Files.PSObject.Properties[$name].Value
        if ($actual -ne $expected) {
            throw "$target 파일이 설치 후 변경되었습니다. 안전을 위해 제거하지 않았습니다."
        }
    }
}

foreach ($name in @('winmm.dll', 'ModLoader.dll')) {
    $target = Join-Path $resolvedBin $name
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
}
Remove-Item -LiteralPath $manifestPath
Write-Host "Emmanim 전용 로더를 제거했습니다: $resolvedBin"
