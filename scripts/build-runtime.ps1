[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$VintageStoryPath,
    [string]$Generator = 'Ninja',
    [switch]$SkipShaders
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
# Bootstrap uses reflection to load Game; build it explicitly, without a test project.
foreach ($project in @('VulkanStory.Bootstrap', 'VulkanStory.Game', 'VulkanStory.Mod', 'VulkanStory.Input.Companion')) {
    $arguments = @('build', (Join-Path $projectRoot "src/$project/$project.csproj"), '-c', $Configuration)
    if ($VintageStoryPath) { $arguments += "-p:VintageStoryPath=$VintageStoryPath" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Managed build failed: $project" }
}
if (-not $SkipShaders) {
    $shaderProject = Join-Path $projectRoot 'tools/VulkanStory.Shaders.Compiler/VulkanStory.Shaders.Compiler.csproj'
    & dotnet build $shaderProject -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Shader compiler build failed.' }
    $shaderTool = Join-Path $projectRoot "tools/VulkanStory.Shaders.Compiler/bin/$Configuration/net10.0/VulkanStory.Shaders.Compiler.dll"
    & dotnet $shaderTool --build (Join-Path $projectRoot 'shaders/native') (Join-Path $projectRoot "artifacts/runtime-shaders/$Configuration")
    if ($LASTEXITCODE -ne 0) { throw 'Full native shader compilation failed.' }
}
$nativeBuild = Join-Path $projectRoot 'artifacts/native-bootstrap'
$arguments = @('-S', (Join-Path $projectRoot 'native/bootstrap'), '-B', $nativeBuild, "-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $arguments += @('-G', $Generator) }
if ($Generator -like 'Visual Studio*') { $arguments += @('-A', 'x64') }
& cmake @arguments
if ($LASTEXITCODE -ne 0) { throw 'Native bootstrap configuration failed.' }
& cmake --build $nativeBuild --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Native bootstrap build failed.' }
Write-Host 'Built production managed projects and bootstrap; full shaders compile unless SkipShaders is selected. Use build-provider-bridges.ps1 for native provider builds. No tests, staging, installation or game launch ran.'
