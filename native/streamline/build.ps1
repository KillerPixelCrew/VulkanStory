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
