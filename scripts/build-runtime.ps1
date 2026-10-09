<#
.SYNOPSIS
Builds production managed projects, native bootstrap, and the maintained shader corpus.
.DESCRIPTION
Runs dotnet builds for Bootstrap, Game, Mod, and Input.Companion. Windows additionally builds the CMake activation proxy; Linux uses the original run.sh startup-hook integration and has no native activation proxy. Unless SkipShaders is set, it builds/runs the shader CLI into artifacts/runtime-shaders/<Configuration>: the loose shaders-vk directory and the shipped single-file shaders-vk.pak that stage-runtime.ps1 -ShaderPack takes. Native provider bridges are built separately. Any checked external failure terminates the script; no tests, staging, deployment, or game launch are performed.
.PARAMETER Configuration
Debug or Release configuration used for managed, shader-tool, and native bootstrap outputs.
.PARAMETER VintageStoryPath
Optional official game assembly directory passed through the VintageStoryPath MSBuild property.
.PARAMETER Generator
CMake generator, default Ninja; Visual Studio generators select x64, and an empty value omits the explicit generator.
.PARAMETER SkipShaders
Skips both the shader-tool build and the complete native shader compilation.
.PARAMETER RuntimeIdentifier
win-x64 or linux-x64, defaulting to the current host. Windows keeps its established managed output layout; Linux outputs use net10.0/linux-x64. Native provider builds and redistribution inputs remain explicit separate work.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$VintageStoryPath,
    [string]$Generator = 'Ninja',
    [switch]$SkipShaders,
    [ValidateSet('win-x64','linux-x64')][string]$RuntimeIdentifier = $(if ($IsLinux) { 'linux-x64' } else { 'win-x64' })
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
# Bootstrap uses reflection to load Game; build it explicitly, without a test project.
foreach ($project in @('VulkanStory.Bootstrap', 'VulkanStory.Game', 'VulkanStory.Mod', 'VulkanStory.Input.Companion')) {
    $arguments = @('build', (Join-Path $projectRoot "src/$project/$project.csproj"), '-c', $Configuration)
    if ($RuntimeIdentifier -eq 'linux-x64') { $arguments += @('-r', $RuntimeIdentifier, '-p:SelfContained=false', '-p:IsRidAgnostic=false') }
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
if ($RuntimeIdentifier -eq 'linux-x64') {
    Write-Host 'Built Linux managed runtime and requested shaders; Windows activation proxy was skipped. Build native/ngx/build.sh separately if supplying the optional NGX shim. No installation or game launch ran.'
    return
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
