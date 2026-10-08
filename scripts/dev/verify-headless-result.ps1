[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][int]$ExpectedFrameCount,
    [Parameter(Mandatory)][int]$ProcessExitCode,
    [Parameter(Mandatory)][bool]$TimedOut,
    [switch]$RequireScenario,
    [switch]$Visible,
    [switch]$MainMenuOptions,
    [ValidateSet('open','save','cancel')][string]$MainOptionsAction = 'open',
    [string]$ExpectedGameAssembly
)
$ErrorActionPreference = 'Stop'
$failures = [Collections.Generic.List[string]]::new()
if ($TimedOut) { $failures.Add('TimedOut') }
if ($ProcessExitCode -ne 0) { $failures.Add('ProcessExitCode') }
if ($ExpectedFrameCount -lt 0) { $failures.Add('LegacyFrameCount') }
$clientCrash = Join-Path $RunDirectory 'data/Logs/client-crash.log'
# The launcher creates a fresh isolated data directory and does not copy logs.
# A handled game crash can exit 0 after writing an earlier successful capture.
if ((Test-Path -LiteralPath $clientCrash -PathType Leaf) -and
    (Get-Item -LiteralPath $clientCrash).Length -gt 0) { $failures.Add('ClientCrash') }

function Assert-UniqueResultProperties($Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate JSON property.' }
            Assert-UniqueResultProperties $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-UniqueResultProperties $item }
    }
}
function Get-ResultObject($Element) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Expected JSON object.' }
    $properties = [Collections.Generic.Dictionary[string,System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) { $properties.Add($property.Name, $property.Value.Clone()) }
    return ,$properties
}
function Read-ResultObject([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Missing result file.' }
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 32
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($Path), $options)
    try {
        Assert-UniqueResultProperties $document.RootElement
        return ,(Get-ResultObject $document.RootElement)
    }
    finally { $document.Dispose() }
}
function Require-ResultFields($Properties, [string[]]$Names) {
    foreach ($name in $Names) {
        if (-not $Properties.ContainsKey($name)) { throw "Missing result field: $name" }
    }
}
function Get-ResultBoolean($Element) {
    if ($Element.ValueKind -notin @([System.Text.Json.JsonValueKind]::True, [System.Text.Json.JsonValueKind]::False)) {
        throw 'Expected JSON Boolean.'
    }
    return $Element.GetBoolean()
}
function Get-ResultInteger($Element) {
    [uint64]$value = 0
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or -not $Element.TryGetUInt64([ref]$value)) {
        throw 'Expected nonnegative integral JSON number in UInt64 range.'
    }
    return $value
}
function Get-ResultString($Element, [bool]$Nonempty = $true) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Expected JSON string.' }
    $value = $Element.GetString()
    if ($Nonempty -and [string]::IsNullOrEmpty($value)) { throw 'Expected nonempty JSON string.' }
    return $value
}
function Assert-ResultHash($Element) {
    $value = Get-ResultString $Element
    if (-not [regex]::IsMatch($value, '\A[0-9A-Fa-f]{64}\z')) { throw 'Expected SHA256 hex string.' }
}
function Assert-NullableResultString($Element) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { [void](Get-ResultString $Element $false) }
}
function Assert-NullableResultInteger($Element) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { [void](Get-ResultInteger $Element) }
}
function Resolve-ResultPath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Replace('\','/').Split('/') -contains '..') {
        throw 'Invalid result path.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $path.StartsWith($Root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Result path escapes its run directory.'
    }
    $node = $path
    while ($node -and $node.Length -ge $Root.Length) {
        if ((Test-Path -LiteralPath $node) -and
            ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Result path traverses a reparse point.'
        }
        if ($node.Equals($Root, [StringComparison]::OrdinalIgnoreCase)) { break }
        $node = Split-Path $node -Parent
    }
    return $path
}

$runRoot = [IO.Path]::GetFullPath($RunDirectory)
$frameRoot = Join-Path $runRoot 'frames'
$multiplierCheck = $env:VULKANSTORY_OPTIONS_MULTIPLIER_CHECK -eq '1'
if ($multiplierCheck) {
    try {
        $multiplier = Read-ResultObject (Join-Path $frameRoot 'options-multiplier.json')
        Require-ResultFields $multiplier @('success','context','before','sliderValue','requested','persisted','applied','returned')
        $expectedContext = if ($MainMenuOptions) { 'main' } else { 'world' }
        $valid = (Get-ResultBoolean $multiplier['success']) -and (Get-ResultString $multiplier['context']) -ceq $expectedContext
        foreach ($field in @('sliderValue','requested','persisted','applied')) {
            $valid = $valid -and (Get-ResultInteger $multiplier[$field]) -eq 6
        }
        $before = Get-ResultInteger $multiplier['before']
        if (-not $valid -or $before -lt 2 -or $before -gt 6 -or
            -not (Get-ResultString $multiplier['returned']).StartsWith('gamesettings-graphics',[StringComparison]::Ordinal)) {
            $failures.Add('OptionsMultiplier')
        }
    } catch { $failures.Add('MalformedOrMissingOptionsMultiplier') }
}
if ($MainMenuOptions) {
    try {
        $menu = Read-ResultObject (Join-Path $frameRoot 'main-options-result.json')
        Require-ResultFields $menu @('success','context','hidden','focused','hasWorld','host','displayed',
            'gameAssembly','pid','menuFrame','requested','written')
        if (-not (Get-ResultBoolean $menu['success'])) { $failures.Add('MainOptionsFailure') }
        $hidden = Get-ResultBoolean $menu['hidden']
        $focused = Get-ResultBoolean $menu['focused']
        $expectedPrefix = if ($MainOptionsAction -eq 'open' -and -not $multiplierCheck) { 'gamesettings-vulkanstory-' } else { 'gamesettings-graphics' }
        if ((Get-ResultString $menu['context']) -cne 'main' -or (Get-ResultBoolean $menu['hasWorld']) -or
            $hidden -eq [bool]$Visible -or (-not $Visible -and $focused) -or
            (Get-ResultString $menu['host']) -cne 'Vintagestory.Client.NoObf.GuiScreenSettings' -or
            -not (Get-ResultString $menu['displayed']).StartsWith($expectedPrefix, [StringComparison]::Ordinal)) {
            $failures.Add('MainOptionsInvariant')
        }
        if ($MainOptionsAction -ne 'open') {
            Require-ResultFields $menu @('action')
            $actionResult = Read-ResultObject (Join-Path $frameRoot 'main-options-action.json')
            Require-ResultFields $actionResult @('success','action','before','expected','requested','persisted','applied','returned')
            $before = Get-ResultBoolean $actionResult['before']
            $expected = if ($MainOptionsAction -eq 'save') { -not $before } else { $before }
            if ((Get-ResultString $menu['action']) -cne $MainOptionsAction -or
                (Get-ResultString $actionResult['action']) -cne $MainOptionsAction -or
                -not (Get-ResultBoolean $actionResult['success']) -or
                (Get-ResultBoolean $actionResult['expected']) -ne $expected -or
                (Get-ResultBoolean $actionResult['requested']) -ne $expected -or
                (Get-ResultBoolean $actionResult['persisted']) -ne $expected -or
                (Get-ResultBoolean $actionResult['applied']) -ne $expected -or
                -not (Get-ResultString $actionResult['returned']).StartsWith('gamesettings-graphics',[StringComparison]::Ordinal)) {
                $failures.Add('MainOptionsAction')
            }
        }
        $actualAssembly = [IO.Path]::GetFullPath((Get-ResultString $menu['gameAssembly']))
        if (-not $ExpectedGameAssembly -or $actualAssembly -ine [IO.Path]::GetFullPath($ExpectedGameAssembly)) {
            $failures.Add('MainOptionsAssembly')
        }
        [void](Get-ResultInteger $menu['pid'])
        [void](Get-ResultInteger $menu['menuFrame'])
        if ((Get-ResultInteger $menu['requested']) -ne $ExpectedFrameCount -or
            (Get-ResultInteger $menu['written']) -ne $ExpectedFrameCount -or
            @(Get-ChildItem -LiteralPath $frameRoot -Filter 'frame-*.png' -File).Count -ne $ExpectedFrameCount -or
            @(Get-ChildItem -LiteralPath $frameRoot -Filter 'frame-*.ppm' -File).Count -ne $ExpectedFrameCount) {
            $failures.Add('MainOptionsFrameCount')
        }
        if ($RequireScenario) { $failures.Add('MainOptionsScenarioConflict') }
    }
    catch { $failures.Add('MalformedOrMissingMainOptionsResult') }
} else {
try {
    $legacy = Read-ResultObject (Join-Path $frameRoot 'headless-result.json')
    Require-ResultFields $legacy @('success','reason','hidden','focused','stagedModLoaded','modLocation',
        'worldReady','pid','worldFrame','requested','written')
    $success = Get-ResultBoolean $legacy['success']
    $hidden = Get-ResultBoolean $legacy['hidden']
    $focused = Get-ResultBoolean $legacy['focused']
    $staged = Get-ResultBoolean $legacy['stagedModLoaded']
    $worldReady = Get-ResultBoolean $legacy['worldReady']
    [void](Get-ResultString $legacy['reason'])
    [void](Get-ResultString $legacy['modLocation'])
    [void](Get-ResultInteger $legacy['pid'])
    [void](Get-ResultInteger $legacy['worldFrame'])
    $requested = Get-ResultInteger $legacy['requested']
    $written = Get-ResultInteger $legacy['written']
    if (-not $success) { $failures.Add('HeadlessFailure') }
    if ($ExpectedFrameCount -lt 0 -or $requested -ne $ExpectedFrameCount -or $written -ne $ExpectedFrameCount) {
        $failures.Add('LegacyFrameCount')
    }
    if (@(Get-ChildItem -LiteralPath $frameRoot -Filter 'frame-*.png' -File).Count -ne $ExpectedFrameCount -or
        @(Get-ChildItem -LiteralPath $frameRoot -Filter 'frame-*.ppm' -File).Count -ne $ExpectedFrameCount) {
        $failures.Add('LegacyFrameFiles')
    }
    if ($hidden -eq [bool]$Visible -or (-not $Visible -and $focused) -or -not $staged -or -not $worldReady) { $failures.Add('LegacyInvariant') }
}
catch { $failures.Add('MalformedOrMissingHeadlessResult') }
}

if ($RequireScenario) {
    try {
        $scenario = Read-ResultObject (Join-Path $frameRoot 'scenario-result.json')
        $manifest = Read-ResultObject (Join-Path $frameRoot 'scenario-captures.json')
        Require-ResultFields $scenario @('schema','id','sessionId','inputSha256','expectedActionCount',
            'executedActionCount','requestedScenarioCaptures','writtenScenarioCaptures','pairedScenarioCaptures',
            'legacyReady','complete','terminalPhase','success','error','failedPhase','finalCompletedFrameId',
            'hidden','focused','stagedModLoaded','worldReady')
        Require-ResultFields $manifest @('schema','sessionId','captures')
        $schema = Get-ResultInteger $scenario['schema']
        [void](Get-ResultString $scenario['id'])
        $sessionId = Get-ResultString $scenario['sessionId']
        Assert-ResultHash $scenario['inputSha256']
        $inputReference = Read-ResultObject (Join-Path $runRoot 'input/scenario-input.json')
        Require-ResultFields $inputReference @('id','path','sha256')
        $inputPath = Resolve-ResultPath $runRoot (Get-ResultString $inputReference['path'])
        $inputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
        if ($inputHash -ine (Get-ResultString $inputReference['sha256']) -or
            $inputHash -ine (Get-ResultString $scenario['inputSha256'])) { throw 'Scenario input hash mismatch.' }
        $inputScenario = Read-ResultObject $inputPath
        Require-ResultFields $inputScenario @('schema','id','actions')
        if ((Get-ResultInteger $inputScenario['schema']) -ne 1 -or
            (Get-ResultString $inputScenario['id']) -cne (Get-ResultString $scenario['id']) -or
            (Get-ResultString $inputScenario['id']) -cne (Get-ResultString $inputReference['id']) -or
            $inputScenario['actions'].ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw 'Scenario input identity mismatch.'
        }
        $inputActions = @($inputScenario['actions'].EnumerateArray())
        $inputCaptures = @($inputActions | Where-Object { $_.GetProperty('kind').GetString() -ceq 'capture' })
        $expectedActions = Get-ResultInteger $scenario['expectedActionCount']
        $executedActions = Get-ResultInteger $scenario['executedActionCount']
        if ($expectedActions -lt 1 -or $expectedActions -gt 128 -or $executedActions -gt 128) {
            throw 'Action count outside core range.'
        }
        $requestedCaptures = Get-ResultInteger $scenario['requestedScenarioCaptures']
        $writtenCaptures = Get-ResultInteger $scenario['writtenScenarioCaptures']
        $pairedCaptures = Get-ResultInteger $scenario['pairedScenarioCaptures']
        if ($expectedActions -ne $inputActions.Count -or $requestedCaptures -ne $inputCaptures.Count) {
            throw 'Scenario counts do not match the copied input.'
        }
        if ($requestedCaptures -gt $expectedActions -or $writtenCaptures -gt $expectedActions -or $pairedCaptures -gt $expectedActions) {
            throw 'Capture count exceeds declared actions.'
        }
        $legacyReady = Get-ResultBoolean $scenario['legacyReady']
        $complete = Get-ResultBoolean $scenario['complete']
        $success = Get-ResultBoolean $scenario['success']
        $phase = Get-ResultString $scenario['terminalPhase']
        $finalFrame = Get-ResultInteger $scenario['finalCompletedFrameId']
        $hidden = Get-ResultBoolean $scenario['hidden']
        $focused = Get-ResultBoolean $scenario['focused']
        $staged = Get-ResultBoolean $scenario['stagedModLoaded']
        $worldReady = Get-ResultBoolean $scenario['worldReady']
        Assert-NullableResultString $scenario['error']
        Assert-NullableResultString $scenario['failedPhase']
        $manifestSchema = Get-ResultInteger $manifest['schema']
        $manifestSession = Get-ResultString $manifest['sessionId']
        if ($manifest['captures'].ValueKind -ne [System.Text.Json.JsonValueKind]::Array) { throw 'captures must be JSON array.' }

        $captureIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $captureNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $captureIndexes = [Collections.Generic.HashSet[uint64]]::new()
        $capturePairsValid = $true
        foreach ($element in $manifest['captures'].EnumerateArray()) {
            $capture = Get-ResultObject $element
            Require-ResultFields $capture @('actionIndex','actionId','name','attachments','phase','sessionId',
                'scenarioTick','captureFrameId','completedFrameId','worldFrame','rawTemporalFrameId',
                'hasCurrentWorldSample','temporalFrameId','files')
            $index = Get-ResultInteger $capture['actionIndex']
            $actionId = Get-ResultString $capture['actionId']
            $name = Get-ResultString $capture['name']
            if ($index -ge $inputActions.Count) { throw 'Capture index is outside input actions.' }
            $inputAction = Get-ResultObject $inputActions[$index]
            Require-ResultFields $inputAction @('kind','id','name','attachments','tick')
            if ((Get-ResultString $inputAction['kind']) -cne 'capture' -or
                (Get-ResultString $inputAction['id']) -cne $actionId -or
                (Get-ResultString $inputAction['name']) -cne $name -or
                (Get-ResultBoolean $inputAction['attachments']) -ne (Get-ResultBoolean $capture['attachments'])) {
                throw 'Capture identity differs from its input action.'
            }
            if ($index -ge $expectedActions -or -not $captureIndexes.Add($index) -or
                -not $captureIds.Add($actionId) -or -not $captureNames.Add($name) -or
                -not [regex]::IsMatch($name, '\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z')) {
                throw 'Invalid or duplicate capture identity.'
            }
            [void](Get-ResultBoolean $capture['attachments'])
            $capturePhase = Get-ResultString $capture['phase']
            $captureSession = Get-ResultString $capture['sessionId']
            $tick = Get-ResultInteger $capture['scenarioTick']
            if ($tick -lt (Get-ResultInteger $inputAction['tick'])) { throw 'Capture predates its scheduled action.' }
            if ($tick -gt 100000) { throw 'Capture tick outside core range.' }
            $captureFrame = Get-ResultInteger $capture['captureFrameId']
            $completedFrame = Get-ResultInteger $capture['completedFrameId']
            [void](Get-ResultInteger $capture['worldFrame'])
            $rawFrame = Get-ResultInteger $capture['rawTemporalFrameId']
            $currentWorld = Get-ResultBoolean $capture['hasCurrentWorldSample']
            Assert-NullableResultInteger $capture['temporalFrameId']
            if ($currentWorld) {
                if ($capture['temporalFrameId'].ValueKind -eq [System.Text.Json.JsonValueKind]::Null -or
                    (Get-ResultInteger $capture['temporalFrameId']) -ne $rawFrame -or $rawFrame -ne $captureFrame) {
                    $capturePairsValid = $false
                }
            }
            elseif ($capture['temporalFrameId'].ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { $capturePairsValid = $false }
            if ($capture['files'].ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or $capture['files'].GetArrayLength() -lt 1) {
                throw 'Capture files must be nonempty JSON array.'
            }
            $filePaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($fileElement in $capture['files'].EnumerateArray()) {
                $file = Get-ResultObject $fileElement
                Require-ResultFields $file @('Path','Sha256','Width','Height')
                $filePath = Get-ResultString $file['Path']
                if (-not $filePaths.Add($filePath)) { throw 'Duplicate captured file path.' }
                Assert-ResultHash $file['Sha256']
                $actualFile = Resolve-ResultPath $frameRoot $filePath
                if (-not (Test-Path -LiteralPath $actualFile -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $actualFile -Algorithm SHA256).Hash -ine (Get-ResultString $file['Sha256'])) {
                    throw 'Captured file is missing or its SHA256 changed.'
                }
                Assert-NullableResultInteger $file['Width']
                Assert-NullableResultInteger $file['Height']
            }
            if ($capturePhase -cne 'preGenerateReadback' -or $captureSession -cne $sessionId -or
                $captureFrame -ne $completedFrame -or $completedFrame -gt $finalFrame) { $capturePairsValid = $false }
        }
        if ($schema -ne 1 -or -not $complete -or -not $success -or $phase -cne 'completedFrame' -or
            -not $legacyReady -or $scenario['error'].ValueKind -ne [System.Text.Json.JsonValueKind]::Null -or
            $scenario['failedPhase'].ValueKind -ne [System.Text.Json.JsonValueKind]::Null) { $failures.Add('ScenarioTerminal') }
        if ($hidden -eq [bool]$Visible -or (-not $Visible -and $focused) -or -not $staged -or -not $worldReady) { $failures.Add('ScenarioInvariant') }
        if ($expectedActions -ne $executedActions) { $failures.Add('ScenarioActionCount') }
        if ($requestedCaptures -ne $writtenCaptures -or $writtenCaptures -ne $pairedCaptures) { $failures.Add('ScenarioCaptureCount') }
        if ($manifestSchema -ne 1 -or $manifestSession -cne $sessionId -or
            $manifest['captures'].GetArrayLength() -ne $requestedCaptures) { $failures.Add('ScenarioCaptureManifest') }
        if (-not $capturePairsValid) { $failures.Add('ScenarioCaptureFramePair') }
    }
    catch { $failures.Add('MalformedOrMissingScenarioResult') }
}

$pass = $failures.Count -eq 0
@{
    pass = $pass
    errorCategory = if ($pass) { $null } else { 'HeadlessResult' }
    failures = @($failures | Select-Object -Unique)
    childStarted = $true
    processExitCode = $ProcessExitCode
    timedOut = $TimedOut
    scenarioRequired = [bool]$RequireScenario
    visibleRequested = [bool]$Visible
    mainMenuOptions = [bool]$MainMenuOptions
} | ConvertTo-Json -Compress
if (-not $pass) { exit 1 }
exit 0
