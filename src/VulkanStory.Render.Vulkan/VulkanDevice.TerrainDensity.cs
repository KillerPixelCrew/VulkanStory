using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
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
        string stage = "float-control query";
        try
        {
            // Density is binary64 in the stock game. Gate this optional kernel on the
            // actual enabled feature and queried arithmetic guarantees, independently of vendor.
            PhysicalDeviceFloatControlsProperties controls = new() { SType = StructureType.PhysicalDeviceFloatControlsProperties };
            PhysicalDeviceProperties2 properties = new() { SType = StructureType.PhysicalDeviceProperties2, PNext = &controls };
            _context.Api.GetPhysicalDeviceProperties2(_context.PhysicalDevice, &properties);
            stage = "float-control requirements";
            if (!_context.ShaderFloat64Enabled || !controls.ShaderSignedZeroInfNanPreserveFloat64 ||
                !controls.ShaderDenormPreserveFloat64 || !controls.ShaderRoundingModeRtefloat64)
                throw new NotSupportedException("Stock terrain density requires binary64 RTE, denormal and signed-zero/NaN preservation. " +
                    $"ShaderFloat64Enabled={_context.ShaderFloat64Enabled}, " +
                    $"ShaderSignedZeroInfNanPreserveFloat64={(bool)controls.ShaderSignedZeroInfNanPreserveFloat64}, " +
                    $"ShaderDenormPreserveFloat64={(bool)controls.ShaderDenormPreserveFloat64}, " +
                    $"ShaderRoundingModeRTEFloat64={(bool)controls.ShaderRoundingModeRtefloat64}.");
            stage = "shader source";
            string source = ReadWorldShader("worldgen/terrain-density.comp")
                .Replace("#include \"terrain-noise-kernel.glsl\"", ReadWorldShader("worldgen/terrain-noise-kernel.glsl"), StringComparison.Ordinal)
                .Replace("#include \"terrain-binary32.glsl\"", ReadWorldShader("worldgen/terrain-binary32.glsl"), StringComparison.Ordinal);
            stage = "shader compile";
            var compiled = _shaderCompiler.CompileCompute(source, "terrain-density");
            if (!compiled.Success) throw new InvalidOperationException(compiled.Error);
            stage = "SPIR-V patch";
            byte[] spirv = DensityFloatControls(compiled.Spirv);
            stage = "compute program allocation";
            id = CreateComputeProgram(new ComputeProgramDescription { Name = "terrain-density", Spirv = spirv, PushConstantBytes = 4,
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

    private static byte[] DensityFloatControls(byte[] spirv)
    {
        // SPIR-V 1.5 includes KHR_float_controls. shaderc's GLSL frontend has no
        // portable layout qualifier for its binary64 execution modes: request them
        // explicitly on this single compute entry point after compilation.
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);
        if (words.Length < 5 || words[0] != 0x07230203 || words[1] < 0x00010500)
            throw new InvalidOperationException("Terrain density requires SPIR-V 1.5 or later.");
        var output = new List<uint>(words.Length + 18);
        for (int i = 0; i < 5; i++) output.Add(words[i]);
        bool capabilities = false, entry = false;
        for (int p = 5; p < words.Length;)
        {
            int length = (int)(words[p] >> 16), opcode = (int)(words[p] & 65535);
            if (length == 0 || p + length > words.Length) throw new InvalidOperationException("Malformed density SPIR-V.");
            if (!capabilities && opcode != 17)
            {
                foreach (uint capability in new uint[] { 4464, 4466, 4467 })
                { output.Add((2u << 16) | 17u); output.Add(capability); }
                capabilities = true;
            }
            for (int i = 0; i < length; i++) output.Add(words[p + i]);
            if (opcode == 15)
            {
                if (entry || length < 4 || words[p + 1] != 5) throw new InvalidOperationException("Unexpected density entry point.");
                uint function = words[p + 2];
                foreach (uint mode in new uint[] { 4459, 4461, 4462 })
                { output.Add((4u << 16) | 16u); output.Add(function); output.Add(mode); output.Add(64); }
                entry = true;
            }
            p += length;
        }
        if (!entry) throw new InvalidOperationException("Missing density entry point.");
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(output)).ToArray();
    }
}
