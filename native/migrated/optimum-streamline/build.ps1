param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$Output
)

$ErrorActionPreference = 'Stop'
$compiler = Get-Command g++.exe -ErrorAction SilentlyContinue
if (-not $compiler -or -not $env:VULKAN_SDK) {
    Write-Warning 'C++ compiler or Vulkan SDK missing; Streamline bridge unavailable.'
    exit 0
}

$source = Join-Path $PSScriptRoot 'bridge.cpp'
$include = Join-Path $SdkRoot 'include'
$vulkanInclude = Join-Path $env:VULKAN_SDK 'Include'
if (-not (Test-Path -LiteralPath (Join-Path $include 'sl.h'))) {
    Write-Warning 'Streamline headers missing; Streamline bridge unavailable.'
    exit 0
}

$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$temporary = [IO.Path]::ChangeExtension($target, '.tmp.dll')
& $compiler.Source -std=c++20 -O2 -shared -I $include -I $vulkanInclude $source -o $temporary -lwintrust -ladvapi32
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw 'Streamline bridge compilation failed.'
}
Move-Item -LiteralPath $temporary -Destination $target -Force
Write-Host "optimum-streamline: built $target"
