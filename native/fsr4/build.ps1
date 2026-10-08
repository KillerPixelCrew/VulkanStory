<#
.SYNOPSIS
Compiles the retained FSR4 Vulkan/DX12 bridge DLL.
.DESCRIPTION
Checks FidelityFX DX12/Vulkan headers and invokes g++ in C++20 mode with static compiler runtime and d3d12/dxgi linkage. Builds a sibling temporary DLL, replacing Output only after success; compilation failure removes the temporary and throws.
.PARAMETER SdkRoot
FidelityFX SDK root containing Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h.
.PARAMETER Output
Target bridge DLL path; a successful build replaces this file.
.PARAMETER VulkanSdkRoot
Vulkan SDK root supplying Include headers for adapter matching; defaults to VULKAN_SDK.
#>
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
