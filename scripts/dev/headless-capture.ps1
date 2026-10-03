[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$GameDirectory = "$env:APPDATA/Vintagestory",
    [string]$SourceDataDirectory = "$env:APPDATA/VintagestoryData",
    [string]$World = 'foggy village story',
    [string]$Commands,
    [string]$Frames,
    [ValidateRange(1,100000)][int]$Count = 3,
    [ValidateRange(1,100000)][int]$Stride = 30,
    [ValidateRange(0,100000)][int]$First = 180,
    [ValidateRange(0,100000)][int]$CommandFrame = 30,
    [ValidateRange(0,1)][float]$FixedDt = 0.0166667,
    [ValidateRange(10,3600)][int]$TimeoutSeconds = 300,
    [ValidateSet('off','dlss','fsr3','fsr4','xess')][string]$Upscaler = 'off',
    [ValidateSet('off','dlss','fsr3','xess')][string]$FrameGeneration = 'off',
    [string]$Scenario,
    [switch]$ControllerEnabled,
    [switch]$TouchEnabled,
    [ValidateSet('present','absent')][string]$CompanionMode = 'present',
    [switch]$PreflightOnly,
    [switch]$ParityDump,
    [int]$ParityFrame = -1,
    [switch]$AoOutputs,
    [switch]$AsyncPipelines,
    [switch]$Visible,
    [switch]$KeepOpen,
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
if ($KeepOpen -and -not $Visible) { throw 'KeepOpen requires the explicitly selected Visible diagnostic mode.' }

function Throw-ScenarioError([string]$Category, [string]$Message) {
    throw "H01SCENARIO|$Category|$Message"
}

function Get-ScenarioProperties($Element, [string]$Category) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        Throw-ScenarioError $Category 'Expected a JSON object.'
    }
    $properties = [System.Collections.Generic.Dictionary[string,System.Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($properties.ContainsKey($property.Name)) {
            Throw-ScenarioError 'DuplicateProperty' "Duplicate JSON property: $($property.Name)"
        }
        $properties.Add($property.Name, $property.Value)
    }
    return ,$properties
}

function Require-ScenarioProperties($Properties, [string[]]$Allowed, [string[]]$Required, [string]$Category) {
    $allowedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Allowed) { [void]$allowedSet.Add($name) }
    foreach ($name in $Properties.Keys) {
        if (-not $allowedSet.Contains($name)) { Throw-ScenarioError $Category "Unknown property: $name" }
    }
    foreach ($name in $Required) {
        if (-not $Properties.ContainsKey($name)) { Throw-ScenarioError $Category "Missing property: $name" }
    }
}

function Get-ScenarioString($Element, [string]$Category) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
        Throw-ScenarioError $Category 'Expected a nonempty string.'
    }
    $value = $Element.GetString()
    if ([string]::IsNullOrEmpty($value)) { Throw-ScenarioError $Category 'Expected a nonempty string.' }
    return $value
}

function Get-ScenarioTick($Element) {
    $value = 0
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $Element.TryGetInt32([ref]$value) -or $value -lt 0 -or $value -gt 100000) {
        Throw-ScenarioError 'TickRange' 'Action ticks must be integers in 0..100000.'
    }
    return $value
}

function Get-ScenarioName($Element) {
    $value = Get-ScenarioString $Element 'CaptureName'
    if (-not [regex]::IsMatch($value, '\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z')) {
        Throw-ScenarioError 'CaptureName' 'Name must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}.'
    }
    return $value
}

function Get-ScenarioSettingValue([string]$Name, [string]$Type, $Element) {
    switch ($Type) {
        'bool' {
            if ($Element.ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) {
                Throw-ScenarioError 'SettingsType' "RendererSettings.$Name must be Boolean."
            }
            return $Element.GetBoolean()
        }
        'int' {
            $number = 0
            if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                -not $Element.TryGetInt32([ref]$number)) {
                Throw-ScenarioError 'SettingsType' "RendererSettings.$Name must be a 32-bit integer."
            }
            return $number
        }
        'float' {
            $number = [single]0
            if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
                -not $Element.TryGetSingle([ref]$number) -or -not [single]::IsFinite($number)) {
                Throw-ScenarioError 'SettingsType' "RendererSettings.$Name must be a finite number."
            }
            return $number
        }
        'string' {
            if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                Throw-ScenarioError 'SettingsType' "RendererSettings.$Name must be a string."
            }
            return $Element.GetString()
        }
    }
    Throw-ScenarioError 'SettingsType' "Unsupported RendererSettings type for $Name."
}

function Normalize-ScenarioChoice([string]$Value, [string[]]$Choices, [string]$Name) {
    foreach ($choice in $Choices) {
        if ([string]::Equals($Value.Trim(), $choice, [StringComparison]::OrdinalIgnoreCase)) { return $choice }
    }
    Throw-ScenarioError 'SettingsEnum' ("Unknown choice for RendererSettings." + $Name + ": " + $Value)
}

function Apply-ScenarioSettings($Values) {
    $properties = Get-ScenarioProperties $Values 'SettingsShape'
    foreach ($name in $properties.Keys) {
        if (-not $script:settingTypes.ContainsKey($name)) {
            Throw-ScenarioError 'SettingsProperty' "Unknown RendererSettings property: $name"
        }
    }
    $updates = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($name in $properties.Keys) {
        $value = Get-ScenarioSettingValue $name $script:settingTypes[$name] $properties[$name]
        if (($name -eq 'ControllerEnabled' -or $name -eq 'TouchEnabled') -and $value) {
            Throw-ScenarioError 'InputProfileRequired' ($name + '=true requires the deferred child-owned input profile.')
        }
        $updates.Add($name, $value)
    }

    $upscalerInput = if ($updates.ContainsKey('Upscaler')) { $updates['Upscaler'] } else { $script:scenarioSettingsState['Upscaler'] }
    $upscaler = Normalize-ScenarioChoice $upscalerInput @('off','dlss','xess','fsr3','fsr4') 'Upscaler'
    $qualityInput = if ($updates.ContainsKey('UpscalerQuality')) { $updates['UpscalerQuality'] } else { $script:scenarioSettingsState['UpscalerQuality'] }
    $qualityChoices = if ($upscaler -eq 'xess') {
        @('dlaa','ultraquality','ultraqualityplus','quality','balanced','performance','ultraperformance')
    } else { @('dlaa','quality','balanced','performance','ultraperformance') }
    if ($updates.ContainsKey('UpscalerQuality')) {
        $quality = Normalize-ScenarioChoice $qualityInput $qualityChoices 'UpscalerQuality'
    }
    elseif ($qualityChoices -cnotcontains $qualityInput) { $quality = 'quality' }
    else { $quality = Normalize-ScenarioChoice $qualityInput $qualityChoices 'UpscalerQuality' }
    $frameGenerationInput = if ($updates.ContainsKey('FrameGeneration')) { $updates['FrameGeneration'] } else { $script:scenarioSettingsState['FrameGeneration'] }
    $frameGeneration = Normalize-ScenarioChoice $frameGenerationInput @('off','dlss','fsr3','xess') 'FrameGeneration'
    $latencyInput = if ($updates.ContainsKey('LowLatencyMode')) { $updates['LowLatencyMode'] } else { $script:scenarioSettingsState['LowLatencyMode'] }
    $latency = Normalize-ScenarioChoice $latencyInput @('off','on','boost') 'LowLatencyMode'
    $aoInput = if ($updates.ContainsKey('AmbientOcclusion')) { $updates['AmbientOcclusion'] } else { $script:scenarioSettingsState['AmbientOcclusion'] }
    $ao = Normalize-ScenarioChoice $aoInput @('auto','vanilla','gtao') 'AmbientOcclusion'
    $aoPresetInput = if ($updates.ContainsKey('AmbientOcclusionPreset')) { $updates['AmbientOcclusionPreset'] } else { $script:scenarioSettingsState['AmbientOcclusionPreset'] }
    $aoPreset = Normalize-ScenarioChoice $aoPresetInput @('low','medium','high','ultra') 'AmbientOcclusionPreset'
    $script:scenarioSettingsState['Upscaler'] = $upscaler
    $script:scenarioSettingsState['UpscalerQuality'] = $quality
    $script:scenarioSettingsState['FrameGeneration'] = $frameGeneration
    $script:scenarioSettingsState['LowLatencyMode'] = $latency
    $script:scenarioSettingsState['AmbientOcclusion'] = $ao
    $script:scenarioSettingsState['AmbientOcclusionPreset'] = $aoPreset
}

function Get-ScenarioNumber($Element, [string]$Category) {
    $number = 0.0
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $Element.TryGetDouble([ref]$number) -or -not [double]::IsFinite($number)) {
        Throw-ScenarioError $Category 'Expected a finite JSON number.'
    }
    return $number
}

function Test-ScenarioDocument($Root) {
    $rootProperties = Get-ScenarioProperties $Root 'ScenarioField'
    Require-ScenarioProperties $rootProperties @('schema','id','actions') @('schema','id','actions') 'ScenarioField'
    $schema = 0
    if ($rootProperties['schema'].ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $rootProperties['schema'].TryGetInt32([ref]$schema) -or $schema -ne 1) {
        Throw-ScenarioError 'SchemaVersion' 'Scenario schema must be integer 1.'
    }
    $scenarioId = Get-ScenarioString $rootProperties['id'] 'ScenarioId'
    $actions = $rootProperties['actions']
    if ($actions.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
        $actions.GetArrayLength() -lt 1 -or $actions.GetArrayLength() -gt 128) {
        Throw-ScenarioError 'ActionsRange' 'Scenario must contain 1..128 actions.'
    }
    $ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $captureNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $checkpointIndexes = [System.Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    $previousTick = -1
    $index = 0
    foreach ($action in $actions.EnumerateArray()) {
        $properties = Get-ScenarioProperties $action 'ActionField'
        Require-ScenarioProperties $properties @('id','tick','kind','values','name','attachments','field','op','expected','baseline') @('id','tick','kind') 'ActionField'
        $actionId = Get-ScenarioString $properties['id'] 'ActionId'
        if (-not $ids.Add($actionId)) { Throw-ScenarioError 'DuplicateId' "Action IDs must be unique: $actionId" }
        $tick = Get-ScenarioTick $properties['tick']
        if ($tick -lt $previousTick) { Throw-ScenarioError 'TickRange' 'Action ticks must be nondecreasing.' }
        $previousTick = $tick
        $kind = Get-ScenarioString $properties['kind'] 'ActionKind'
        switch -CaseSensitive ($kind) {
            'settings' {
                Require-ScenarioProperties $properties @('id','tick','kind','values') @('values') 'ActionField'
                Apply-ScenarioSettings $properties['values']
            }
            'capture' {
                Require-ScenarioProperties $properties @('id','tick','kind','name','attachments') @('name','attachments') 'ActionField'
                $name = Get-ScenarioName $properties['name']
                if (-not $captureNames.Add($name)) { Throw-ScenarioError 'CaptureName' "Capture names must be unique: $name" }
                if ($properties['attachments'].ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) {
                    Throw-ScenarioError 'CaptureAttachments' 'Capture attachments must be Boolean.'
                }
            }
            'checkpoint' {
                Require-ScenarioProperties $properties @('id','tick','kind','name') @('name') 'ActionField'
                $name = Get-ScenarioName $properties['name']
                if ($checkpointIndexes.ContainsKey($name)) { Throw-ScenarioError 'CheckpointName' "Checkpoint names must be unique: $name" }
                $checkpointIndexes.Add($name, $index)
            }
            'assert' {
                Require-ScenarioProperties $properties @('id','tick','kind','field','op','expected','baseline') @('field','op','expected') 'ActionField'
                $field = Get-ScenarioString $properties['field'] 'AssertionField'
                if (-not $script:assertionKinds.ContainsKey($field)) { Throw-ScenarioError 'AssertionField' "Unknown assertion field: $field" }
                $operator = Get-ScenarioString $properties['op'] 'AssertionOperator'
                if ($operator -cnotin @('eq','gte','deltaGte')) { Throw-ScenarioError 'AssertionOperator' "Unknown assertion operator: $operator" }
                $expected = $properties['expected']
                $fieldType = $script:assertionKinds[$field]
                $nullable = $fieldType.EndsWith('?')
                $baseType = $fieldType.TrimEnd('?')
                if ($operator -eq 'eq') {
                    if ($expected.ValueKind -eq [System.Text.Json.JsonValueKind]::Null -and $nullable) { }
                    elseif ($baseType -eq 'bool' -and $expected.ValueKind -notin @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) {
                        Throw-ScenarioError 'AssertionExpected' 'eq expected value must be Boolean.'
                    }
                    elseif ($baseType -eq 'number') { [void](Get-ScenarioNumber $expected 'AssertionExpected') }
                    elseif ($baseType -eq 'string' -and $expected.ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                        Throw-ScenarioError 'AssertionExpected' 'eq expected value must be a string.'
                    }
                    elseif ($expected.ValueKind -eq [System.Text.Json.JsonValueKind]::Null) {
                        Throw-ScenarioError 'AssertionExpected' 'Null is allowed only for nullable fields.'
                    }
                }
                else {
                    if ($baseType -ne 'number') { Throw-ScenarioError 'AssertionExpected' "$operator requires a numeric assertion field." }
                    [void](Get-ScenarioNumber $expected 'AssertionExpected')
                }
                if ($expected.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                    $choice = $expected.GetString()
                    $choices = switch -CaseSensitive ($field) {
                        { $_ -in @('requestedUpscaler','effectiveUpscaler') } { @('off','dlss','xess','fsr3','fsr4'); break }
                        { $_ -in @('requestedFrameGeneration','effectiveFrameGeneration') } { @('off','dlss','fsr3','xess'); break }
                        'phase' { @('completedFrame'); break }
                        default { $null }
                    }
                    if ($null -ne $choices -and -not ($choices -ccontains $choice)) {
                        Throw-ScenarioError 'AssertionExpected' ("Unknown assertion choice for " + $field + ": " + $choice)
                    }
                }
                if ($operator -eq 'deltaGte') {
                    if ($field -notin @('successfulUpscaleFrames','preparedFrames','realPresents','sdkReportedPresents')) {
                        Throw-ScenarioError 'AssertionField' 'deltaGte is supported only for monotonic session totals.'
                    }
                    if (-not $properties.ContainsKey('baseline')) { Throw-ScenarioError 'AssertionBaseline' 'deltaGte requires a baseline checkpoint.' }
                    $baseline = Get-ScenarioString $properties['baseline'] 'AssertionBaseline'
                    if (-not $checkpointIndexes.ContainsKey($baseline) -or $checkpointIndexes[$baseline] -ge $index) {
                        Throw-ScenarioError 'AssertionBaseline' 'deltaGte baseline must name an earlier checkpoint.'
                    }
                }
                elseif ($properties.ContainsKey('baseline')) {
                    Throw-ScenarioError 'AssertionBaseline' 'baseline is allowed only for deltaGte.'
                }
            }
            default { Throw-ScenarioError 'ActionKind' "Unknown scenario action kind: $kind" }
        }
        $index++
    }
    return @{ Id = $scenarioId; ActionCount = $index }
}

$script:settingTypes = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($name in @('Enabled','Taa','TaaJitterDev','ShowFpsCounter','AmbientOcclusionDebugView','GodRaysSampleCap','HandheldShadowTier','NativeShaders','Streamline','ControllerEnabled','TouchEnabled')) {
    $script:settingTypes.Add($name, 'bool')
}
foreach ($name in @('RenderScale','TaaSharpness','TaaMipBias','UpscalerLodBiasOffset')) { $script:settingTypes.Add($name, 'float') }
foreach ($name in @('TaaDebugView','FrameGenerationMultiplier')) { $script:settingTypes.Add($name, 'int') }
foreach ($name in @('Upscaler','UpscalerQuality','FrameGeneration','LowLatencyMode','AmbientOcclusion','AmbientOcclusionPreset')) {
    $script:settingTypes.Add($name, 'string')
}
$script:assertionKinds = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
foreach ($name in @('hidden','focused','stagedModLoaded','worldReady','hasCurrentWorldSample','inputsPreparedThisFrame','cpuRenderCycleSucceeded','presentCallReturned')) {
    $script:assertionKinds.Add($name, 'bool')
}
foreach ($name in @('WorldCaptured','MotionValid','HasCamera','temporalReset')) { $script:assertionKinds.Add($name, 'bool?') }
foreach ($name in @('temporalFrameId','renderWidth','renderHeight','configuredDlssGeneratedFrames','dlssStateQueryFrameId','dlssStateQueryResult','dlssMaximumGenerated','dlssDynamicMfgSupport','successfulUpscaleFrames','preparedFrames')) {
    $script:assertionKinds.Add($name, 'number?')
}
foreach ($name in @('displayWidth','displayHeight','realPresents','sdkReportedPresents','sdkReportedDlssPresents','scenarioTick','worldFrame','sampledAtFrameId','completedFrameId')) {
    $script:assertionKinds.Add($name, 'number')
}
foreach ($name in @('requestedUpscaler','effectiveUpscaler','requestedFrameGeneration','effectiveFrameGeneration','preparationStatus','sessionId','phase')) {
    $script:assertionKinds.Add($name, 'string')
}
$script:scenarioSettingsState = @{
    Upscaler = $Upscaler
    UpscalerQuality = 'quality'
    FrameGeneration = $FrameGeneration
    LowLatencyMode = 'on'
    AmbientOcclusion = 'auto'
    AmbientOcclusionPreset = 'medium'
}

$scenarioSourcePath = $null
$scenarioSourceBytes = $null
$scenarioId = $null
$scenarioInputHash = $null
$runRoot = $null
try {
    $runRoot = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $runRoot) { Throw-ScenarioError 'OutputDirectory' 'Use a fresh output directory; captures never merge or delete runs.' }
    if ($Scenario) {
        $scenarioSourcePath = [IO.Path]::GetFullPath($Scenario)
        if (-not (Test-Path -LiteralPath $scenarioSourcePath -PathType Leaf)) {
            Throw-ScenarioError 'ScenarioPath' 'Scenario file does not exist.'
        }
        $jsonOptions = [System.Text.Json.JsonDocumentOptions]::new()
        $jsonOptions.MaxDepth = 32
        $scenarioSourceBytes = [IO.File]::ReadAllBytes($scenarioSourcePath)
        $scenarioStream = [IO.MemoryStream]::new($scenarioSourceBytes)
        try { $scenarioDocument = [System.Text.Json.JsonDocument]::Parse($scenarioStream, $jsonOptions) }
        catch { Throw-ScenarioError 'MalformedJson' $_.Exception.Message }
        finally { $scenarioStream.Dispose() }
        try { $scenarioMetadata = Test-ScenarioDocument $scenarioDocument.RootElement }
        finally { $scenarioDocument.Dispose() }
        $scenarioId = $scenarioMetadata.Id
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $scenarioInputHash = [Convert]::ToHexString($sha.ComputeHash($scenarioSourceBytes)).ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    if ($ControllerEnabled) { Throw-ScenarioError 'InputProfileRequired' '-ControllerEnabled requires the deferred child-owned input profile.' }
    if ($TouchEnabled) { Throw-ScenarioError 'InputProfileRequired' '-TouchEnabled requires the deferred child-owned input profile.' }
    if ($PreflightOnly) {
        [ordered]@{ pass=$true; errorCategory=$null; error=$null; scenarioId=$scenarioId; inputSha256=$scenarioInputHash;
            childStarted=$false; childPid=$null; runDirectoryCreated=$false } | ConvertTo-Json -Compress
        exit 0
    }
}
catch {
    if (-not $PreflightOnly) { throw }
    $message = $_.Exception.Message
    $category = 'Path'
    $detail = $message
    if ($message -match '^H01SCENARIO\|([^|]+)\|(.*)$') { $category = $Matches[1]; $detail = $Matches[2] }
    [ordered]@{ pass=$false; errorCategory=$category; error=$detail; scenarioId=$scenarioId; inputSha256=$scenarioInputHash;
        childStarted=$false; childPid=$null; runDirectoryCreated=$false } | ConvertTo-Json -Compress
    exit 1
}

$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$sourceRoot = (Resolve-Path -LiteralPath $SourceDataDirectory).Path
if ([IO.Path]::GetFileName($World) -ne $World -or $World.EndsWith('.vcdbs')) { throw 'World must be a save basename, without extension.' }
$database = Join-Path $sourceRoot "Saves/$World.vcdbs"
if (-not (Test-Path -LiteralPath $database)) { throw 'Existing user world is missing; refusing to create a replacement world.' }
$hook = Join-Path $packageRoot 'VulkanStory/managed/VulkanStory.Bootstrap.dll'
if (-not (Test-Path -LiteralPath $hook)) { throw 'Supply a complete staged runtime package.' }
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
Get-Command $Python -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $runRoot | Out-Null
$dataRoot = Join-Path $runRoot 'data'
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'ModConfig') -Force | Out-Null
if ($scenarioSourcePath) {
    $inputRoot = Join-Path $runRoot 'input'
    New-Item -ItemType Directory -Path $inputRoot | Out-Null
    $copiedScenarioPath = Join-Path $inputRoot 'scenario.json'
    [IO.File]::WriteAllBytes($copiedScenarioPath, $scenarioSourceBytes)
    $copiedScenarioHash = (Get-FileHash -LiteralPath $copiedScenarioPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copiedScenarioHash -ne $scenarioInputHash) { throw 'Scenario copy hash differs from the validated input.' }
    [ordered]@{ id=$scenarioId; path='input/scenario.json'; sha256=$scenarioInputHash } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $inputRoot 'scenario-input.json')
}
& $Python (Join-Path $PSScriptRoot 'snapshot-world.py') $database (Join-Path $dataRoot "Saves/$World.vcdbs")
if ($LASTEXITCODE -ne 0) { throw 'Consistent world snapshot failed; no client launched.' }
foreach ($file in @('clientsettings.json','clientsettings.cache')) {
    $source = Join-Path $sourceRoot $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $dataRoot }
}
$clientPath = Join-Path $dataRoot 'clientsettings.json'
if (Test-Path -LiteralPath $clientPath) {
    $client = Get-Content -LiteralPath $clientPath -Raw | ConvertFrom-Json -AsHashtable
    function Set-IsolatedSettings($node) {
        if ($node -isnot [System.Collections.IDictionary]) { return }
        foreach ($key in @($node.Keys)) {
            if ($key -in @('soundLevel','musicLevel','vsyncMode')) { $node[$key] = 0 }
            elseif ($key -eq 'pauseGameOnLostFocus') { $node[$key] = $false }
            elseif ($key -eq 'fullScreen') { $node[$key] = $false }
            elseif ($key -eq 'disabledMods') {
                # Only this isolated renderer run enables its own two mods.
                # Preserve the user's disabled choices for every other mod.
                $node[$key] = @($node[$key] | Where-Object {
                    $_ -notin @('vulkanstory','vulkanstoryinput') -and
                    $_ -notmatch '^vulkanstory(?:input)?@'
                })
            }
            else { Set-IsolatedSettings $node[$key] }
        }
    }
    Set-IsolatedSettings $client
    $client | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $clientPath
}
# Mod discovery remains the game's own loader. Private runtime is loaded from
# this package, so an installed older ordinary mod must not shadow this copy.
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Mods') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packageRoot 'Mods/vulkanstory') -Destination (Join-Path $dataRoot 'Mods') -Recurse
if ($CompanionMode -eq 'present') {
    Copy-Item -LiteralPath (Join-Path $packageRoot 'Mods/vulkanstoryinput') -Destination (Join-Path $dataRoot 'Mods') -Recurse
}
@{ Enabled=$true; Upscaler=$Upscaler; UpscalerQuality='quality'; FrameGeneration=$FrameGeneration; ShowFpsCounter=$true; Streamline=$true; ControllerEnabled=$false; TouchEnabled=$false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'ModConfig/vulkanstory.json')
$info = [Diagnostics.ProcessStartInfo]::new($dotnet)
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.WorkingDirectory = $gameRoot
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
foreach ($argument in @('exec','--runtimeconfig',(Join-Path $gameRoot 'Vintagestory.runtimeconfig.json'),
    '--depsfile',(Join-Path $gameRoot 'Vintagestory.deps.json'),(Join-Path $gameRoot 'Vintagestory.dll'),
    '--dataPath',$dataRoot,'--openWorld',$World,'--addModPath',(Join-Path $dataRoot 'Mods'))) { $info.ArgumentList.Add($argument) }
$info.Environment['DOTNET_STARTUP_HOOKS'] = $hook
$info.Environment['VULKANSTORY_HEADLESS'] = '1'
$info.Environment['VULKANSTORY_HEADLESS_VISIBLE'] = if ($Visible) { '1' } else { '0' }
$info.Environment['VULKANSTORY_HEADLESS_MOD_DIRECTORY'] = Join-Path $dataRoot 'Mods/vulkanstory'
$info.Environment['VULKANSTORY_HEADLESS_FRAMES'] = Join-Path $runRoot 'frames'
$info.Environment['VULKANSTORY_HEADLESS_EXIT_WHEN_DONE'] = if ($KeepOpen) { '0' } else { '1' }
$info.Environment['VULKANSTORY_HEADLESS_FRAME_COUNT'] = "$Count"
$info.Environment['VULKANSTORY_HEADLESS_FRAME_STRIDE'] = "$Stride"
$info.Environment['VULKANSTORY_HEADLESS_FIRST_FRAME'] = "$First"
$info.Environment['VULKANSTORY_HEADLESS_FIXED_DT'] = $FixedDt.ToString([Globalization.CultureInfo]::InvariantCulture)
$info.Environment['VULKANSTORY_HEADLESS_COMMAND_FRAME'] = "$CommandFrame"
$info.Environment['VULKANSTORY_HEADLESS_TIMEOUT'] = "$TimeoutSeconds"
$info.Environment['VULKANSTORY_HEADLESS_UPSCALER'] = $Upscaler
$info.Environment['VULKANSTORY_HEADLESS_FRAME_GENERATION'] = $FrameGeneration
if ($copiedScenarioPath) { $info.Environment['VULKANSTORY_HEADLESS_SCENARIO'] = $copiedScenarioPath }
if ($AsyncPipelines) { $info.Environment['VULKANSTORY_HEADLESS_ASYNC_PIPELINES'] = '1' }
$info.Environment['VULKANSTORY_RUNTIME_DIAGNOSTICS'] = Join-Path $runRoot 'status'
$info.Environment['VULKANSTORY_BOOTSTRAP_LOG'] = Join-Path $runRoot 'bootstrap.jsonl'
if ($Frames) { $info.Environment['VULKANSTORY_HEADLESS_FRAME_LIST'] = $Frames }
if ($Commands) { $info.Environment['VULKANSTORY_HEADLESS_COMMANDS'] = (Resolve-Path -LiteralPath $Commands).Path }
if ($ParityDump) {
    $info.Environment['VULKANSTORY_PARITY_DUMP'] = Join-Path $runRoot 'attachments'
    $info.Environment['VULKANSTORY_PARITY_FRAME'] = if ($ParityFrame -ge 0) { "$ParityFrame" } else { "$First" }
}
if ($AoOutputs) { $info.Environment['VULKANSTORY_AO_OUTPUTS'] = '1' }
$process = [Diagnostics.Process]::Start($info)
try { $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal } catch { Write-Warning 'Could not lower harness process priority.' }
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
Write-Host "Headless capture PID $($process.Id), isolated data: $dataRoot"
$deadline = [Diagnostics.Stopwatch]::StartNew()
$timedOut = $false
while (-not $process.WaitForExit(30000)) {
    Write-Host "Headless PID $($process.Id) remains running."
    if ($deadline.Elapsed.TotalSeconds -gt $TimeoutSeconds + 30) {
        $process.Kill() # Only the child handle this run created, never a game-name search.
        $timedOut = $true
        $process.WaitForExit()
        break
    }
}
[IO.File]::WriteAllText((Join-Path $runRoot 'stdout.log'), $stdout.GetAwaiter().GetResult())
[IO.File]::WriteAllText((Join-Path $runRoot 'stderr.log'), $stderr.GetAwaiter().GetResult())
$expectedFrameCount = $Count
if (-not [string]::IsNullOrWhiteSpace($Frames)) {
    $frameSet = [System.Collections.Generic.HashSet[long]]::new()
    foreach ($part in ($Frames.Split([char[]]@(',', ' ', ';'), [StringSplitOptions]::RemoveEmptyEntries))) {
        $frame = 0L
        if ([long]::TryParse($part.Trim(), [Globalization.NumberStyles]::Integer,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$frame) -and $frame -ge 0) {
            [void]$frameSet.Add($frame)
        }
    }
    $expectedFrameCount = $frameSet.Count
}
$verifyArguments = @{
    RunDirectory = $runRoot
    ExpectedFrameCount = $expectedFrameCount
    ProcessExitCode = $process.ExitCode
    TimedOut = $timedOut
    Visible = [bool]$Visible
}
if ($Scenario) { $verifyArguments.RequireScenario = $true }
& (Join-Path $PSScriptRoot 'verify-headless-result.ps1') @verifyArguments
if ($LASTEXITCODE -ne 0) { throw 'Headless result verification failed; inspect run artifacts.' }
Write-Host "Captured $expectedFrameCount frames in $runRoot. No installed files or user settings changed."
