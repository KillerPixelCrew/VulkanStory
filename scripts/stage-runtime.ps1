[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory)][string]$NativeDirectory,
    [Parameter(Mandatory)][string]$NativeLicensesDirectory,
    [Parameter(Mandatory)][string]$ManagedLicensesDirectory,
    [Parameter(Mandatory)][string]$ShadersDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$stage = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $stage) { throw 'Choose a fresh output directory; staging never merges or deletes packages.' }
$sources = [ordered]@{}
function Add-Payload([string]$relative, [string]$source) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $source" }
    if ($sources.Contains($relative)) { throw "Duplicate package destination: $relative" }
    $sources[$relative] = [IO.Path]::GetFullPath($source)
}
function Add-Tree([string]$relative, [string]$directory) {
    $root = [IO.Path]::GetFullPath($directory)
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Missing input directory: $root" }
    $files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
    if ($files.Count -eq 0) { throw "Empty input directory: $root" }
    foreach ($file in $files) {
        Add-Payload "$relative/$([IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/'))" $file.FullName
    }
}
$proxy = @(
    (Join-Path $projectRoot "artifacts/native-bootstrap/$Configuration/hostfxr.dll"),
    (Join-Path $projectRoot 'artifacts/native-bootstrap/hostfxr.dll')
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $proxy) { throw 'Build the native hostfxr proxy first.' }
Add-Payload 'hostfxr.dll' $proxy
Add-Payload 'VulkanStory/loader.ini' (Join-Path $projectRoot 'packaging/loader.ini')
Add-Payload 'VulkanStory/README.txt' (Join-Path $projectRoot 'packaging/README-client.txt')
Add-Payload 'VulkanStory/tools/deploy-runtime.ps1' (Join-Path $projectRoot 'scripts/deploy-runtime.ps1')
Add-Payload 'VulkanStory/tools/remove-runtime.ps1' (Join-Path $projectRoot 'scripts/remove-runtime.ps1')
foreach ($project in @('Bootstrap','Contracts','Game','Input','Platform.Sdl','Render.Vulkan')) {
    Add-Payload "VulkanStory/managed/VulkanStory.$project.dll" (Join-Path $projectRoot "src/VulkanStory.$project/bin/$Configuration/net10.0/VulkanStory.$project.dll")
    Add-Payload "VulkanStory/managed/VulkanStory.$project.xml" (Join-Path $projectRoot "src/VulkanStory.$project/bin/$Configuration/net10.0/VulkanStory.$project.xml")
}
# Explicit dependency inventory matches BootstrapDependencies; game/Harmony assemblies are excluded.
$backend = Join-Path $projectRoot "src/VulkanStory.Render.Vulkan/bin/$Configuration/net10.0"
foreach ($name in @('SDL3-CS','Silk.NET.Core','Silk.NET.Shaderc','Silk.NET.Vulkan',
    'Silk.NET.Vulkan.Extensions.EXT','Silk.NET.Vulkan.Extensions.KHR',
    'Microsoft.DotNet.PlatformAbstractions','Microsoft.Extensions.DependencyModel')) {
    Add-Payload "VulkanStory/managed/$name.dll" (Join-Path $backend "$name.dll")
}
Add-Payload 'VulkanStory/managed/profiles/vs-1.22.7-win-x64.json' (Join-Path $projectRoot 'profiles/vs-1.22.7-win-x64.json')
Add-Payload 'Mods/vulkanstory/VulkanStory.Mod.dll' (Join-Path $projectRoot "src/VulkanStory.Mod/bin/$Configuration/net10.0/VulkanStory.Mod.dll")
Add-Payload 'Mods/vulkanstory/VulkanStory.Mod.xml' (Join-Path $projectRoot "src/VulkanStory.Mod/bin/$Configuration/net10.0/VulkanStory.Mod.xml")
Add-Payload 'Mods/vulkanstory/modinfo.json' (Join-Path $projectRoot 'src/VulkanStory.Mod/modinfo.json')
# The ordinary server-only mod also loads in the integrated single-player server.
# Its shared Input DLL comes from the same build as the early payload, preserving
# assembly identity. Dedicated servers receive the separate companion archive.
foreach ($name in @('VulkanStory.Input.Companion','VulkanStory.Input')) {
    Add-Payload "Mods/vulkanstoryinput/$name.dll" (Join-Path $projectRoot "src/$name/bin/$Configuration/net10.0/$name.dll")
    Add-Payload "Mods/vulkanstoryinput/$name.xml" (Join-Path $projectRoot "src/$name/bin/$Configuration/net10.0/$name.xml")
    Add-Payload "optional-server/vulkanstoryinput/$name.dll" (Join-Path $projectRoot "src/$name/bin/$Configuration/net10.0/$name.dll")
    Add-Payload "optional-server/vulkanstoryinput/$name.xml" (Join-Path $projectRoot "src/$name/bin/$Configuration/net10.0/$name.xml")
}
Add-Payload 'Mods/vulkanstoryinput/modinfo.json' (Join-Path $projectRoot 'src/VulkanStory.Input.Companion/modinfo.json')
Add-Payload 'optional-server/vulkanstoryinput/modinfo.json' (Join-Path $projectRoot 'src/VulkanStory.Input.Companion/modinfo.json')
Add-Payload 'VulkanStory/assets/gamecontrollerdb.txt' (Join-Path $projectRoot 'src/VulkanStory.Platform.Sdl/assets/gamecontrollerdb.txt')
Add-Payload 'VulkanStory/licenses/SDL_GameControllerDB-LICENSE.txt' (Join-Path $projectRoot 'src/VulkanStory.Platform.Sdl/assets/SDL_GameControllerDB-LICENSE.txt')
Add-Tree 'VulkanStory/licenses/PromptFont' (Join-Path $projectRoot 'packaging/notices/promptfont')
Add-Tree 'VulkanStory/licenses/source-provenance' (Join-Path $projectRoot 'porting/provenance')
Add-Tree 'optional-server/vulkanstoryinput/licenses/source-provenance' (Join-Path $projectRoot 'porting/provenance')
Add-Tree 'Mods/vulkanstoryinput/licenses/source-provenance' (Join-Path $projectRoot 'porting/provenance')
Add-Tree 'VulkanStory/licenses/native' $NativeLicensesDirectory
Add-Tree 'VulkanStory/licenses/managed' $ManagedLicensesDirectory
Add-Payload 'VulkanStory/licenses/dependency-notice-sources.json' (Join-Path $projectRoot 'packaging/notices/sources.json')
$nativeInventory = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/native-win-x64.json') -Raw | ConvertFrom-Json
foreach ($component in $nativeInventory.components.PSObject.Properties) {
    foreach ($required in $component.Value) {
        Add-Payload "VulkanStory/native/win-x64/$required" (Join-Path $NativeDirectory $required)
    }
}
Add-Payload 'VulkanStory/native-inventory.json' (Join-Path $projectRoot 'packaging/native-win-x64.json')
$shaderRoot = [IO.Path]::GetFullPath($ShadersDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $shaderRoot 'shaders.manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Unsupported shader manifest schema.' }
$expected = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'shaders/native') -Filter '*.glsl' -File | Select-Object -ExpandProperty BaseName)
foreach ($name in $expected) {
    if (-not (@($manifest.programs.name) -ccontains $name)) { throw "Incomplete shader corpus: $name" }
}
Add-Payload 'VulkanStory/shaders-vk/shaders.manifest.json' (Join-Path $shaderRoot 'shaders.manifest.json')
foreach ($program in $manifest.programs) {
    if (@($program.variants).Count -eq 0) { throw "Shader has no variants: $($program.name)" }
    foreach ($variant in $program.variants) {
        if (@($variant.stages).Count -eq 0) { throw "Shader has no stages: $($program.name)" }
        foreach ($entry in $variant.stages) {
            if ([string]::IsNullOrWhiteSpace($entry.spirv) -or [IO.Path]::IsPathRooted($entry.spirv)) { throw 'Invalid shader blob path.' }
            $blob = [IO.Path]::GetFullPath((Join-Path $shaderRoot $entry.spirv))
            if (-not $blob.StartsWith($shaderRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Shader blob escapes its input directory.' }
            $hash = (Get-FileHash -LiteralPath $blob -Algorithm SHA256).Hash
            if ($hash -ine $entry.sha256) { throw "Shader blob hash mismatch: $blob" }
            $relative = "VulkanStory/shaders-vk/$($entry.spirv.Replace('\','/'))"
            if (-not $sources.Contains($relative)) { Add-Payload $relative $blob }
        }
    }
}
# All inputs are resolved before the fresh staging directory is created.
$inventory = [ordered]@{}
foreach ($relative in $sources.Keys) {
    $destination = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sources[$relative] -Destination $destination
    $inventory[$relative] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
[ordered]@{ schema=1; product='VulkanStory'; profile='vs-1.22.7-win-x64'; acceptance='unverified'; files=$inventory } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'VulkanStory/package.json') -Encoding utf8
Write-Host "Staged runtime at $stage. No installation, game launch or release acceptance was performed."
