<#
.SYNOPSIS
Builds the required Windows x64 VMA allocator bridge with a static C++ runtime.
.DESCRIPTION
Uses unmodified headers from the sdk/vma submodule and VULKAN_SDK. No Vulkan loader
library is linked; the renderer supplies its Vulkan dispatch at runtime. Replaces
Output only after successful compilation. Does not stage, deploy or launch.
Copies the VMA MIT notice beside the output under licenses/vma/LICENSE.txt.
.PARAMETER Output
Destination VulkanStoryVma.dll.
.PARAMETER VulkanSdkRoot
Vulkan SDK root containing Include/vulkan/vulkan.h; defaults to VULKAN_SDK.
.PARAMETER Configuration
Debug or Release compiler optimization and symbols.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Output,
    [string]$VulkanSdkRoot = $env:VULKAN_SDK,
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The VMA Windows build requires Windows x64 PowerShell 7; use native/vma/build.sh on Linux.'
}
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$vmaInclude = Join-Path $projectRoot 'sdk/vma/include'
if (-not (Test-Path -LiteralPath (Join-Path $vmaInclude 'vk_mem_alloc.h') -PathType Leaf)) {
    throw "Missing sdk/vma submodule header. Run 'git submodule update --init sdk/vma'."
}
if (-not $VulkanSdkRoot) { throw 'Specify -VulkanSdkRoot or set VULKAN_SDK.' }
$vulkanInclude = Join-Path (Resolve-Path -LiteralPath $VulkanSdkRoot).Path 'Include'
if (-not (Test-Path -LiteralPath (Join-Path $vulkanInclude 'vulkan/vulkan.h') -PathType Leaf)) {
    throw 'Vulkan SDK headers are required.'
}
$compiler = (Get-Command g++.exe -ErrorAction Stop).Source
$target = [IO.Path]::GetFullPath($Output)
$temporary = "$target.tmp.dll"
New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
$optimization = if ($Configuration -eq 'Debug') { @('-O0', '-g') } else { @('-O2', '-DNDEBUG') }
& $compiler -std=c++17 @optimization -shared -static -Wall -Wextra -Wno-unused-parameter `
    -I $vmaInclude -I $vulkanInclude (Join-Path $PSScriptRoot 'bridge.cpp') -o $temporary
if ($LASTEXITCODE -ne 0) {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw "VMA bridge build failed: $LASTEXITCODE"
}
Move-Item -LiteralPath $temporary -Destination $target -Force
$noticeDirectory = Join-Path (Split-Path $target -Parent) 'licenses/vma'
New-Item -ItemType Directory -Path $noticeDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'sdk/vma/LICENSE.txt') -Destination (Join-Path $noticeDirectory 'LICENSE.txt') -Force
Write-Host "vulkanstory-vma: built $target"
