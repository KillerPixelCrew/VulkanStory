param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$VulkanSdkRoot = $env:VULKAN_SDK
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
if (-not $VulkanSdkRoot) { throw 'Specify -VulkanSdkRoot or set VULKAN_SDK.' }
$vulkan = Join-Path (Resolve-Path -LiteralPath $VulkanSdkRoot).Path 'Include'
$compiler = (Get-Command g++.exe -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath (Join-Path $sdk 'Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h'))) {
    throw 'FidelityFX SDK DX12 API headers are required'
}
if (-not (Test-Path -LiteralPath (Join-Path $vulkan 'vulkan/vulkan.h'))) {
    throw 'Vulkan SDK headers are required for DXGI adapter matching'
}
$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$temporary = [IO.Path]::ChangeExtension($target, '.tmp.dll')
& $compiler -std=c++20 -O2 -shared -static -Wall -Wextra -I (Join-Path $sdk 'Kits/FidelityFX') -I $vulkan `
    (Join-Path $PSScriptRoot 'bridge.cpp') -o $temporary -ld3d12 -ldxgi
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw "FSR 4 bridge compilation failed: $LASTEXITCODE"
}
Move-Item -LiteralPath $temporary -Destination $target -Force
