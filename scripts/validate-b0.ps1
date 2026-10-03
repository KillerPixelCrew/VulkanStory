[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$RunId = ('b0-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')),
    [string]$VintageStoryPath,
    [string]$Generator = 'Ninja',
    [switch]$Deploy,
    [string]$GameDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runDirectory = Join-Path $projectRoot (Join-Path 'artifacts/validation' $RunId)
if (Test-Path -LiteralPath $runDirectory) { throw "Validation run already exists: $runDirectory" }
if ($Deploy -and -not $GameDirectory) { throw 'Specify -GameDirectory with -Deploy.' }

New-Item -ItemType Directory -Path $runDirectory -ErrorAction Stop | Out-Null
$summaryPath = Join-Path $runDirectory 'summary.json'
$summary = [ordered]@{
    schema = 1
    id = $RunId
    configuration = $Configuration
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    status = 'running'
    steps = @()
    managedTests = $null
    nativeTest = $null
    packageDirectory = $null
    deploymentDirectory = $null
    failure = $null
}

function Save-ValidationSummary {
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
}

function Invoke-ValidationStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Executable,
        [string[]]$Arguments = @(),
        [ValidateRange(1, 1800)][int]$TimeoutSeconds = 300
    )

    $stdout = Join-Path $runDirectory "$Name.stdout.txt"
    $stderr = Join-Path $runDirectory "$Name.stderr.txt"
    $started = (Get-Date).ToUniversalTime()
    $exitCode = $null
    $reason = $null
    $requestedExecutable = $Executable
    if ($Executable.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        # Invoke project scripts in their own PowerShell process. This gives us a
        # durable exit code even when the outer Codex command yields a session.
        $Executable = if ($PSVersionTable.PSEdition -eq 'Core') {
            Join-Path $PSHOME 'pwsh.exe'
        } else {
            Join-Path $PSHOME 'powershell.exe'
        }
        $Arguments = @('-NoProfile', '-NonInteractive', '-File', $requestedExecutable) + $Arguments
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $outTask = $null
    $errTask = $null
    try {
        if (-not $process.Start()) { throw 'The child process did not start.' }
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $reason = "Timed out after $TimeoutSeconds seconds."
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        if ($process.HasExited) { $exitCode = $process.ExitCode }
    } catch { $reason = $_.Exception.ToString() }
    finally {
        $outText = '[Output collection incomplete]'
        $errText = '[Error collection incomplete]'
        try { if ($outTask -and $outTask.Wait(5000)) { $outText = $outTask.GetAwaiter().GetResult() } }
        catch { $reason = "Output collection failed: $($_.Exception.Message)" }
        try { if ($errTask -and $errTask.Wait(5000)) { $errText = $errTask.GetAwaiter().GetResult() } }
        catch { $reason = "Error collection failed: $($_.Exception.Message)" }
        [IO.File]::WriteAllText($stdout, $outText)
        [IO.File]::WriteAllText($stderr, $errText)
        $process.Dispose()
    }

    $step = [ordered]@{
        name = $Name
        requestedExecutable = $requestedExecutable
        executable = $Executable
        arguments = $Arguments
        timeoutSeconds = $TimeoutSeconds
        startedUtc = $started.ToString('o')
        finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
        exitCode = $exitCode
        stdout = $stdout
        stderr = $stderr
        error = $reason
    }
    $summary.steps += $step
    Save-ValidationSummary
    if ($null -eq $exitCode -or $exitCode -ne 0 -or $reason) {
        throw "$Name failed (exit $exitCode). Inspect $stdout and $stderr. $reason"
    }
    return $step
}

Save-ValidationSummary
try {
    $buildArguments = @('-Configuration', $Configuration, '-Generator', $Generator)
    if ($VintageStoryPath) { $buildArguments += @('-VintageStoryPath', $VintageStoryPath) }
    Invoke-ValidationStep -Name 'build' -Executable (Join-Path $PSScriptRoot 'build-b0.ps1') -Arguments $buildArguments -TimeoutSeconds 600 | Out-Null

    $resultsDirectory = Join-Path $runDirectory 'managed-results'
    New-Item -ItemType Directory -Path $resultsDirectory -ErrorAction Stop | Out-Null
    $testProject = Join-Path $projectRoot 'tests/VulkanStory.Bootstrap.Tests/VulkanStory.Bootstrap.Tests.csproj'
    Invoke-ValidationStep -Name 'managed-tests' -Executable 'dotnet' -Arguments @(
        'test', $testProject, '-c', $Configuration, '--no-build',
        '--results-directory', $resultsDirectory, '--logger', 'trx;LogFileName=b0-managed.trx'
    ) -TimeoutSeconds 300 | Out-Null

    $trxPath = Join-Path $resultsDirectory 'b0-managed.trx'
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw 'Managed tests returned success without a TRX result.' }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if (-not $counters) { throw 'Managed TRX has no test counters.' }
    $total = [int]$counters.Attributes['total'].Value
    $passed = [int]$counters.Attributes['passed'].Value
    $failed = [int]$counters.Attributes['failed'].Value
    $notExecuted = [int]$counters.Attributes['notExecuted'].Value
    $summary.managedTests = [ordered]@{
        trx = $trxPath; total = $total; passed = $passed; failed = $failed; notExecuted = $notExecuted
    }
    Save-ValidationSummary
    if ($total -lt 9 -or $passed -ne $total -or $failed -ne 0 -or $notExecuted -ne 0) {
        throw "Managed TRX does not show the complete passing B0 suite: total=$total passed=$passed failed=$failed notExecuted=$notExecuted"
    }

    $nativeBuild = Join-Path $projectRoot 'artifacts/native-bootstrap'
    $nativeStep = Invoke-ValidationStep -Name 'native-tests' -Executable 'ctest' -Arguments @(
        '--test-dir', $nativeBuild, '-C', $Configuration, '--output-on-failure'
    ) -TimeoutSeconds 90
    $nativeLog = Join-Path $nativeBuild 'Testing/Temporary/LastTest.log'
    if (-not (Test-Path -LiteralPath $nativeLog -PathType Leaf)) { throw 'CTest returned success without a result log.' }
    $nativeStartedUtc = [DateTimeOffset]::Parse(
        $nativeStep.startedUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    if ((Get-Item -LiteralPath $nativeLog).LastWriteTimeUtc -lt $nativeStartedUtc.AddSeconds(-1)) {
        throw 'CTest result log predates this validation step.'
    }
    $nativeLogText = Get-Content -LiteralPath $nativeLog -Raw
    if (-not $nativeLogText.Contains('fxr_version_order') -or -not $nativeLogText.Contains('Test Passed.')) {
        throw 'CTest result log does not show the native version-order test passed.'
    }
    $nativeCopy = Join-Path $runDirectory 'native-tests.LastTest.log'
    Copy-Item -LiteralPath $nativeLog -Destination $nativeCopy
    $summary.nativeTest = [ordered]@{ name = 'fxr_version_order'; result = 'passed'; log = $nativeCopy }
    Save-ValidationSummary

    $packageDirectory = Join-Path $runDirectory 'package'
    Invoke-ValidationStep -Name 'stage' -Executable (Join-Path $PSScriptRoot 'stage-b0.ps1') -Arguments @(
        '-Configuration', $Configuration, '-OutputDirectory', $packageDirectory
    ) -TimeoutSeconds 60 | Out-Null
    $summary.packageDirectory = $packageDirectory
    Save-ValidationSummary

    if ($Deploy) {
        $resolvedGame = (Resolve-Path -LiteralPath $GameDirectory).Path
        Invoke-ValidationStep -Name 'deploy' -Executable (Join-Path $PSScriptRoot 'deploy-b0.ps1') -Arguments @(
            '-GameDirectory', $resolvedGame, '-PackageDirectory', $packageDirectory
        ) -TimeoutSeconds 120 | Out-Null
        $summary.deploymentDirectory = $resolvedGame
        $summary.status = 'deployed-awaiting-live-check'
    } else {
        $summary.status = 'staged-awaiting-live-check'
    }
    $summary.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Save-ValidationSummary
    Write-Host "B0 build, tests, and staging completed. Summary: $summaryPath"
    Write-Host "Managed tests: $passed/$total. Native test: passed. Package: $packageDirectory"
    if ($Deploy) { Write-Host "Deployed to $($summary.deploymentDirectory); live startup remains to be checked." }
} catch {
    $summary.status = 'failed'
    $summary.failure = $_.Exception.ToString()
    $summary.finishedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Save-ValidationSummary
    throw
}
