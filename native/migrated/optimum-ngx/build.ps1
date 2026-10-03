param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$compiler = Get-Command gcc.exe -ErrorAction SilentlyContinue
if (-not $compiler) { $compiler = Get-Command cc.exe -ErrorAction SilentlyContinue }

$output = [IO.Path]::GetFullPath($OutputDirectory)
$target = Join-Path $output 'OptimumNgx.dll'
$temporary = Join-Path $output 'OptimumNgx.tmp.dll'
if (-not $compiler) {
    Write-Warning 'No C compiler found; DLSS will be unavailable on this build.'
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    exit 0
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$source = Join-Path $PSScriptRoot 'optimum_ngx.c'
$header = Join-Path $PSScriptRoot 'optimum_ngx.h'
if ((Test-Path -LiteralPath $target) -and
    (Get-Item -LiteralPath $target).LastWriteTimeUtc -gt (Get-Item -LiteralPath $source).LastWriteTimeUtc -and
    (Get-Item -LiteralPath $target).LastWriteTimeUtc -gt (Get-Item -LiteralPath $header).LastWriteTimeUtc) {
    exit 0
}

& $compiler.Source -std=c99 -O2 -fno-optimize-sibling-calls -shared $source -o $temporary
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }
    Write-Warning 'The NGX shim failed to compile; DLSS will be unavailable.'
    exit 0
}
Move-Item -LiteralPath $temporary -Destination $target -Force
Write-Host "optimum-ngx: built $target"
