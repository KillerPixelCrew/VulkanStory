<#
.SYNOPSIS
Moves unchanged inventoried VulkanStory files into a fresh external recovery backup.
.DESCRIPTION
Reads the install/package inventory, rejects invalid paths/reparse traversal/running client processes, and preserves modified nonactivation files. ShouldProcess supports WhatIf/Confirm. Activation moves first; failure restores dependencies before activation without overwriting recovery conflicts. Saves/settings, version.dll, and empty directories remain.
.PARAMETER GameDirectory
Installation containing a recognized VulkanStory/install.json or package.json ownership inventory.
.PARAMETER BackupDirectory
Fresh directory outside the game root receiving unchanged owned files and removal.json.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [Parameter(Mandatory)][string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$backupRoot = [IO.Path]::GetFullPath($BackupDirectory)
if (Test-Path -LiteralPath $backupRoot) { throw 'Choose a fresh backup directory.' }
if ($backupRoot.Equals($gameRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $backupRoot.StartsWith($gameRoot.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The removal backup must be outside the game directory.'
}
<#
.SYNOPSIS
Resolves an ownership path without traversing a junction.
.DESCRIPTION
Rejects rooted/drive-qualified/parent paths, verifies lexical containment, and checks existing ancestry through the selected root. Returns an absolute path without moving files.
.PARAMETER root
Absolute installation or backup root.
.PARAMETER relative
Relative ownership path beneath the root.
#>
function Resolve-Child([string]$root, [string]$relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Replace('\','/').Split('/') -contains '..') { throw "Invalid ownership path: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $path.StartsWith($root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Ownership path escapes directory: $relative"
    }
    # Lexical containment alone cannot authorize a move through a junction.
    $node = $path
    while ($node -and $node.Length -ge $root.Length) {
        if (Test-Path -LiteralPath $node) {
            if (((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Ownership path traverses a reparse point: $node"
            }
        }
        if ($node.Equals($root, [StringComparison]::OrdinalIgnoreCase)) { break }
        $node = Split-Path $node -Parent
    }
    return $path
}
function Is-OwnedPath([string]$relative) {
    return $relative -eq 'hostfxr.dll' -or $relative.StartsWith('VulkanStory/', [StringComparison]::Ordinal) -or
        $relative.StartsWith('Mods/vulkanstory/', [StringComparison]::Ordinal) -or
        $relative.StartsWith('Mods/vulkanstoryinput/', [StringComparison]::Ordinal)
}
$inventoryRelative = if (Test-Path -LiteralPath (Join-Path $gameRoot 'VulkanStory/install.json') -PathType Leaf) {
    'VulkanStory/install.json'
} else { 'VulkanStory/package.json' }
$inventoryPath = Resolve-Child $gameRoot $inventoryRelative
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if ($inventory.schema -ne 1 -or $inventory.product -ne 'VulkanStory' -or $inventory.profile -ne 'vs-1.22.7-win-x64') {
    throw 'Unknown VulkanStory installation inventory.'
}
$owned = [ordered]@{}
foreach ($entry in $inventory.files.PSObject.Properties) {
    if ($entry.Name.StartsWith('optional-server/', [StringComparison]::Ordinal)) { continue }
    if (-not (Is-OwnedPath $entry.Name) -or $entry.Value -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid ownership entry: $($entry.Name)" }
    $owned[$entry.Name] = $entry.Value
}
foreach ($required in @('hostfxr.dll','VulkanStory/managed/VulkanStory.Bootstrap.dll','VulkanStory/managed/VulkanStory.Game.dll','Mods/vulkanstory/modinfo.json')) {
    if (-not $owned.Contains($required)) { throw "Incomplete installation inventory: $required" }
}
# The metadata itself is retained in the removal backup for recovery.
$owned[$inventoryRelative] = (Get-FileHash -LiteralPath $inventoryPath -Algorithm SHA256).Hash
$planned = [ordered]@{}
$preserved = [ordered]@{}
foreach ($relative in $owned.Keys) {
    $source = Resolve-Child $gameRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ($actual -ine $owned[$relative]) {
        if ($relative -eq 'hostfxr.dll') { throw 'The activation proxy changed; refusing to detach its payload.' }
        $preserved[$relative] = $actual
    } else { $planned[$relative] = $actual }
}
if (Get-Process -Name 'Vintagestory','VintagestoryServer','VSCrashReporter' -ErrorAction SilentlyContinue) {
    throw 'Close game, server and crash reporter before removal.'
}
if (-not $PSCmdlet.ShouldProcess($gameRoot, "Move $($planned.Count) unchanged VulkanStory files to $backupRoot")) { return }
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$moved = [ordered]@{}
$recordPath = Join-Path $backupRoot 'removal.json'
<#
.SYNOPSIS
Persists removal/recovery progress in the backup.
.DESCRIPTION
Writes the enclosing game root, moved files, and preserved modified files to recordPath; I/O failures propagate.
.PARAMETER status
Recorded phase token: prepared, moving, complete, or rolled-back.
#>
function Write-RemovalRecord([string]$status) {
    [ordered]@{schema=1;product='VulkanStory';gameDirectory=$gameRoot;status=$status;files=$moved;preserved=$preserved} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recordPath -Encoding utf8
}
Write-RemovalRecord 'prepared'
# Detach activation first; retain its dependencies until the proxy has moved.
$order = @('hostfxr.dll' | Where-Object { $planned.Contains($_) }) + @($planned.Keys | Where-Object { $_ -ne 'hostfxr.dll' })
try {
    foreach ($relative in $order) {
        $source = Resolve-Child $gameRoot $relative
        $destination = Resolve-Child $backupRoot $relative
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $planned[$relative]) { throw "File changed during removal: $relative" }
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Move-Item -LiteralPath $source -Destination $destination
        $moved[$relative] = $planned[$relative]
        Write-RemovalRecord 'moving'
    }
    Write-RemovalRecord 'complete'
} catch {
    # Restore dependencies before activation; never overwrite a concurrently created file.
    $failure = $_
    foreach ($relative in @($moved.Keys | Where-Object { $_ -ne 'hostfxr.dll' }) + @('hostfxr.dll' | Where-Object { $moved.Contains($_) })) {
        $destination = Resolve-Child $gameRoot $relative
        if (Test-Path -LiteralPath $destination) { throw "Removal failed; recovery conflict at $destination. Backup retained at $backupRoot. Original failure: $failure" }
        Move-Item -LiteralPath (Resolve-Child $backupRoot $relative) -Destination $destination
    }
    Write-RemovalRecord 'rolled-back'
    throw $failure
}
Write-Host "Detached VulkanStory. Backup: $backupRoot. Preserved $($preserved.Count) modified files. Empty directories remain; saves/settings and version.dll were not moved."
