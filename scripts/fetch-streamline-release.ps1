<#
.SYNOPSIS
Downloads and extracts the pinned Streamline 2.14.1 release SDK.
.DESCRIPTION
The sdk/streamline submodule pins the Streamline 2.14.1 source and headers, but the
production runtime binaries (bin/x64) and their notices only ship in the release ZIP.
This script downloads that ZIP from the GitHub release, verifies its pinned SHA256,
and extracts it into a fresh, gitignored directory used as the release SDK root by
prepare-native-bundle.ps1. It does not build, stage, install or launch anything.
.PARAMETER OutputDirectory
Fresh destination; defaults to sdk/streamline-release-2.14.1. An existing directory is rejected.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk/streamline-release-2.14.1')
)
$ErrorActionPreference = 'Stop'
$url = 'https://github.com/NVIDIA-RTX/Streamline/releases/download/v2.14.1/streamline-sdk-v2.14.1.zip'
$sha256 = '92C4D954631A1710DA86CA3FA8D5034F2B9503838C95FC4AE977AE149319781B'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a fresh output directory; $output already exists." }
$archive = Join-Path ([IO.Path]::GetTempPath()) ("streamline-sdk-v2.14.1-" + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri $url -OutFile $archive
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($actual -ne $sha256) { throw "Streamline release archive hash mismatch: $actual" }
    Expand-Archive -LiteralPath $archive -DestinationPath $output
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}
# The archive may wrap the SDK in a single top-level folder; flatten it so include/ and bin/ sit at the root.
$children = @(Get-ChildItem -LiteralPath $output)
if ($children.Count -eq 1 -and $children[0].PSIsContainer -and -not (Test-Path -LiteralPath (Join-Path $output 'include'))) {
    $inner = $children[0].FullName
    Get-ChildItem -LiteralPath $inner -Force | Move-Item -Destination $output
    Remove-Item -LiteralPath $inner
}
foreach ($required in 'include/sl_version.h', 'bin/x64/sl.interposer.dll', 'license.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $output $required) -PathType Leaf)) { throw "Release SDK is missing $required." }
}
Write-Host "Extracted the Streamline 2.14.1 release SDK to $output."
