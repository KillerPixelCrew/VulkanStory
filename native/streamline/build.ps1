<#
.SYNOPSIS
Compiles the retained Streamline Vulkan bridge with its compiler runtime linked statically.
.DESCRIPTION
Checks Streamline/Vulkan headers, invokes g++ in C++20 mode with wintrust/advapi32 linkage, and builds a sibling temporary DLL. Successful compilation replaces Output; failure removes temporary output and throws. SDK plugin binaries are supplied separately by native bundle preparation.
.PARAMETER SdkRoot
Streamline SDK root containing include/sl.h.
.PARAMETER Output
Target bridge DLL path; parent directories are created and successful output replaces the file.
.PARAMETER VulkanSdkRoot
Vulkan SDK root containing Include/vulkan/vulkan.h; defaults to VULKAN_SDK.
#>
param(
    [Parameter(Mandatory = $true)][string]$SdkRoot,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$VulkanSdkRoot = $env:VULKAN_SDK
)

$ErrorActionPreference = 'Stop'
$compiler = (Get-Command g++.exe -ErrorAction Stop).Source
if (-not $VulkanSdkRoot) { throw 'Specify -VulkanSdkRoot or set VULKAN_SDK.' }

$source = Join-Path $PSScriptRoot 'bridge.cpp'
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$vulkanSdk = (Resolve-Path -LiteralPath $VulkanSdkRoot).Path
$include = Join-Path $sdk 'include'
$vulkanInclude = Join-Path $vulkanSdk 'Include'
if (-not (Test-Path -LiteralPath (Join-Path $include 'sl.h'))) {
    throw 'Streamline headers are required.'
}
if (-not (Test-Path -LiteralPath (Join-Path $vulkanInclude 'vulkan\vulkan.h'))) {
    throw 'Vulkan SDK headers are required.'
}

$target = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
$temporary = [IO.Path]::ChangeExtension($target, '.tmp.dll')
& $compiler -std=c++20 -O2 -shared -static -I $include -I $vulkanInclude $source -o $temporary -lwintrust -ladvapi32
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw 'Streamline bridge compilation failed.'
}
Move-Item -LiteralPath $temporary -Destination $target -Force
Write-Host "vulkanstory-streamline: built $target"
