# Build the pinned Khronos loader. The headset's active runtime stays user-selected.
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $projectRoot 'sdk/openxr'
$build = Join-Path $projectRoot "artifacts/native-openxr/$Configuration/build"
$output = Join-Path $projectRoot "artifacts/native-openxr/$Configuration/win-x64"
if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) {
    throw 'Initialize the pinned sdk/openxr submodule before building the loader.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$instances = @((& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json) | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0 -or $instances.Count -ne 1) { throw 'Visual Studio x64 C++ build tools are required.' }
$instance = $instances[0]
$generator = switch ([int]$instance.installationVersion.Split('.')[0]) {
    18 { 'Visual Studio 18 2026' }
    17 { 'Visual Studio 17 2022' }
    default { throw 'The OpenXR loader build requires Visual Studio 2022 or 2026.' }
}
& cmake -S $source -B $build -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($instance.installationPath)" `
    -DDYNAMIC_LOADER=ON -DBUILD_LOADER=ON -DBUILD_API_LAYERS=OFF -DBUILD_TESTS=OFF `
    -DBUILD_CONFORMANCE_TESTS=OFF -DBUILD_SDK_TESTS=OFF -DBUILD_TESTING=OFF -DBUILD_ALL_EXTENSIONS=ON `
    -DBUILD_WITH_SYSTEM_JSONCPP=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE -ne 0) { throw 'OpenXR loader configuration failed.' }
& cmake --build $build --config $Configuration --target openxr_loader --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'OpenXR loader compilation failed.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build "src/loader/$Configuration/openxr_loader.dll") -Destination $output -Force
New-Item -ItemType Directory -Path (Join-Path $output 'licenses/openxr') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination (Join-Path $output 'licenses/openxr/LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $source 'src/external/jsoncpp/LICENSE') -Destination (Join-Path $output 'licenses/openxr/jsoncpp-LICENSE.txt') -Force
Write-Host "Built the OpenXR loader: $output"
