[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
if (Test-Path -LiteralPath $backupRoot) { throw 'Choose a fresh backup directory.' }
foreach ($root in @($gameRoot,$packageRoot)) {
    if ($backupRoot.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
        $backupRoot.StartsWith($root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backups must be outside the game and package directories.'
    }
}
function Resolve-Child([string]$root, [string]$relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Replace('\','/').Split('/') -contains '..') { throw "Invalid payload path: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Payload path escapes directory: $relative"
    }
    return $path
}
function Is-OwnedPath([string]$relative) {
    return $relative -eq 'hostfxr.dll' -or $relative.StartsWith('VulkanStory/', [StringComparison]::Ordinal) -or
        $relative.StartsWith('Mods/vulkanstory/', [StringComparison]::Ordinal) -or
        $relative.StartsWith('Mods/vulkanstoryinput/', [StringComparison]::Ordinal)
}
$package = Get-Content -LiteralPath (Join-Path $packageRoot 'VulkanStory/package.json') -Raw | ConvertFrom-Json
if ($package.schema -ne 1 -or $package.product -ne 'VulkanStory' -or $package.profile -ne 'vs-1.22.7-win-x64') { throw 'Unsupported runtime package.' }
$sources = [ordered]@{}
foreach ($entry in $package.files.PSObject.Properties) {
    if ($entry.Name.StartsWith('optional-server/', [StringComparison]::Ordinal)) { continue }
    if (-not (Is-OwnedPath $entry.Name)) { throw "Unexpected client payload path: $($entry.Name)" }
    $source = Resolve-Child $packageRoot $entry.Name
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.Value) { throw "Package hash mismatch: $($entry.Name)" }
    $sources[$entry.Name] = $source
}
foreach ($required in @('hostfxr.dll','VulkanStory/loader.ini','VulkanStory/managed/VulkanStory.Bootstrap.dll',
    'VulkanStory/managed/VulkanStory.Game.dll','VulkanStory/managed/profiles/vs-1.22.7-win-x64.json','Mods/vulkanstory/modinfo.json',
    'Mods/vulkanstoryinput/modinfo.json','Mods/vulkanstoryinput/VulkanStory.Input.Companion.dll','Mods/vulkanstoryinput/VulkanStory.Input.dll')) {
    if (-not $sources.Contains($required)) { throw "Incomplete client package: $required" }
}
$sources['VulkanStory/package.json'] = Join-Path $packageRoot 'VulkanStory/package.json'
$profile = Get-Content -LiteralPath $sources['VulkanStory/managed/profiles/vs-1.22.7-win-x64.json'] -Raw | ConvertFrom-Json
foreach ($entry in $profile.files.PSObject.Properties) {
    if ((Get-FileHash -LiteralPath (Resolve-Child $gameRoot $entry.Name) -Algorithm SHA256).Hash -ine $entry.Value) {
        throw "Official game reference mismatch: $($entry.Name)"
    }
}
if (Get-Process -Name 'Vintagestory','VintagestoryServer','VSCrashReporter' -ErrorAction SilentlyContinue) { throw 'Close game, server and crash reporter before deployment.' }
$receiptRelative = 'VulkanStory/install.json'
$receiptPath = Join-Path $gameRoot $receiptRelative
$previousReceipt = if (Test-Path -LiteralPath $receiptPath) { $receiptPath }
    elseif (Test-Path -LiteralPath (Join-Path $gameRoot 'VulkanStory/b0-install.json')) { Join-Path $gameRoot 'VulkanStory/b0-install.json' }
    else { $null }
$owned = @{}
if ($previousReceipt) {
    $previous = Get-Content -LiteralPath $previousReceipt -Raw | ConvertFrom-Json
    if ($previous.schema -ne 1 -or $previous.product -notin @('VulkanStory','VulkanStory-B0')) { throw 'Unknown existing installation receipt.' }
    foreach ($entry in $previous.files.PSObject.Properties) {
        if (-not (Is-OwnedPath $entry.Name)) { throw "Invalid installation ownership: $($entry.Name)" }
        $owned[$entry.Name] = $entry.Value
    }
} elseif (Test-Path -LiteralPath (Join-Path $gameRoot 'VulkanStory/package.json') -PathType Leaf) {
    # A first install by ZIP extraction has a package inventory but no updater
    # receipt. Adopt it only after checking its complete client payload.
    $installedManifestPath = Join-Path $gameRoot 'VulkanStory/package.json'
    $installedPackage = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
    if ($installedPackage.schema -ne 1 -or $installedPackage.product -ne 'VulkanStory' -or
        $installedPackage.profile -ne $package.profile) { throw 'Unknown extracted installation inventory.' }
    foreach ($required in @('hostfxr.dll','VulkanStory/managed/VulkanStory.Bootstrap.dll','VulkanStory/managed/VulkanStory.Game.dll','Mods/vulkanstory/modinfo.json')) {
        if ($null -eq $installedPackage.files.PSObject.Properties[$required]) { throw "Extracted inventory is incomplete: $required" }
    }
    foreach ($entry in $installedPackage.files.PSObject.Properties) {
        if ($entry.Name.StartsWith('optional-server/', [StringComparison]::Ordinal)) { continue }
        if (-not (Is-OwnedPath $entry.Name)) { throw "Invalid extracted installation path: $($entry.Name)" }
        $installedFile = Resolve-Child $gameRoot $entry.Name
        if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf)) { throw "Extracted installation file is missing: $($entry.Name)" }
        $actual = (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash
        # User edits to the documented loader switch survive an update.
        if ($entry.Name -ne 'VulkanStory/loader.ini' -and $actual -ine $entry.Value) {
            throw "Extracted installation file changed: $($entry.Name)"
        }
        $owned[$entry.Name] = $actual
    }
    $owned['VulkanStory/package.json'] = (Get-FileHash -LiteralPath $installedManifestPath -Algorithm SHA256).Hash
}
$writes = [ordered]@{}
foreach ($relative in $sources.Keys) {
    $destination = Resolve-Child $gameRoot $relative
    if (Test-Path -LiteralPath $destination) {
        # Preserve the existing loader configuration, including development roots/bypass.
        if ($relative -eq 'VulkanStory/loader.ini' -and $owned.ContainsKey($relative)) { continue }
        $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if (-not $owned.ContainsKey($relative) -or $actual -ine $owned[$relative]) { throw "Existing file is not an unchanged owned payload: $relative" }
        if ($actual -eq (Get-FileHash -LiteralPath $sources[$relative] -Algorithm SHA256).Hash) { continue }
    }
    $writes[$relative] = $sources[$relative]
}
$versionPath = Join-Path $gameRoot 'version.dll'
$versionHash = if (Test-Path -LiteralPath $versionPath) { (Get-FileHash -LiteralPath $versionPath -Algorithm SHA256).Hash } else { $null }
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$backedUp = [ordered]@{}
foreach ($relative in @($writes.Keys) + @($receiptRelative)) {
    $existing = Resolve-Child $gameRoot $relative
    if (-not (Test-Path -LiteralPath $existing -PathType Leaf)) { continue }
    $backup = Resolve-Child $backupRoot $relative
    New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $existing -Destination $backup
    $backedUp[$relative] = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
}
[ordered]@{ schema=1; gameDirectory=$gameRoot; previousReceipt=$previousReceipt; backups=$backedUp; plannedWrites=@($writes.Keys) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backupRoot 'deployment.json') -Encoding utf8
# Dependencies and shaders precede Game, Bootstrap and finally the activation proxy.
$last = @('VulkanStory/managed/VulkanStory.Game.dll','VulkanStory/managed/VulkanStory.Bootstrap.dll','hostfxr.dll')
$order = @($writes.Keys | Where-Object { $_ -notin $last }) + @($last | Where-Object { $writes.Contains($_) })
try {
    foreach ($relative in $order) {
        $destination = Resolve-Child $gameRoot $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        [IO.File]::Copy($writes[$relative], $destination, $true)
    }
    $installed = [ordered]@{}
    foreach ($relative in $sources.Keys) { $installed[$relative] = (Get-FileHash -LiteralPath (Resolve-Child $gameRoot $relative) -Algorithm SHA256).Hash }
    if (Test-Path -LiteralPath $versionPath) {
        if ((Get-FileHash -LiteralPath $versionPath -Algorithm SHA256).Hash -ne $versionHash) { throw 'Existing version proxy changed during deployment.' }
    } elseif ($versionHash) { throw 'Existing version proxy disappeared during deployment.' }
    [ordered]@{ schema=1; product='VulkanStory'; profile=$package.profile; acceptance=$package.acceptance; files=$installed;
        backupDirectory=$backupRoot; existingVersionProxySha256=$versionHash } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $receiptPath -Encoding utf8
} catch {
    # Restore overwritten files; keep new files for diagnosis. No deletion is performed.
    foreach ($relative in $backedUp.Keys) { Copy-Item -LiteralPath (Resolve-Child $backupRoot $relative) -Destination (Resolve-Child $gameRoot $relative) -Force }
    throw
}
Write-Host "Installed candidate in $gameRoot. Backups: $backupRoot. No game launch or runtime acceptance occurred."
