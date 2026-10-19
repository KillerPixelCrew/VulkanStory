using VulkanStory.Contracts;
using VulkanStory.Render.Vulkan.Core;

namespace VulkanStory.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    private TerrainNoiseRunner? terrainDensity;
    private bool terrainDensityUnavailable;
    private string terrainDensityAvailabilityReason = "not-prepared";
    internal bool TerrainDensityAvailable => terrainDensity?.Available == true;
    internal string TerrainDensityAvailabilityReason => TerrainDensityAvailable ? "available" : terrainDensityAvailabilityReason;
    internal long TerrainDensityDispatchesRecorded => terrainDensity?.Dispatches ?? 0;
    internal ITerrainDensityBatch? SubmitTerrainDensity(ReadOnlySpan<TerrainDensityColumn> columns,
        ReadOnlySpan<TerrainDensityOctave> octaves, ReadOnlySpan<TerrainDensityRequest> requests) =>
        terrainDensity?.SubmitDensity(columns, octaves, requests);

    private void PrepareTerrainDensity(bool enabled, float[] gradients)
    {
        if (!enabled || terrainDensity != null || terrainDensityUnavailable || _disposed) return;
        int id = 0;
        string stage = "shader source";
        try
        {
            // Binary64 values remain pairs of uint words throughout this kernel.
            // Its arithmetic does not depend on native float64 features or modes.
            string source = ReadWorldShader("worldgen/terrain-density.comp")
                .Replace("#include \"terrain-noise-kernel.glsl\"", ReadWorldShader("worldgen/terrain-noise-kernel.glsl"), StringComparison.Ordinal)
                .Replace("#include \"terrain-binary32.glsl\"", ReadWorldShader("worldgen/terrain-binary32.glsl"), StringComparison.Ordinal)
                .Replace("#include \"terrain-binary64.glsl\"", ReadWorldShader("worldgen/terrain-binary64.glsl"), StringComparison.Ordinal);
            stage = "shader compile";
            var compiled = _shaderCompiler.CompileCompute(source, "terrain-density");
            if (!compiled.Success) throw new InvalidOperationException(compiled.Error);
            stage = "compute program allocation";
            id = CreateComputeProgram(new ComputeProgramDescription { Name = "terrain-density", Spirv = compiled.Spirv, PushConstantBytes = 4,
                Slots = [new(0, ComputeSlotKind.Buffer), new(1, ComputeSlotKind.Buffer), new(2, ComputeSlotKind.Buffer)], LocalSizeX = 64, LocalSizeY = 1 });
            var program = _compute.Get(id)!;
            stage = "pipeline allocation";
            var pipeline = _compute.PipelineFor(program, []);
            stage = "runner allocation";
            terrainDensity = new TerrainNoiseRunner(_context, program, pipeline, gradients, density: true);
        }
        catch (Exception error)
        {
            terrainDensityUnavailable = true;
            terrainDensityAvailabilityReason = $"{stage}: {error}";
            if (id != 0) _compute.Remove(id)?.Dispose();
            RenderLogger?.Warning("VulkanStory: GPU terrain density unavailable: {0}", terrainDensityAvailabilityReason);
        }
    }
}
