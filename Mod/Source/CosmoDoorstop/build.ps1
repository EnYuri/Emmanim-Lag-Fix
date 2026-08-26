param (
    [Parameter(Mandatory = $false)]
    [switch] $with_logging = $false,
    [ValidateSet("x86", "x64")]
    [string[]]
    $Arch = @("x86", "x64"),
    [Parameter(Position = 0, Mandatory = $false, ValueFromRemainingArguments = $true)]
    [string[]]
    $ScriptArgs
)

$VERSION = "3.0.8"

function writeErrorTip($msg) {
    Write-Host $msg -BackgroundColor Red -ForegroundColor White
}

[Net.ServicePointManager]::SecurityProtocol = "tls12, tls11, tls"

$TOOLS_DIR = Join-Path $PSScriptRoot "tools"
$XMAKE_DIR = Join-Path $TOOLS_DIR "xmake"

if ((Test-Path $PSScriptRoot) -and !(Test-Path $TOOLS_DIR)) {
    Write-Verbose -Message "Creating tools dir..."
    New-Item -Path $TOOLS_DIR -ItemType "directory" | Out-Null
}

if (!(Test-Path $XMAKE_DIR)) {
    $outfile = Join-Path $TOOLS_DIR "$pid-xmake.zip"
    $x64arch = @('AMD64', 'IA64', 'ARM64')
    $os_arch = if ($env:PROCESSOR_ARCHITECTURE -in $x64arch -or $env:PROCESSOR_ARCHITEW6432 -in $x64arch) { 'win64' } else { 'win32' }
    $url = "https://github.com/xmake-io/xmake/releases/download/v$VERSION/xmake-v$VERSION.$os_arch.zip"
    Write-Host "Downloading xmake ($os_arch) from $url..."

    try {
        Invoke-WebRequest $url -OutFile $outfile -UseBasicParsing
    }
    catch {
        writeErrorTip "Download failed!"
        throw
    }
    
    try {
        Expand-Archive -Path $outfile -DestinationPath $TOOLS_DIR
    }
    catch {
        writeErrorTip "Failed to extract!"
        throw
    }
    finally {
        Remove-Item -Path $outfile
    }
}

$XMAKE_EXE = Join-Path $XMAKE_DIR "xmake.exe"
foreach ($a in $Arch) {
    $verbose_opt = if ($with_logging) { "--include_logging=y" } else { "--include_logging=n" }
    & $XMAKE_EXE f -P $PSScriptRoot -a $a $verbose_opt
    if ($LASTEXITCODE -ne 0) { throw "xmake configure failed with exit code $LASTEXITCODE" }
    & $XMAKE_EXE -P $PSScriptRoot @ScriptArgs
    if ($LASTEXITCODE -ne 0) { throw "xmake build failed with exit code $LASTEXITCODE" }
}
