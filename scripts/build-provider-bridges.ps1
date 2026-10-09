<#
.SYNOPSIS
Builds the five Windows x64 provider bridges into a fresh output directory.
.DESCRIPTION
Requires Windows x64 PowerShell 7, declared SDK headers, g++, and gcc or cc. Validates inputs before creating OutputDirectory, then invokes the retained NGX, FSR3, FSR4, XeSS-FG, and Streamline build entries. Vendor redistributable runtimes/notices are separate inputs; a partially populated output may remain if a later build fails.
.PARAMETER Fsr3SdkRoot
FidelityFX SDK root containing ffx-api/include/ffx_api/ffx_api.h; defaults to the sdk/fidelityfx-vk submodule (v1.1.4).
.PARAMETER Fsr4SdkRoot
FidelityFX DX12 SDK root containing Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h; defaults to the sdk/fidelityfx submodule (v2.3.0).
.PARAMETER XessSdkRoot
XeSS SDK root containing inc/xess_fg/xefg_swapchain_d3d12.h; defaults to the sdk/xess submodule.
.PARAMETER StreamlineSdkRoot
Streamline SDK root containing include/sl.h; defaults to the sdk/streamline submodule (v2.14.1).
.PARAMETER OutputDirectory
Fresh destination for the five compiled bridge DLLs; an existing directory is rejected.
.PARAMETER VulkanSdkRoot
Vulkan SDK root containing Include/vulkan/vulkan.h; defaults to VULKAN_SDK.
#>
[CmdletBinding()]
param(
    [string]$Fsr3SdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/fidelityfx-vk'),
    [string]$Fsr4SdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/fidelityfx'),
    [string]$XessSdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/xess'),
    [string]$StreamlineSdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/streamline'),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$VulkanSdkRoot = $env:VULKAN_SDK
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'This bridge build entry requires Windows x64 PowerShell 7.'
}
if (-not $VulkanSdkRoot) { throw 'Specify -VulkanSdkRoot or set VULKAN_SDK.' }
$vulkanRoot = (Resolve-Path -LiteralPath $VulkanSdkRoot).Path
$headers = [ordered]@{
    (Join-Path $vulkanRoot 'Include/vulkan/vulkan.h') = 'Vulkan'
    (Join-Path $Fsr3SdkRoot 'ffx-api/include/ffx_api/ffx_api.h') = 'FidelityFX 1.1.4'
    (Join-Path $Fsr4SdkRoot 'Kits/FidelityFX/api/include/dx12/ffx_api_dx12.h') = 'FidelityFX DX12'
    (Join-Path $XessSdkRoot 'inc/xess_fg/xefg_swapchain_d3d12.h') = 'XeSS FG'
    (Join-Path $StreamlineSdkRoot 'include/sl.h') = 'Streamline'
}
foreach ($header in $headers.Keys) {
    if (-not (Test-Path -LiteralPath $header -PathType Leaf)) { throw "Missing $($headers[$header]) header: $header" }
}
Get-Command g++.exe -ErrorAction Stop | Out-Null
if (-not (Get-Command gcc.exe -ErrorAction SilentlyContinue) -and
    -not (Get-Command cc.exe -ErrorAction SilentlyContinue)) { throw 'NGX requires gcc.exe or cc.exe.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh bridge output directory; stale binaries are not merged.' }
New-Item -ItemType Directory -Path $output | Out-Null
& (Join-Path $projectRoot 'native/ngx/build.ps1') -OutputDirectory $output
$builds = @(
    @{ Directory='fsr3'; Sdk=$Fsr3SdkRoot; Binary='VulkanStoryFsr3.dll' },
    @{ Directory='fsr4'; Sdk=$Fsr4SdkRoot; Binary='VulkanStoryFsr4.dll' },
    @{ Directory='xess-fg'; Sdk=$XessSdkRoot; Binary='VulkanStoryXessFg.dll' },
    @{ Directory='streamline'; Sdk=$StreamlineSdkRoot; Binary='VulkanStoryStreamline.dll' }
)
foreach ($build in $builds) {
    & (Join-Path $projectRoot "native/$($build.Directory)/build.ps1") -SdkRoot $build.Sdk `
        -VulkanSdkRoot $vulkanRoot -Output (Join-Path $output $build.Binary)
}
Write-Host "Built provider bridges at $output. Vendor runtimes/notices are separate redistributable inputs. No tests, staging, installation or game launch ran."
