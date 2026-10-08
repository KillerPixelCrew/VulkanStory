using Vintagestory.API.Common;
using Vintagestory.Common;

namespace VulkanStory.Game;

// Asset origins are authoritative at the game's normal load/reload boundary.
// No Optimum launcher-generated scan report is required by the new host.
/// <summary>Tracks patched or non-game shader assets and limits retained stage/include replacement to compatible original sources.</summary>
internal sealed class ShaderOverridePolicy(System.Func<AssetManager?> assets, Action<string> warning)
{
    private readonly HashSet<string> programs = new(StringComparer.OrdinalIgnoreCase);
    private bool known, sharedIncludes;
    private static readonly string[] Writers =
        ["chunkopaque", "chunktopsoil", "entityanimated", "standard", "instanced", "decals", "particlescube"];
    internal bool AllowsRetainedSceneFeatures => known && !sharedIncludes && !Writers.Any(programs.Contains);
    internal bool IsOverridden(string pass) => !known || sharedIncludes ||
        (!string.Equals(pass, "all", StringComparison.OrdinalIgnoreCase) && programs.Contains(pass));
    internal static bool External(IAsset asset) => asset.IsPatched || asset.Origin is not GameOrigin;
    internal void MarkProgram(string pass)
    {
        if (programs.Add(pass)) warning("VulkanStory: shader override uses the rewriter: " + pass);
    }
    internal void MarkIncludes()
    {
        if (!sharedIncludes) warning("VulkanStory: shared shader includes are overridden; native bundle and stock scene motion/AO modes are disabled.");
        sharedIncludes = true;
    }
    /// <summary>Rebuilds shader override knowledge from current game assets, failing closed when discovery cannot establish original sources.</summary>
    internal void Refresh()
    {
        programs.Clear(); sharedIncludes = false; known = false;
        AssetManager? manager = assets();
        if (manager == null || manager.Origins == null || manager.Origins.Count == 0) return;
        try
        {
            var sharedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IAssetOrigin origin in manager.Origins)
                if (origin is GameOrigin)
                    foreach (IAsset asset in origin.GetAssets(AssetCategory.shaderincludes, shouldLoad: false))
                        sharedNames.Add(asset.Name);
            sharedNames.Add("vertexwarp.vsh");
            foreach (IAssetOrigin origin in manager.Origins)
            {
                if (origin is GameOrigin) continue;
                foreach (IAsset asset in origin.GetAssets(AssetCategory.shaders, shouldLoad: false))
                {
                    MarkProgram(Path.GetFileNameWithoutExtension(asset.Location.Path));
                    if (sharedNames.Contains(asset.Name)) MarkIncludes();
                }
                foreach (IAsset asset in origin.GetAssets(AssetCategory.shaderincludes, shouldLoad: false))
                    if (sharedNames.Contains(asset.Name)) MarkIncludes();
            }
            // Also catch patched selected base assets, not just external origins.
            foreach (IAsset asset in manager.GetMany(AssetCategory.shaders, loadAsset: false))
                if (External(asset)) MarkProgram(Path.GetFileNameWithoutExtension(asset.Location.Path));
            foreach (IAsset asset in manager.GetMany(AssetCategory.shaderincludes, loadAsset: false))
                if (External(asset) && sharedNames.Contains(asset.Name)) MarkIncludes();
            known = true;
        }
        catch (Exception error)
        {
            warning("VulkanStory: shader override discovery failed; using the rewriter and disabling stock scene feature publication: " + error.Message);
        }
    }
    /// <summary>Checks whether the named shader stage can use the retained embedded source.</summary>
    /// <param name="pass">Original registered pass name.</param>
    /// <param name="domain">Asset domain requested by the original shader load.</param>
    /// <param name="extension">Shader stage file extension.</param>
    /// <returns>True only for an allowed original stage.</returns>
    internal bool MayReplaceStage(string pass, string? domain, string extension)
    {
        if (!string.IsNullOrEmpty(domain) && !string.Equals(domain, "game", StringComparison.OrdinalIgnoreCase))
        { MarkProgram(pass); return false; }
        IAsset? selected = assets()?.TryGet_BaseAssets(new AssetLocation(domain, "shaders/" + pass + "." + extension));
        if (selected != null && External(selected)) { MarkProgram(pass); return false; }
        return true;
    }
    /// <summary>Checks whether a retained include can replace the current original asset.</summary>
    /// <param name="name">Include asset name.</param>
    /// <returns>True when retained source replacement is permitted.</returns>
    internal bool MayReplaceInclude(string name)
    {
        IAsset? selected = assets()?.TryGet_BaseAssets(new AssetLocation("shaderincludes/" + name));
        if (selected != null && External(selected)) { MarkIncludes(); return false; }
        return true;
    }
}
