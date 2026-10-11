[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BuildDirectory = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'artifacts/native-nrd-msvc/win-x64'))
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = [IO.Path]::GetFullPath($BuildDirectory)
Get-Command cmake -ErrorAction Stop | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer is required for the NRD host build.' }
$instances = @((& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json) | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $instances.Count -ne 1) { throw 'Visual Studio x64 C++ tools are required.' }
$generator = switch ([int]$instances[0].installationVersion.Split('.')[0]) {
    18 { 'Visual Studio 18 2026' }
    17 { 'Visual Studio 17 2022' }
    default { throw 'The NRD host build requires Visual Studio 2022 or 2026.' }
}
& cmake -S $PSScriptRoot -B $build -G $generator -A x64
if ($LASTEXITCODE -ne 0) { throw "NRD configuration failed: $LASTEXITCODE" }
& cmake --build $build --config Release --target VulkanStoryNrd --parallel 4
if ($LASTEXITCODE -ne 0) { throw "NRD build failed: $LASTEXITCODE" }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'Release/VulkanStoryNrd.dll') -Destination (Join-Path $output 'VulkanStoryNrd.dll')
$notices = Join-Path $output 'licenses/nrd'
New-Item -ItemType Directory -Force -Path $notices | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../sdk/nrd/LICENSE.txt') -Destination (Join-Path $notices 'LICENSE.txt')
foreach ($notice in Get-ChildItem -LiteralPath (Join-Path $build '_deps/mathlib-src') -Filter '*LICENSE*' -File) {
    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $notices ('MathLib-' + $notice.Name))
}
Copy-Item -LiteralPath (Join-Path $build '_deps/shadermake-src/LICENSE.txt') -Destination (Join-Path $notices 'ShaderMake-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $build '_deps/shadermake-src/ThirdPartyLicenses.txt') -Destination (Join-Path $notices 'ShaderMake-ThirdPartyLicenses.txt')
Write-Host "Built NRD Vulkan bridge at $output. No staging, deployment or game launch occurred."
