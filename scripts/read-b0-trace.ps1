[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateSet('Active','Bypassed')][string]$Expected = 'Active',
    [switch]$RequireExit
)
$ErrorActionPreference = 'Stop'
$events = @(Get-Content -LiteralPath $Path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
if (-not $events.Count) { throw 'The trace is empty.' }
$processIds = @($events | Select-Object -ExpandProperty pid -Unique)
if ($processIds.Count -ne 1) { throw 'The trace mixes process identities.' }
if (@($events | Select-Object -ExpandProperty frequency -Unique).Count -ne 1) { throw 'The trace mixes clock frequencies.' }
if ($events | Where-Object { $_.event -eq 'native.forwarding.failed' }) { throw 'Native host forwarding failed.' }
if ($Expected -eq 'Active') {
    $required = @('native.proxy.attached','native.hostfxr.entry','native.startup_hook.armed','native.hostfxr.forward',
        'managed.bootstrap.enter','managed.profile.accepted','managed.patches.ready','managed.bootstrap.observing',
        'game.client.main.enter','game.client.start.enter','game.platform.construct.enter',
        'game.window.request.enter','game.window.construct.enter','game.window.construct.return',
        'game.screens.start.enter','game.platform.start.enter')
    $previous = [long]0
    foreach ($name in $required) {
        $matching = @($events | Where-Object { $_.event -eq $name })
        if (-not $matching.Count) { throw "Missing startup evidence: $name" }
        $current = [long]$matching[0].qpc
        if ($current -lt $previous) { throw "Startup marker is out of order: $name" }
        $previous = $current
    }
    foreach ($name in @('native.hostfxr.forward','managed.bootstrap.enter','managed.patches.ready','game.client.main.enter','game.client.start.enter')) {
        if (@($events | Where-Object { $_.event -eq $name }).Count -ne 1) { throw "Duplicate entry evidence: $name" }
    }
    $entryThread = ($events | Where-Object { $_.event -eq 'native.hostfxr.forward' } | Select-Object -First 1).nativeThread
    foreach ($name in @('managed.bootstrap.enter','managed.patches.ready','game.client.main.enter','game.client.start.enter')) {
        if (($events | Where-Object { $_.event -eq $name } | Select-Object -First 1).nativeThread -ne $entryThread) { throw "Startup moved to another OS thread: $name" }
    }
    if ($events | Where-Object { $_.event -eq 'managed.bootstrap.bypassed' -or $_.event -eq 'native.forwarding.failed' }) { throw 'Active startup contains a bootstrap failure.' }
} else {
    if (-not ($events | Where-Object { $_.event -eq 'native.startup_hook.bypassed' -or $_.event -eq 'managed.bootstrap.bypassed' })) { throw 'No bypass evidence.' }
    if ($events | Where-Object { $_.event -eq 'managed.patches.ready' }) { throw 'Observation patches were installed during expected bypass.' }
}
if ($RequireExit -and -not ($events | Where-Object { $_.event -eq 'native.hostfxr.return' -or $_.event -eq 'managed.process.exit' })) { throw 'No shutdown evidence. Confirm the original process exit separately.' }
if ($events | Where-Object { $_.event -eq 'native.hostfxr.return' -and $_.detail -ne '0' }) { throw 'The native host returned a failure exit code.' }
$events | Select-Object event,pid,qpc,detail
Write-Host "B0 trace satisfies $Expected ordering. This verifies startup markers, not renderer or MFG functionality."
