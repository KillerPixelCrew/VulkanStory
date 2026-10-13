param([Parameter(Mandatory)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$report = (Resolve-Path -LiteralPath $ReportDirectory).Path
$log = Join-Path $report 'collection.txt'

function Read-RecordedPath([string]$name) {
    $path = Join-Path $report $name
    if (Test-Path -LiteralPath $path) { (Get-Content -LiteralPath $path -Raw).Trim() }
}
function Copy-ReportFile([string]$source, [string]$destination) {
    if (-not $source -or -not (Test-Path -LiteralPath $source -PathType Leaf)) { return }
    try { Copy-Item -LiteralPath $source -Destination (Join-Path $report $destination) -Force }
    catch { $_.Exception.Message | Add-Content -LiteralPath $log }
}
function Remove-AccountFields($value) {
    if ($null -eq $value) { return }
    if ($value -is [System.Collections.IList]) {
        foreach ($item in $value) { Remove-AccountFields $item }
    } elseif ($value -is [pscustomobject]) {
        foreach ($property in @($value.PSObject.Properties)) {
            if ($property.Name -match '(?i)token|password|session|auth|email|login|username|server|favorite|connect') {
                $value.PSObject.Properties.Remove($property.Name)
            } else { Remove-AccountFields $property.Value }
        }
    }
}
try {
    $data = Read-RecordedPath 'game-data-path.txt'
    $game = Read-RecordedPath 'game-directory.txt'
    Copy-ReportFile (Read-RecordedPath 'bootstrap-path.txt') 'bootstrap.jsonl'
    if ($game) { Copy-ReportFile (Join-Path $game 'VulkanStory/package.json') 'package.json' }
    if ($data) {
        foreach ($settings in @('ModConfig/vulkanstory.json','clientsettings.json')) {
            $source = Join-Path $data $settings
            if (Test-Path -LiteralPath $source -PathType Leaf) {
                try {
                    $content = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
                    Remove-AccountFields $content
                    $content | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $report ([IO.Path]::GetFileName($settings))) -Encoding utf8
                } catch { $_.Exception.Message | Add-Content -LiteralPath $log }
            }
        }
    }
    foreach ($root in @($game,$data) | Where-Object { $_ }) {
        $mods = Join-Path $root 'Mods'
        if (Test-Path -LiteralPath $mods -PathType Container) {
            "Mods in $mods" | Add-Content -LiteralPath (Join-Path $report 'mods.txt')
            Get-ChildItem -LiteralPath $mods | Select-Object -ExpandProperty Name |
                Add-Content -LiteralPath (Join-Path $report 'mods.txt')
        }
    }
    'Crash report: native exception parameters, thread stacks, module/driver versions, managed exceptions, bootstrap/game logs, settings and mod filenames. Account fields are removed from settings. The minidump filters stack data and excludes the game heap. Review logs for private information before sharing.' |
        Set-Content -LiteralPath (Join-Path $report 'README.txt') -Encoding utf8
    # Zip in the parent directory: never include the archive in itself.
    $archive = $report + '.zip'
    Compress-Archive -LiteralPath $report -DestinationPath $archive -CompressionLevel Optimal
    "Report written: $archive" | Add-Content -LiteralPath $log
} catch { $_.Exception.ToString() | Add-Content -LiteralPath $log; exit 1 }
