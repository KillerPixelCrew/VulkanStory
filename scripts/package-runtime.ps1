[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StagingDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$stage = [IO.Path]::GetFullPath($StagingDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh archive output directory.' }
$manifest = Get-Content -LiteralPath (Join-Path $stage 'VulkanStory/package.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.product -ne 'VulkanStory' -or $manifest.profile -ne 'vs-1.22.7-win-x64') {
    throw 'Unsupported runtime staging manifest.'
}
$client = [ordered]@{}
$server = [ordered]@{}
foreach ($entry in $manifest.files.PSObject.Properties) {
    $relative = $entry.Name.Replace('\','/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw "Invalid inventory path: $relative" }
    $source = [IO.Path]::GetFullPath((Join-Path $stage $relative))
    if (-not $source.StartsWith($stage.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Inventory path escapes staging: $relative"
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.Value) { throw "Staged file changed: $relative" }
    if ($relative.StartsWith('optional-server/', [StringComparison]::Ordinal)) {
        $server[$relative.Substring('optional-server/'.Length)] = @{ source=$source; hash=$entry.Value }
    } else {
        $client[$relative] = @{ source=$source; hash=$entry.Value }
    }
}
foreach ($required in @('hostfxr.dll','VulkanStory/managed/VulkanStory.Game.dll','Mods/vulkanstory/modinfo.json',
    'Mods/vulkanstoryinput/modinfo.json','Mods/vulkanstoryinput/VulkanStory.Input.Companion.dll','Mods/vulkanstoryinput/VulkanStory.Input.dll')) {
    if (-not $client.Contains($required)) { throw "Incomplete client inventory: $required" }
}
foreach ($required in @('vulkanstoryinput/modinfo.json','vulkanstoryinput/VulkanStory.Input.Companion.dll','vulkanstoryinput/VulkanStory.Input.dll')) {
    if (-not $server.Contains($required)) { throw "Incomplete server companion inventory: $required" }
}
function Write-Archive([string]$name, $files, [string]$manifestName, [string]$product) {
    $path = Join-Path $output $name
    $archive = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $hashes = [ordered]@{}
        foreach ($relative in $files.Keys) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $files[$relative].source,
                $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            $hashes[$relative] = $files[$relative].hash
        }
        $record = [ordered]@{ schema=1; product=$product; profile=$manifest.profile; acceptance=$manifest.acceptance; files=$hashes }
        $entry = $archive.CreateEntry($manifestName)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write(($record | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
    } finally { $archive.Dispose() }
    return [pscustomobject]@{ file=$name; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
# The client archive extracts directly into the existing game directory.
# The companion archive extracts directly into a server's Mods directory.
New-Item -ItemType Directory -Path $output | Out-Null
$archives = @(
    (Write-Archive 'VulkanStory-win-x64.zip' $client 'VulkanStory/package.json' 'VulkanStory'),
    (Write-Archive 'VulkanStory-Input-Companion.zip' $server 'vulkanstoryinput/package.json' 'VulkanStory-Input-Companion')
)
$archives | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'archives.json') -Encoding utf8
Write-Host "Created client and companion archives at $output. Acceptance status is preserved; no installation or game launch ran."
