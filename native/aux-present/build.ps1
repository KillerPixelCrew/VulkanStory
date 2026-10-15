param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$target=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
$temporary=[IO.Path]::ChangeExtension($target,'.tmp.dll')
& g++.exe -std=c++20 -O2 -shared -static -Wall -Wextra (Join-Path $PSScriptRoot 'bridge.cpp') -o $temporary -ld3d12 -ldxgi
if($LASTEXITCODE -ne 0){throw 'Auxiliary DXGI presenter compilation failed.'}
Move-Item -LiteralPath $temporary -Destination $target -Force
