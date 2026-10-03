[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'Vintagestory.exe'))) { throw 'Target is not a game installation.' }
foreach ($reserved in @('hostfxr.dll','VulkanStory')) {
    if (Test-Path -LiteralPath (Join-Path $gameRoot $reserved)) { throw "Existing $reserved will not be replaced. B0 supports fresh deployment only." }
}
$package = Get-Content -LiteralPath (Join-Path $packageRoot 'b0-package.json') -Raw | ConvertFrom-Json
if ($package.schema -ne 1 -or $package.product -ne 'VulkanStory-B0') { throw 'Unsupported B0 package manifest.' }
$required = @('hostfxr.dll','VulkanStory/loader.ini','VulkanStory/managed/VulkanStory.Bootstrap.dll',
    'VulkanStory/managed/VulkanStory.Game.dll','VulkanStory/managed/profiles/vs-1.22.7-win-x64.json')
if (@($package.files.PSObject.Properties).Count -ne $required.Count) { throw 'Unexpected B0 package file inventory.' }
foreach ($relative in $required) {
    $source = Join-Path $packageRoot $relative
    $expected = $package.files.PSObject.Properties[$relative].Value
    if (-not $expected -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected) { throw "Package mismatch: $relative" }
}
$profile = Get-Content -LiteralPath (Join-Path $packageRoot $required[-1]) -Raw | ConvertFrom-Json
foreach ($file in $profile.files.PSObject.Properties) {
    $resolved = [IO.Path]::GetFullPath((Join-Path $gameRoot $file.Name))
    if (-not $resolved.StartsWith($gameRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Profile path outside game directory.' }
    if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $file.Value) { throw "Official reference mismatch: $($file.Name)" }
}
$running = Get-Process -Name 'Vintagestory','VintagestoryServer','VSCrashReporter' -ErrorAction SilentlyContinue
if ($running) { throw 'Close game, server and crash reporter processes before B0 deployment.' }
$existingVersion = Join-Path $gameRoot 'version.dll'
$versionHash = if (Test-Path -LiteralPath $existingVersion) { (Get-FileHash -LiteralPath $existingVersion -Algorithm SHA256).Hash } else { $null }
# All collision/hash checks precede writes. The activation proxy is installed last.
# If copying fails, no game files are replaced; the diagnostic payload may remain for inspection.
foreach ($relative in $required | Where-Object { $_ -ne 'hostfxr.dll' }) {
    $destination = Join-Path $gameRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    [IO.File]::Copy((Join-Path $packageRoot $relative), $destination, $false)
}
[ordered]@{ schema=1; product='VulkanStory-B0'; files=$package.files; existingVersionProxySha256=$versionHash } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $gameRoot 'VulkanStory/b0-install.json') -Encoding utf8
[IO.File]::Copy((Join-Path $packageRoot 'hostfxr.dll'), (Join-Path $gameRoot 'hostfxr.dll'), $false)
Write-Host "Installed B0 observer in $gameRoot. Launch the normal game shortcut when performing the planned validation batch."
