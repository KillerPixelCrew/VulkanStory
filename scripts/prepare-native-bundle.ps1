<#
.SYNOPSIS
Collects declared Windows native runtimes, provider bridges, and redistribution notices.
.DESCRIPTION
Maps packaging/native-win-x64.json to explicit bridge/core/vendor sources, checks the selected Streamline 2.14.1 version and runtime hashes, then copies native/ and licenses/ into a fresh tree with bundle.json hashes. Missing inputs or identity mismatches throw; no provider execution, builds, tests, installation, or game launch occur.
.PARAMETER BridgesDirectory
Directory containing the compiled VulkanStory provider bridge DLLs.
.PARAMETER CoreNativeDirectory
Directory supplying inventory-listed SDL/shader compiler core binaries.
.PARAMETER CoreNoticesDirectory
Nonempty recursive tree of SDL/shader compiler and MinHook (statically linked into the FSR 4 bridge) redistribution notices, normally packaging/notices/native.
.PARAMETER DlssSdkRoot
DLSS SDK root supplying lib/Windows_x86_64/rel runtimes and LICENSE.txt; defaults to the sdk/dlss submodule.
.PARAMETER Fsr3SdkRoot
FidelityFX SDK root supplying PrebuiltSignedDLL runtimes and license files; defaults to the sdk/fidelityfx-vk submodule.
.PARAMETER Fsr4SdkRoot
FidelityFX DX12 SDK root supplying Kits/FidelityFX/signedbin runtimes and notices; defaults to the sdk/fidelityfx submodule.
.PARAMETER XessSdkRoot
XeSS SDK root supplying bin runtimes and LICENSE.txt; defaults to the sdk/xess submodule.
.PARAMETER StreamlineSdkRoot
Matching Streamline 2.14.1 release SDK root supplying headers, reference runtime hashes, and notices; defaults to sdk/streamline-release-2.14.1 (populate with scripts/fetch-streamline-release.ps1).
.PARAMETER StreamlineRuntimeDirectory
Optional runtime source directory; defaults to StreamlineSdkRoot/bin/x64 and must match its release binary hashes.
.PARAMETER OutputDirectory
Fresh bundle destination, with native/ and licenses/ subtrees plus bundle.json.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BridgesDirectory,
    [Parameter(Mandatory)][string]$CoreNativeDirectory,
    [Parameter(Mandatory)][string]$CoreNoticesDirectory,
    [string]$DlssSdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/dlss'),
    [string]$Fsr3SdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/fidelityfx-vk'),
    [string]$Fsr4SdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/fidelityfx'),
    [string]$XessSdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/xess'),
    [string]$StreamlineSdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/streamline-release-2.14.1'),
    [string]$StreamlineRuntimeDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh bundle output directory.' }
$inventory = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/native-win-x64.json') -Raw | ConvertFrom-Json
if (-not $StreamlineRuntimeDirectory) { $StreamlineRuntimeDirectory = Join-Path $StreamlineSdkRoot 'bin/x64' }
$streamlineVersion = Get-Content -LiteralPath (Join-Path $StreamlineSdkRoot 'include/sl_version.h') -Raw
foreach ($part in @(@{ name='MAJOR'; value=2 }, @{ name='MINOR'; value=14 }, @{ name='PATCH'; value=1 })) {
    if ($streamlineVersion -notmatch "(?m)^\s*#define\s+SL_VERSION_$($part.name)\s+$($part.value)\s*$") {
        throw 'This delivery profile requires the matching Streamline 2.14.1 release SDK.'
    }
}
$files = [ordered]@{}
<#
.SYNOPSIS
Registers a verified native bundle input.
.DESCRIPTION
Stores an absolute source path without copying; absent inputs and duplicate bundle destinations throw.
.PARAMETER relative
Bundle-relative native/license destination key.
.PARAMETER source
Existing input file.
#>
function Add-Input([string]$relative, [string]$source) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing native bundle input: $source" }
    if ($files.Contains($relative)) { throw "Duplicate bundle destination: $relative" }
    $files[$relative] = [IO.Path]::GetFullPath($source)
}
foreach ($component in $inventory.components.PSObject.Properties) {
    foreach ($name in $component.Value) {
        $source = if ($name.StartsWith('VulkanStory')) { Join-Path $BridgesDirectory $name }
        elseif ($component.Name -eq 'core') { Join-Path $CoreNativeDirectory $name }
        elseif ($component.Name -eq 'dlss') { Join-Path $DlssSdkRoot "lib/Windows_x86_64/rel/$name" }
        elseif ($component.Name -eq 'fsr3') { Join-Path $Fsr3SdkRoot "PrebuiltSignedDLL/$name" }
        elseif ($component.Name -eq 'fsr4') { Join-Path $Fsr4SdkRoot "Kits/FidelityFX/signedbin/$name" }
        elseif ($component.Name -in @('xess','xess-fg')) { Join-Path $XessSdkRoot "bin/$name" }
        else { Join-Path $StreamlineRuntimeDirectory $name }
        if ($component.Name -eq 'streamline' -and -not $name.StartsWith('VulkanStory')) {
            $release = Join-Path $StreamlineSdkRoot "bin/x64/$name"
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $release -Algorithm SHA256).Hash) {
                throw "Streamline runtime differs from the selected SDK release: $name"
            }
        }
        Add-Input "native/$name" $source
    }
}
Add-Input 'licenses/dlss/LICENSE.txt' (Join-Path $DlssSdkRoot 'LICENSE.txt')
Add-Input 'licenses/fsr3/LICENSE.txt' (Join-Path $Fsr3SdkRoot 'LICENSE.txt')
Add-Input 'licenses/fsr3/sdk-LICENSE.txt' (Join-Path $Fsr3SdkRoot 'sdk/LICENSE.txt')
Add-Input 'licenses/fsr4/license.md' (Join-Path $Fsr4SdkRoot 'Kits/FidelityFX/docs/license.md')
Add-Input 'licenses/fsr4/3rdpartynotice.md' (Join-Path $Fsr4SdkRoot '3rdpartynotice.md')
Add-Input 'licenses/xess/LICENSE.txt' (Join-Path $XessSdkRoot 'LICENSE.txt')
Add-Input 'licenses/streamline/license.txt' (Join-Path $StreamlineSdkRoot 'license.txt')
Add-Input 'licenses/streamline/3rd-party-licenses.md' (Join-Path $StreamlineSdkRoot '3rd-party-licenses.md')
Add-Input 'licenses/streamline/ngx-license.txt' (Join-Path $StreamlineSdkRoot 'external/ngx-sdk/license.txt')
Add-Input 'licenses/streamline/reflex.license.txt' (Join-Path $StreamlineRuntimeDirectory 'reflex.license.txt')
Add-Input 'licenses/streamline/release-ngx-license.txt' (Join-Path $StreamlineRuntimeDirectory 'nvngx_dlss.license.txt')
$coreNotices = [IO.Path]::GetFullPath($CoreNoticesDirectory)
if (-not (Test-Path -LiteralPath $coreNotices -PathType Container)) { throw 'Core native notices directory is missing.' }
$noticeFiles = @(Get-ChildItem -LiteralPath $coreNotices -File -Recurse)
if ($noticeFiles.Count -eq 0) { throw 'Supply SDL and shaderc redistribution notices.' }
foreach ($notice in $noticeFiles) {
    Add-Input "licenses/core/$([IO.Path]::GetRelativePath($coreNotices, $notice.FullName))" $notice.FullName
}
$hashes = [ordered]@{}
foreach ($relative in $files.Keys) {
    $destination = Join-Path $output $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $files[$relative] -Destination $destination
    $hashes[$relative] = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
}
[ordered]@{ schema=1; rid='win-x64'; acceptance='unverified'; files=$hashes } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'bundle.json') -Encoding utf8
Write-Host "Prepared native inputs at $output. Supply native/ and licenses/ to stage-runtime.ps1. No SDK calls, tests, installation or game launch ran."
