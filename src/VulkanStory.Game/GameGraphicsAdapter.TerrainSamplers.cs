using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private readonly Dictionary<int, float> terrainTextureBias = new();
    private readonly Dictionary<int, float> terrainSamplerBias = new();
    internal bool TerrainShadersReady => !sceneShaderLoading &&
        ShaderPrograms.Chunkopaque is { Disposed: false, LoadError: false, ProgramId: > 0 } &&
        ShaderPrograms.Chunktopsoil is { Disposed: false, LoadError: false, ProgramId: > 0 };
    /// <summary>Applies the requested reconstruction mip bias to the supplied nonzero terrain texture identifiers.</summary>
    /// <param name="textures">Terrain texture identifiers; zero entries are ignored.</param>
    /// <param name="bias">Mip LOD bias selected by the current reconstruction policy.</param>
    internal void ApplyTerrainLodBias(int[] textures, float bias)
    {
        var renderer = RequireDevice();
        if (!float.IsFinite(bias)) throw new ArgumentOutOfRangeException(nameof(bias));
        foreach (int texture in textures)
        {
            if (texture <= 0 || (!terrainTextureBias.TryGetValue(texture, out float previous) && bias == 0f) ||
                (terrainTextureBias.ContainsKey(texture) && Math.Abs(previous - bias) < .0001f)) continue;
            renderer.SetTextureParameter(texture, 34049, bias);
            terrainTextureBias[texture] = bias;
        }
        foreach (ShaderProgramBase? program in new ShaderProgramBase?[] { ShaderPrograms.Chunkopaque, ShaderPrograms.Chunktopsoil })
        {
            if (program == null || program.Disposed) continue;
            foreach (string name in new[] { "terrainTex", "terrainTexLinear" })
            {
                if (!program.customSamplers.TryGetValue(name, out int sampler) ||
                    (!terrainSamplerBias.TryGetValue(sampler, out float previous) && bias == 0f) ||
                    (terrainSamplerBias.ContainsKey(sampler) && Math.Abs(previous - bias) < .0001f)) continue;
                renderer.SetSamplerParameter(sampler, 34049, bias);
                terrainSamplerBias[sampler] = bias;
            }
        }
    }
}
