using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using VulkanStory.Settings;

namespace VulkanStory.Mod;

/// <summary>Typed chat-command edits to the existing renderer settings JSON object.</summary>
internal static class RendererSettingCommand
{
    /// <summary>Updates one existing setting by case-insensitive key, validating supported choices and numeric/boolean syntax.</summary>
    /// <param name="json">Current settings serialized as an object.</param>
    /// <param name="key">Existing property name to change.</param>
    /// <param name="input">User text parsed with invariant numeric culture.</param>
    /// <returns>Edited JSON for the runtime's apply/persist callback; this method itself does not apply settings.</returns>
    /// <exception cref="ArgumentException">The setting, choice, value, or JSON value type is unsupported.</exception>
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
                string[] allowed = RendererChoices.Get(property.Key, draft["Upscaler"]?.GetValue<string>() == "xess").Select(item => item.Value).ToArray();
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
