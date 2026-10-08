using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    internal readonly StatedRenderState Stated = new();
    private readonly Dictionary<(int Program, string Sampler), int> programTextures = new();
    private int legacyBoundTexture2D;
    internal void BindLegacyTexture2D(int texture)
    {
        RequireDevice();
        Stated.BindTexture(0, texture);
        legacyBoundTexture2D = texture;
    }
    internal void BindLegacyCube(int texture)
    { RequireDevice(); Stated.BindTexture(0, texture); }
    internal void GenerateLegacyTextureMips()
    {
        var renderer = RequireDevice();
        if (legacyBoundTexture2D != 0) renderer.GenerateMipmaps(legacyBoundTexture2D);
    }

    internal int GenSampler(bool linear) => RequireDevice().CreateSampler(linear);
    internal void SetDebugDepthComparison(int value)
    {
        var renderer = RequireDevice();
        ShaderProgramBase? program = ShaderProgramBase.CurrentShaderProgram;
        if (program is not { PassName: "debugdepthbuffer" })
            throw new InvalidOperationException("Framebuffer debug comparison lost its program.");
        int texture = programTextures.GetValueOrDefault((program.ProgramId, "depthSampler"));
        if (texture <= 0) throw new InvalidOperationException("Framebuffer debug comparison lost its depth texture.");
        renderer.SetTextureParameter(texture, 34892, value);
    }
    internal void BindSampler(int unit, int sampler)
    {
        RequireDevice();
        Stated.BindSampler(unit, sampler);
    }

    /// <summary>Associates an original sampler name/unit with the matching adapter texture and cube/2D binding contract.</summary>
    /// <param name="program">Original program associated with the backend program.</param>
    /// <param name="name">Original declared sampler name.</param>
    /// <param name="texture">Adapter texture identifier.</param>
    /// <param name="unit">Original sampler unit index.</param>
    /// <param name="cube">True for cube-map binding; false for 2D binding.</param>
    internal void BindProgramTexture(ShaderProgramBase program, string name, int texture, int unit, bool cube)
    {
        var renderer = RequireDevice();
        programTextures[(program.ProgramId, name)] = texture;
        renderer.SetSamplerUnit(program.ProgramId, name, unit);
        Stated.BindTexture(unit, texture);
        if (!cube)
            Stated.BindSampler(unit, program.customSamplers.TryGetValue(name, out int sampler) ? sampler : 0);
        if (program.clampTToEdge) renderer.SetTextureParameter(texture, 10243, 33071);
    }
}
