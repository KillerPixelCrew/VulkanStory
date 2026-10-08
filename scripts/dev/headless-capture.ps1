<#
.SYNOPSIS
Runs an isolated renderer capture against a copied world or the original main-menu Options host.
.DESCRIPTION
Validates scenario input before creating a fresh run. World mode uses SQLite backup of the named save; menu-only mode skips save copying. Copies client settings/mods into isolated data, verifies six muted audio settings, launches only its own dotnet client child with the staged startup hook, records stdout/stderr, and verifies result artifacts. Default window mode stays hidden; Visible/KeepOpen require explicit selection. Timeout terminates only this child. PreflightOnly validates scenario/path/input-profile preparation without creating a run or launching a child.
.PARAMETER PackageDirectory
Complete staged runtime root supplying the startup hook and ordinary mod directories.
.PARAMETER OutputDirectory
Fresh run directory; existing runs are rejected and never merged or deleted.
.PARAMETER GameDirectory
Official client installation used for runtimeconfig/deps/game assemblies; defaults to APPDATA/Vintagestory.
.PARAMETER SourceDataDirectory
User data root supplying clientsettings.json and the named save; defaults to APPDATA/VintagestoryData.
.PARAMETER World
Existing save basename without .vcdbs or directory components; defaults to foggy village story.
.PARAMETER Commands
Optional command file forwarded to the child after resolving its path.
.PARAMETER Frames
Optional explicit frame list forwarded to the child; unique nonnegative parsed entries determine the verifier's expected count.
.PARAMETER Count
Requested ordinary capture count when no explicit Frames list is supplied.
.PARAMETER Stride
Requested interval between ordinary captures, forwarded to the child.
.PARAMETER First
First requested ordinary capture frame and default parity-capture frame.
.PARAMETER CommandFrame
Frame boundary at which the child runs the supplied commands.
.PARAMETER FixedDt
Fixed frame timestep in seconds forwarded to the child, constrained to zero through one.
.PARAMETER TimeoutSeconds
Child timeout in seconds; the launcher checks at 30-second intervals and kills its own child after the additional grace.
.PARAMETER Upscaler
Initial isolated upscaler token: off, dlss, fsr3, fsr4, or xess.
.PARAMETER FrameGeneration
Initial isolated frame-generation token: off, dlss, fsr3, or xess.
.PARAMETER Scenario
Optional schema-one scenario JSON, validated then copied verbatim with its SHA256/identity into the run.
.PARAMETER ControllerEnabled
Currently rejected because the deferred child-owned controller input profile is required.
.PARAMETER TouchEnabled
Currently rejected because the deferred child-owned touch input profile is required.
.PARAMETER CompanionMode
Copies the input companion into isolated Mods when present; absent omits it.
.PARAMETER PreflightOnly
Returns compact JSON and exit 0/1 for preparation validation without creating a run directory or starting the client.
.PARAMETER ParityDump
Requests attachment dumps beneath the run's attachments directory; cannot combine with MainMenuOptions.
.PARAMETER ParityFrame
Explicit attachment-capture frame; a negative value uses First.
.PARAMETER AoOutputs
Enables the child's diagnostic AO output environment flag.
.PARAMETER AsyncPipelines
Enables asynchronous pipelines in the isolated child via its environment flag.
.PARAMETER Visible
Allows the diagnostic child window to be visible and changes verifier visibility expectations.
.PARAMETER KeepOpen
Requires Visible and disables child exit-on-capture-completion; the launcher's timeout still applies.
.PARAMETER MainMenuOptions
Selects menu-only Options capture without opening/copying a world; rejects world scenarios, commands, parity, and input switches.
.PARAMETER MainOptionsAction
Menu-only open/save/cancel action; Save/Cancel require the Image page.
.PARAMETER OptionsPage
Requested original Options page: Image, Generation, Effects, Device, or Status.
.PARAMETER Python
Python command used by snapshot-world.py for SQLite backup.
#>
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
    [switch]$MainMenuOptions,
    [ValidateSet('open','save','cancel')][string]$MainOptionsAction = 'open',
    [ValidateSet('Image','Generation','Effects','Device','Status')][string]$OptionsPage = 'Image',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
if ($KeepOpen -and -not $Visible) { throw 'KeepOpen requires the explicitly selected Visible diagnostic mode.' }
if ($MainMenuOptions -and ($Scenario -or $Commands -or $ParityDump -or $ControllerEnabled -or $TouchEnabled)) {
    throw 'MainMenuOptions is a distinct menu-only capture mode; world scenarios/commands/parity/input profiles cannot be combined.'
}
if (-not $MainMenuOptions -and $MainOptionsAction -ne 'open') { throw 'MainOptionsAction requires MainMenuOptions.' }
if ($OptionsPage -ne 'Image' -and $MainOptionsAction -ne 'open') { throw 'Main Save/Cancel diagnostics require the Image page.' }

function Throw-ScenarioError([string]$Category, [string]$Message) {
    throw "H01SCENARIO|$Category|$Message"
}

<#
.SYNOPSIS
Reads a scenario object with duplicate-name rejection.
.DESCRIPTION
Returns an ordinal JsonElement dictionary backed by the caller's live document. Nonobject values use Category; duplicate fields use DuplicateProperty.
.PARAMETER Element
Live JSON object element.
.PARAMETER Category
Shape-error category.
#>
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

<#
.SYNOPSIS
Checks allowed and required scenario property names.
.DESCRIPTION
Uses ordinal names and throws a categorized error for unknown or missing fields without mutating the dictionary.
.PARAMETER Properties
Scenario property dictionary.
.PARAMETER Allowed
Complete accepted field-name set.
.PARAMETER Required
Names that must occur.
.PARAMETER Category
Shape-error category.
#>
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

<#
.SYNOPSIS
Decodes one setting using its declared scalar type.
.DESCRIPTION
Accepts bool, Int32, finite Single, or string as selected by Type; wrong JSON kinds/ranges throw SettingsType. Choice normalization is separate.
.PARAMETER Name
Exact renderer setting name used in errors.
.PARAMETER Type
Declared scalar token: bool, int, float, or string.
.PARAMETER Element
Live JSON value to decode.
#>
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

<#
.SYNOPSIS
Validates a scenario settings action and advances validation choice state.
.DESCRIPTION
Rejects unknown fields, wrong scalar types, unsupported choices, and enabling deferred input. Mutates only the script-owned sequential validation state; it does not execute renderer settings.
.PARAMETER Values
Live JSON object holding setting changes.
#>
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

<#
.SYNOPSIS
Checks schema-one scenario actions before run creation.
.DESCRIPTION
Validates bounded ordered actions, unique identities, capture/checkpoint names, settings/assertions, and earlier delta baselines. Advances script-owned choice validation state and returns Id/ActionCount; errors carry H01SCENARIO categories.
.PARAMETER Root
Root element retained by the caller's live JsonDocument.
#>
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
if (-not $MainMenuOptions -and -not (Test-Path -LiteralPath $database)) { throw 'Existing user world is missing; refusing to create a replacement world.' }
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
if (-not $MainMenuOptions) {
    & $Python (Join-Path $PSScriptRoot 'snapshot-world.py') $database (Join-Path $dataRoot "Saves/$World.vcdbs")
    if ($LASTEXITCODE -ne 0) { throw 'Consistent world snapshot failed; no client launched.' }
}
foreach ($file in @('clientsettings.json')) {
    $source = Join-Path $sourceRoot $file
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $dataRoot }
}
$clientPath = Join-Path $dataRoot 'clientsettings.json'
$silentAudioKeys = @('masterSoundLevel','soundLevel','entitySoundLevel','ambientSoundLevel','weatherSoundLevel','musicLevel')
if (-not (Test-Path -LiteralPath $clientPath)) {
    @{ intSettings = @{} } | ConvertTo-Json | Set-Content -LiteralPath $clientPath
}
if (Test-Path -LiteralPath $clientPath) {
    $client = Get-Content -LiteralPath $clientPath -Raw | ConvertFrom-Json -AsHashtable
    <#
    .SYNOPSIS
    Adjusts copied client settings for the diagnostic child.
    .DESCRIPTION
    Recursively mutates dictionaries to mute audio, disable VSync/fullscreen/focus-pausing, and enable the two VulkanStory mods while preserving other disabled choices. Uses the enclosing silentAudioKeys.
    .PARAMETER node
    Copied settings node; nondictionary values are ignored.
    #>
    function Set-IsolatedSettings($node) {
        if ($node -isnot [System.Collections.IDictionary]) { return }
        foreach ($key in @($node.Keys)) {
            if ($key -in $silentAudioKeys -or $key -eq 'vsyncMode') { $node[$key] = 0 }
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
    if ($client['intSettings'] -isnot [System.Collections.IDictionary]) { $client['intSettings'] = @{} }
    foreach ($key in $silentAudioKeys) { $client['intSettings'][$key] = 0 }
    $client | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $clientPath
}
$mutedSettings = Get-Content -LiteralPath $clientPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($key in $silentAudioKeys) {
    if (-not $mutedSettings['intSettings'].Contains($key) -or $mutedSettings['intSettings'][$key] -ne 0) {
        throw ('Isolated audio mute failed: ' + $key + '; no client launched.')
    }
}
@{ silent = $true; settings = 'data/clientsettings.json'; mutedKeys = $silentAudioKeys } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'audio-mute.json')
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
$launchArguments = @('exec','--runtimeconfig',(Join-Path $gameRoot 'Vintagestory.runtimeconfig.json'),
    '--depsfile',(Join-Path $gameRoot 'Vintagestory.deps.json'),(Join-Path $gameRoot 'Vintagestory.dll'),
    '--dataPath',$dataRoot,'--addModPath',(Join-Path $dataRoot 'Mods'))
if (-not $MainMenuOptions) { $launchArguments += @('--openWorld',$World) }
foreach ($argument in $launchArguments) { $info.ArgumentList.Add($argument) }
$info.Environment['DOTNET_STARTUP_HOOKS'] = $hook
$info.Environment['VULKANSTORY_HEADLESS'] = '1'
$info.Environment['VULKANSTORY_HEADLESS_VISIBLE'] = if ($Visible) { '1' } else { '0' }
$info.Environment['VULKANSTORY_HEADLESS_MAIN_OPTIONS'] = if ($MainMenuOptions) { '1' } else { '0' }
$info.Environment['VULKANSTORY_HEADLESS_MAIN_ACTION'] = $MainOptionsAction
$info.Environment['VULKANSTORY_HEADLESS_OPTIONS_PAGE'] = $OptionsPage
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
    MainMenuOptions = [bool]$MainMenuOptions
    MainOptionsAction = $MainOptionsAction
    ExpectedGameAssembly = Join-Path $packageRoot 'VulkanStory/managed/VulkanStory.Game.dll'
}
if ($Scenario) { $verifyArguments.RequireScenario = $true }
& (Join-Path $PSScriptRoot 'verify-headless-result.ps1') @verifyArguments
if ($LASTEXITCODE -ne 0) { throw 'Headless result verification failed; inspect run artifacts.' }
Write-Host "Captured $expectedFrameCount frames in $runRoot. No installed files or user settings changed."
