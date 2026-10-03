using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VulkanStory.Mod;

internal static class RendererSettingCommand
{
    internal static string Update(string json, string key, string input)
    {
        var draft = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Renderer settings are unavailable.");
        var property = draft.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (property.Key == null || property.Value == null) throw new ArgumentException("Unknown renderer setting: " + key);
        JsonNode? value;
        switch (property.Value.GetValueKind())
        {
            case JsonValueKind.String:
                string choice = input.Trim().ToLowerInvariant();
                string[] allowed = property.Key switch
                {
                    "Upscaler" => ["off", "dlss", "xess", "fsr3", "fsr4"],
                    "FrameGeneration" => ["off", "dlss", "fsr3", "xess"],
                    "UpscalerQuality" => draft["Upscaler"]?.GetValue<string>() == "xess"
                        ? ["dlaa", "ultraquality", "ultraqualityplus", "quality", "balanced", "performance", "ultraperformance"]
                        : ["dlaa", "quality", "balanced", "performance", "ultraperformance"],
                    "LowLatencyMode" => ["off", "on", "boost"],
                    "AmbientOcclusion" => ["auto", "vanilla", "gtao"],
                    "AmbientOcclusionPreset" => ["low", "medium", "high", "ultra"],
                    _ => throw new ArgumentException("This setting has no command choices: " + property.Key),
                };
                if (!allowed.Contains(choice)) throw new ArgumentException("Choose " + string.Join(", ", allowed) + ".");
                value = JsonValue.Create(choice);
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (!bool.TryParse(input, out bool toggle)) throw new ArgumentException("Use true or false.");
                value = JsonValue.Create(toggle);
                break;
            case JsonValueKind.Number:
                if (property.Key is "TaaDebugView" or "FrameGenerationMultiplier")
                {
                    if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ||
                        number < 0 || (property.Key == "FrameGenerationMultiplier" && number is < 2 or > 6))
                        throw new ArgumentException("Invalid integer value for " + property.Key + ".");
                    value = JsonValue.Create(number);
                }
                else
                {
                    if (!float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
                        throw new ArgumentException("Use a finite numeric value.");
                    value = JsonValue.Create(number);
                }
                break;
            default: throw new ArgumentException("Unsupported renderer setting type.");
        }
        draft[property.Key] = value;
        return draft.ToJsonString();
    }
}
