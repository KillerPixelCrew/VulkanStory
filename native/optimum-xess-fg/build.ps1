param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$include = Join-Path $sdk 'inc'
$vulkan = Join-Path $env:VULKAN_SDK 'Include'
if (-not (Test-Path -LiteralPath (Join-Path $include 'xess_fg/xefg_swapchain_d3d12.h'))) {
    throw 'XeSS 3 SDK frame generation headers are required'
}
if (-not (Test-Path -LiteralPath (Join-Path $vulkan 'vulkan/vulkan.h'))) {
    throw 'Vulkan SDK headers are required for DXGI adapter matching'
}
$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$temporary = [IO.Path]::ChangeExtension($target, '.tmp.dll')
& g++ -std=c++20 -O2 -shared -static -Wall -Wextra -I $include -I $vulkan `
    (Join-Path $PSScriptRoot 'bridge.cpp') -o $temporary -ld3d12 -ldxgi
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw "XeSS-FG bridge compilation failed: $LASTEXITCODE"
}
Move-Item -LiteralPath $temporary -Destination $target -Force
