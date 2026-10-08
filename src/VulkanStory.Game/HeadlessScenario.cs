using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VulkanStory.Game;

/// <summary>Supported deterministic operations in an isolated headless scenario.</summary>
internal enum HeadlessScenarioActionKind
{
    Settings,
    Capture,
    Checkpoint,
    Assert
}

/// <summary>Validated action scheduled at a specific scenario frame, including optional settings and capture expectations.</summary>
internal sealed record HeadlessScenarioAction(
    int Index,
    string Id,
    int Tick,
    HeadlessScenarioActionKind Kind,
    string? Name = null,
    bool Attachments = false,
    JsonElement Values = default,
    string? Field = null,
    string? Operator = null,
    JsonElement Expected = default,
    string? Baseline = null);

/// <summary>Identifies the scenario validation category alongside its failure detail.</summary>
internal sealed class HeadlessScenarioValidationException(string category, string message) : Exception(message)
{
    internal string Category { get; } = category;
}

/// <summary>Loads and validates the scenario document before session execution; action support and observation scope are explicit.</summary>
internal sealed class HeadlessScenario
{
    private static readonly JsonSerializerOptions SettingsJson = new() { PropertyNameCaseInsensitive = false };
    private static readonly System.Text.RegularExpressions.Regex NamePattern =
        new(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, (JsonValueKind Kind, bool Nullable)> AssertionFields =
        new Dictionary<string, (JsonValueKind, bool)>(StringComparer.Ordinal)
        {
            ["hidden"] = (JsonValueKind.True, false),
            ["focused"] = (JsonValueKind.True, false),
            ["stagedModLoaded"] = (JsonValueKind.True, false),
            ["worldReady"] = (JsonValueKind.True, false),
            ["hasCurrentWorldSample"] = (JsonValueKind.True, false),
            ["WorldCaptured"] = (JsonValueKind.True, true),
            ["MotionValid"] = (JsonValueKind.True, true),
            ["HasCamera"] = (JsonValueKind.True, true),
            ["temporalReset"] = (JsonValueKind.True, true),
            ["temporalFrameId"] = (JsonValueKind.Number, true),
            ["requestedUpscaler"] = (JsonValueKind.String, false),
            ["effectiveUpscaler"] = (JsonValueKind.String, false),
            ["requestedFrameGeneration"] = (JsonValueKind.String, false),
            ["effectiveFrameGeneration"] = (JsonValueKind.String, false),
            ["inputsPreparedThisFrame"] = (JsonValueKind.True, false),
            ["preparationStatus"] = (JsonValueKind.String, false),
            ["renderWidth"] = (JsonValueKind.Number, true),
            ["renderHeight"] = (JsonValueKind.Number, true),
            ["displayWidth"] = (JsonValueKind.Number, false),
            ["displayHeight"] = (JsonValueKind.Number, false),
            ["configuredDlssGeneratedFrames"] = (JsonValueKind.Number, true),
            ["dlssStateQueryFrameId"] = (JsonValueKind.Number, true),
            ["dlssStateQueryResult"] = (JsonValueKind.Number, true),
            ["dlssMaximumGenerated"] = (JsonValueKind.Number, true),
            ["dlssDynamicMfgSupport"] = (JsonValueKind.Number, true),
            ["successfulUpscaleFrames"] = (JsonValueKind.Number, true),
            ["preparedFrames"] = (JsonValueKind.Number, true),
            ["realPresents"] = (JsonValueKind.Number, false),
            ["hostGeneratedPresents"] = (JsonValueKind.Number, false),
            ["sdkReportedPresents"] = (JsonValueKind.Number, false),
            ["sdkReportedDlssPresents"] = (JsonValueKind.Number, false),
            ["cpuRenderCycleSucceeded"] = (JsonValueKind.True, false),
            ["presentCallReturned"] = (JsonValueKind.True, false),
            ["scenarioTick"] = (JsonValueKind.Number, false),
            ["worldFrame"] = (JsonValueKind.Number, false),
            ["sampledAtFrameId"] = (JsonValueKind.Number, false),
            ["completedFrameId"] = (JsonValueKind.Number, false),
            ["sessionId"] = (JsonValueKind.String, false),
            ["phase"] = (JsonValueKind.String, false),
        };
    private static readonly HashSet<string> MonotonicTotals = new(StringComparer.Ordinal)
    {
        "successfulUpscaleFrames", "preparedFrames", "realPresents", "hostGeneratedPresents", "sdkReportedPresents"
    };

    private HeadlessScenario(string path, string inputHash, string id, IReadOnlyList<HeadlessScenarioAction> actions)
    {
        InputPath = path;
        InputSha256 = inputHash;
        Id = id;
        Actions = actions;
    }

    internal string InputPath { get; }
    internal string InputSha256 { get; }
    internal string Id { get; }
    internal IReadOnlyList<HeadlessScenarioAction> Actions { get; }
    internal static IReadOnlyDictionary<string, (JsonValueKind Kind, bool Nullable)> Fields => AssertionFields;

    /// <summary>Loads, hashes and validates the complete isolated scenario before any action executes.</summary>
    /// <param name="path">Scenario JSON pathname.</param>
    /// <returns>Validated scenario with supported actions and observation scopes.</returns>
    /// <remarks>Malformed or unsupported instructions throw a categorized validation exception; file errors propagate.</remarks>
    internal static HeadlessScenario Load(string path)
    {
        string fullPath;
        byte[] bytes;
        try
        {
            fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new HeadlessScenarioValidationException("ScenarioPath", "Scenario file does not exist.");
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (HeadlessScenarioValidationException) { throw; }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new HeadlessScenarioValidationException("ScenarioPath", error.Message);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            return Parse(fullPath, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), document.RootElement);
        }
        catch (HeadlessScenarioValidationException) { throw; }
        catch (JsonException error)
        {
            throw new HeadlessScenarioValidationException("MalformedJson", error.Message);
        }
    }

    private static HeadlessScenario Parse(string path, string inputHash, JsonElement root)
    {
        Dictionary<string, JsonElement> rootProperties = ObjectProperties(root, "ScenarioField");
        RequireOnly(rootProperties, ["schema", "id", "actions"], "ScenarioField");
        RequireAll(rootProperties, ["schema", "id", "actions"], "ScenarioField");
        if (rootProperties["schema"].ValueKind != JsonValueKind.Number ||
            !rootProperties["schema"].TryGetInt32(out int schema) || schema != 1)
            throw Error("SchemaVersion", "Scenario schema must be integer 1.");
        string id = RequiredString(rootProperties["id"], "ScenarioId");
        JsonElement actionArray = rootProperties["actions"];
        if (actionArray.ValueKind != JsonValueKind.Array)
            throw Error("ActionsRange", "Scenario actions must be an array.");
        if (actionArray.GetArrayLength() is < 1 or > 128)
            throw Error("ActionsRange", "Scenario must contain 1..128 actions.");

        var actions = new List<HeadlessScenarioAction>(actionArray.GetArrayLength());
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var captureNames = new HashSet<string>(StringComparer.Ordinal);
        var checkpointNames = new Dictionary<string, int>(StringComparer.Ordinal);
        var settings = InitialSettings();
        int previousTick = -1, index = 0;
        foreach (JsonElement rawAction in actionArray.EnumerateArray())
        {
            Dictionary<string, JsonElement> properties = ObjectProperties(rawAction, "ActionField");
            RequireAll(properties, ["id", "tick", "kind"], "ActionField");
            string actionId = RequiredString(properties["id"], "ActionId");
            if (!ids.Add(actionId)) throw Error("DuplicateId", "Action IDs must be unique: " + actionId);
            if (properties["tick"].ValueKind != JsonValueKind.Number ||
                !properties["tick"].TryGetInt32(out int tick) || tick is < 0 or > 100000 || tick < previousTick)
                throw Error("TickRange", "Action ticks must be nondecreasing integers in 0..100000.");
            previousTick = tick;
            string kindText = RequiredString(properties["kind"], "ActionKind");
            HeadlessScenarioAction action;
            switch (kindText)
            {
                case "settings":
                {
                    RequireOnly(properties, ["id", "tick", "kind", "values"], "ActionField");
                    RequireAll(properties, ["values"], "ActionField");
                    settings = ValidateSettingsValues(properties["values"], settings);
                    action = new(index, actionId, tick, HeadlessScenarioActionKind.Settings,
                        Values: properties["values"].Clone());
                    break;
                }
                case "capture":
                {
                    RequireOnly(properties, ["id", "tick", "kind", "name", "attachments"], "ActionField");
                    RequireAll(properties, ["name", "attachments"], "ActionField");
                    string name = RequiredName(properties["name"]);
                    if (!captureNames.Add(name)) throw Error("CaptureName", "Capture names must be unique: " + name);
                    if (properties["attachments"].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Error("CaptureAttachments", "Capture attachments must be Boolean.");
                    action = new(index, actionId, tick, HeadlessScenarioActionKind.Capture,
                        Name: name, Attachments: properties["attachments"].GetBoolean());
                    break;
                }
                case "checkpoint":
                {
                    RequireOnly(properties, ["id", "tick", "kind", "name"], "ActionField");
                    RequireAll(properties, ["name"], "ActionField");
                    string name = RequiredName(properties["name"]);
                    if (!checkpointNames.TryAdd(name, index))
                        throw Error("CheckpointName", "Checkpoint names must be unique: " + name);
                    action = new(index, actionId, tick, HeadlessScenarioActionKind.Checkpoint, Name: name);
                    break;
                }
                case "assert":
                {
                    RequireOnly(properties, ["id", "tick", "kind", "field", "op", "expected", "baseline"], "ActionField");
                    RequireAll(properties, ["field", "op", "expected"], "ActionField");
                    string field = RequiredString(properties["field"], "AssertionField");
                    if (!AssertionFields.TryGetValue(field, out var fieldSpec))
                        throw Error("AssertionField", "Unknown assertion field: " + field);
                    string operation = RequiredString(properties["op"], "AssertionOperator");
                    if (operation is not ("eq" or "gte" or "deltaGte"))
                        throw Error("AssertionOperator", "Unknown assertion operator: " + operation);
                    if (operation == "eq")
                        ValidateEqualityExpected(field, fieldSpec, properties["expected"]);
                    else
                    {
                        if (fieldSpec.Kind != JsonValueKind.Number || !TryFiniteNumber(properties["expected"], out _))
                            throw Error("AssertionExpected", operation + " requires a finite numeric expected value.");
                    }
                    string? baseline = null;
                    if (operation == "deltaGte")
                    {
                        if (!MonotonicTotals.Contains(field))
                            throw Error("AssertionField", "deltaGte is supported only for monotonic session totals.");
                        if (!properties.TryGetValue("baseline", out JsonElement baselineElement))
                            throw Error("AssertionBaseline", "deltaGte requires a baseline checkpoint.");
                        baseline = RequiredString(baselineElement, "AssertionBaseline");
                        if (!checkpointNames.TryGetValue(baseline, out int checkpointIndex) || checkpointIndex >= index)
                            throw Error("AssertionBaseline", "deltaGte baseline must name an earlier checkpoint.");
                    }
                    else if (properties.ContainsKey("baseline"))
                        throw Error("AssertionBaseline", "baseline is allowed only for deltaGte.");
                    if (properties["expected"].ValueKind == JsonValueKind.String)
                        ValidateAssertionChoice(field, properties["expected"].GetString()!);
                    action = new(index, actionId, tick, HeadlessScenarioActionKind.Assert,
                        Field: field, Operator: operation, Expected: properties["expected"].Clone(), Baseline: baseline);
                    break;
                }
                default:
                    throw Error("ActionKind", "Unknown scenario action kind: " + kindText);
            }
            actions.Add(action);
            index++;
        }
        return new HeadlessScenario(path, inputHash, id, actions);
    }

    private static RendererSettings InitialSettings()
    {
        static string Choice(string variable) => Environment.GetEnvironmentVariable(variable)?.Trim() ?? "off";
        return new RendererSettings
        {
            Upscaler = Choice("VULKANSTORY_HEADLESS_UPSCALER"),
            FrameGeneration = Choice("VULKANSTORY_HEADLESS_FRAME_GENERATION"),
            ControllerEnabled = false,
            TouchEnabled = false,
            ShowFpsCounter = true
        }.Normalize();
    }

    private static RendererSettings ValidateSettingsValues(JsonElement values, RendererSettings current)
    {
        Dictionary<string, JsonElement> properties = ObjectProperties(values, "SettingsShape");
        PropertyInfo[] publicProperties = typeof(RendererSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var propertyMap = publicProperties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        JsonObject merged = JsonSerializer.SerializeToNode(current, SettingsJson)?.AsObject()
            ?? throw Error("SettingsShape", "Could not read baseline renderer settings.");
        foreach ((string name, JsonElement value) in properties)
        {
            if (!propertyMap.TryGetValue(name, out PropertyInfo? property))
                throw Error("SettingsProperty", "Unknown RendererSettings property: " + name);
            ValidateSettingValue(name, property.PropertyType, value);
            if ((name == nameof(RendererSettings.ControllerEnabled) || name == nameof(RendererSettings.TouchEnabled)) &&
                value.ValueKind == JsonValueKind.True)
                throw Error("InputProfileRequired", name + "=true requires the deferred child-owned input profile.");
            merged[name] = JsonNode.Parse(value.GetRawText());
        }
        RendererSettings next = merged.Deserialize<RendererSettings>(SettingsJson)
            ?? throw Error("SettingsShape", "Settings action did not produce an object.");
        string nextUpscaler = properties.TryGetValue(nameof(RendererSettings.Upscaler), out JsonElement upscalerValue)
            ? upscalerValue.GetString()! : current.Upscaler;
        if (properties.ContainsKey(nameof(RendererSettings.Upscaler)))
            ValidateChoice(nextUpscaler, ["off", "dlss", "xess", "fsr3", "fsr4"], "Upscaler");
        if (properties.ContainsKey(nameof(RendererSettings.UpscalerQuality)))
            ValidateChoice(properties[nameof(RendererSettings.UpscalerQuality)].GetString()!,
                Canonical(nextUpscaler) == "xess"
                    ? ["dlaa", "ultraquality", "ultraqualityplus", "quality", "balanced", "performance", "ultraperformance"]
                    : ["dlaa", "quality", "balanced", "performance", "ultraperformance"],
                "UpscalerQuality");
        if (properties.ContainsKey(nameof(RendererSettings.FrameGeneration)))
            ValidateChoice(properties[nameof(RendererSettings.FrameGeneration)].GetString()!,
                ["off", "dlss", "fsr3", "xess"], "FrameGeneration");
        if (properties.ContainsKey(nameof(RendererSettings.LowLatencyMode)))
            ValidateChoice(properties[nameof(RendererSettings.LowLatencyMode)].GetString()!,
                ["off", "on", "boost"], "LowLatencyMode");
        if (properties.ContainsKey(nameof(RendererSettings.AmbientOcclusion)))
            ValidateChoice(properties[nameof(RendererSettings.AmbientOcclusion)].GetString()!,
                ["auto", "vanilla", "gtao"], "AmbientOcclusion");
        if (properties.ContainsKey(nameof(RendererSettings.AmbientOcclusionPreset)))
            ValidateChoice(properties[nameof(RendererSettings.AmbientOcclusionPreset)].GetString()!,
                ["low", "medium", "high", "ultra"], "AmbientOcclusionPreset");
        return next.Normalize();
    }

    private static void ValidateSettingValue(string name, Type type, JsonElement value)
    {
        bool valid = type == typeof(bool) ? value.ValueKind is JsonValueKind.True or JsonValueKind.False
            : type == typeof(int) ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)
            : type == typeof(float) ? value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)
            : type == typeof(string) ? value.ValueKind == JsonValueKind.String
            : false;
        if (!valid) throw Error("SettingsType", "RendererSettings." + name + " has the wrong JSON type.");
    }

    private static void ValidateChoice(string value, string[] choices, string property)
    {
        if (!choices.Contains(Canonical(value), StringComparer.Ordinal))
            throw Error("SettingsEnum", "Unknown choice for RendererSettings." + property + ": " + value);
    }

    private static string Canonical(string value) => value.Trim().ToLowerInvariant();

    private static void ValidateEqualityExpected(string field, (JsonValueKind Kind, bool Nullable) fieldSpec, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Null && fieldSpec.Nullable) return;
        bool sameType = fieldSpec.Kind switch
        {
            JsonValueKind.True => expected.ValueKind is JsonValueKind.True or JsonValueKind.False,
            JsonValueKind.String => expected.ValueKind == JsonValueKind.String,
            JsonValueKind.Number => expected.ValueKind == JsonValueKind.Number && TryFiniteNumber(expected, out _),
            _ => false
        };
        if (!sameType) throw Error("AssertionExpected", "eq expected value must match the field's JSON type.");
    }

    private static void ValidateAssertionChoice(string field, string expected)
    {
        string[]? choices = field switch
        {
            "requestedUpscaler" or "effectiveUpscaler" => ["off", "dlss", "xess", "fsr3", "fsr4"],
            "requestedFrameGeneration" or "effectiveFrameGeneration" => ["off", "dlss", "fsr3", "xess"],
            "phase" => ["completedFrame"],
            _ => null
        };
        if (choices != null && !choices.Contains(expected, StringComparer.Ordinal))
            throw Error("AssertionExpected", "Unknown assertion choice for " + field + ": " + expected);
    }

    private static Dictionary<string, JsonElement> ObjectProperties(JsonElement value, string category)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Error(category, "Expected a JSON object.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!properties.TryAdd(property.Name, property.Value))
                throw Error("DuplicateProperty", "Duplicate JSON property: " + property.Name);
        return properties;
    }

    private static void RequireOnly(Dictionary<string, JsonElement> properties, string[] allowed, string category)
    {
        var known = new HashSet<string>(allowed, StringComparer.Ordinal);
        string? unknown = properties.Keys.FirstOrDefault(name => !known.Contains(name));
        if (unknown != null) throw Error(category, "Unknown property: " + unknown);
    }

    private static void RequireAll(Dictionary<string, JsonElement> properties, string[] required, string category)
    {
        string? missing = required.FirstOrDefault(name => !properties.ContainsKey(name));
        if (missing != null) throw Error(category, "Missing property: " + missing);
    }

    private static string RequiredString(JsonElement value, string category)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString()))
            throw Error(category, "Expected a nonempty string.");
        return value.GetString()!;
    }

    private static string RequiredName(JsonElement value)
    {
        string name = RequiredString(value, "CaptureName");
        if (!NamePattern.IsMatch(name)) throw Error("CaptureName", "Name must match [A-Za-z0-9][A-Za-z0-9_-]{0,63}.");
        return name;
    }

    private static bool TryFiniteNumber(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number &&
            double.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
            double.IsFinite(number);
    }

    private static HeadlessScenarioValidationException Error(string category, string message) => new(category, message);
}
