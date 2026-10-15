param([Parameter(Mandatory)][string]$Output,[string]$VulkanSdkRoot=$env:VULKAN_SDK)
$ErrorActionPreference='Stop'
if(-not $VulkanSdkRoot){throw 'Vulkan SDK headers are required.'}
$target=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
$temporary=[IO.Path]::ChangeExtension($target,'.tmp.dll')
& g++.exe -std=c++20 -O2 -shared -static -Wall -Wextra -Wno-missing-field-initializers -I (Join-Path $VulkanSdkRoot 'Include') (Join-Path $PSScriptRoot 'layer.cpp') -o $temporary
if($LASTEXITCODE -ne 0){throw 'Vulkan presentation layer compilation failed.'}
Move-Item -LiteralPath $temporary -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VulkanStoryWsi.json') -Destination (Join-Path (Split-Path $target) 'VulkanStoryWsi.json') -Force
