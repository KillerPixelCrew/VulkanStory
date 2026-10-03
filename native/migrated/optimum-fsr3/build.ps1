param(
    [string]$SdkRoot = (Join-Path $PSScriptRoot '..\..\_ref\fsr-vulkan'),
    [string]$Output = (Join-Path $PSScriptRoot '..\..\bin\Debug\net10.0\OptimumFsr3.dll')
)
$ErrorActionPreference = 'Stop'
$sdk = (Resolve-Path -LiteralPath $SdkRoot).Path
$header = Join-Path $sdk 'ffx-api\include'
$vulkan = Join-Path $env:VULKAN_SDK 'Include'
if (-not (Test-Path -LiteralPath (Join-Path $header 'ffx_api\ffx_api.h'))) { throw 'FidelityFX SDK v1.1.4 headers are required' }
if (-not (Test-Path -LiteralPath (Join-Path $vulkan 'vulkan\vulkan.h'))) { throw 'Vulkan SDK headers are required' }
$targetDir = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
& g++ -std=c++17 -O2 -shared -static -Wall -Wextra -I $header -I $vulkan (Join-Path $PSScriptRoot 'bridge.cpp') -o $Output
if ($LASTEXITCODE -ne 0) { throw "FSR3 bridge build failed: $LASTEXITCODE" }
