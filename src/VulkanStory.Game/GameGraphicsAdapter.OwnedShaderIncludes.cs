using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private static readonly Regex OwnedIncludeDirective = new(
        @"^[ \t]*#include[ \t]+(?:""(?<name>[^""\r\n]+)""|(?<name>[^\s""<>]+))[ \t]*(?://[^\r\n]*)?\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private IAsset? OwnedShaderAsset(AssetLocation location)
    {
        AssetManager? assets = platform!.AssetManager;
        if (assets == null) return null;
        // The base map holds selected mod assets too. TryGet is unavailable
        // before AssetsLoaded, while our include-free UI already needs to draw.
        return assets.TryGet_BaseAssets(location) ?? (assets.allAssetsLoaded ? assets.TryGet(location) : null);
    }

    /// <summary>Expands one owned stage without invoking the game shader registry or publishing game shader identities.</summary>
    /// <param name="code">The selected stage source.</param>
    /// <param name="programIncludes">Include ownership metadata accumulated across the program's stages.</param>
    /// <returns>Source with game-style and quoted includes expanded from selected assets or permitted owned sources.</returns>
    private string ExpandOwnedShaderIncludes(string code, ISet<string> programIncludes)
    {
        // Each stage needs its own declarations, even when another stage already
        // included the same file. Only the linking metadata is shared.
        var included = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        return Expand(code);

        string Expand(string source) => OwnedIncludeDirective.Replace(source, match =>
        {
            var name = new AssetLocation(match.Groups["name"].Value);
            var location = new AssetLocation(name.Domain, "shaderincludes/" + name.Path);
            string key = location.ToString();
            if (active.Contains(key))
                throw new InvalidOperationException("Owned shader include cycle at '" + name + "'.");
            if (!included.Add(key)) return "";

            IAsset? selected = OwnedShaderAsset(location);
            string? includeCode = null;
            if (name.Domain == "game" && OwnedShaderIncludes.Contains(name.Path) &&
                (name.Path != "vertexwarp.vsh" || selected == null || !ShaderOverridePolicy.External(selected)))
                includeCode = EmbeddedShaderSource(name.Path);
            includeCode ??= selected?.ToText();
            if (includeCode == null)
                throw new InvalidOperationException("Owned shader include is missing: '" + name + "'.");

            programIncludes.Add(name.Domain == "game" ? name.Path : name.ToString());
            active.Add(key);
            try { return Expand(includeCode) + "\n"; }
            finally { active.Remove(key); }
        });
    }
}
