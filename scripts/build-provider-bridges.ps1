[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Fsr3SdkRoot,
    [Parameter(Mandatory)][string]$Fsr4SdkRoot,
    [Parameter(Mandatory)][string]$XessSdkRoot,
    [Parameter(Mandatory)][string]$StreamlineSdkRoot,
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
