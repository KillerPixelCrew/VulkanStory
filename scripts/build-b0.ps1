[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$VintageStoryPath,
    [string]$Generator = 'Ninja'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$managedArguments = @('build', (Join-Path $projectRoot 'tests/VulkanStory.Bootstrap.Tests/VulkanStory.Bootstrap.Tests.csproj'), '-c', $Configuration)
if ($VintageStoryPath) { $managedArguments += "-p:VintageStoryPath=$VintageStoryPath" }
& dotnet @managedArguments
if ($LASTEXITCODE -ne 0) { throw 'B0 managed build failed.' }
$nativeBuild = Join-Path $projectRoot 'artifacts/native-bootstrap'
$configureArguments = @('-S', (Join-Path $projectRoot 'native/bootstrap'), '-B', $nativeBuild, "-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $configureArguments += @('-G', $Generator) }
if ($Generator -like 'Visual Studio*') { $configureArguments += @('-A', 'x64') }
& cmake @configureArguments
if ($LASTEXITCODE -ne 0) { throw 'B0 native configuration failed.' }
& cmake --build $nativeBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'B0 native build failed.' }
Write-Host 'Built B0. No tests, staging, installation, or game launch were performed by this script.'
