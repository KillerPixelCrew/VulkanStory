using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class IndirectDrawCoverageTests
{
    [Fact]
    public void DrawElementsIndirectCommand_StructLayoutMatchesOpenGLSpecification()
    {
        // Issue #75: OpenGL 4.3+ ARB_multi_draw_indirect requires exactly 20 bytes:
        // struct DrawElementsIndirectCommand {
        //     uint  count;
        //     uint  instanceCount;
        //     uint  firstIndex;
        //     int   baseVertex;
        //     uint  baseInstance;
        // };
        Assert.Equal(20, Marshal.SizeOf<DrawElementsIndirectCommand>());
        Assert.Equal(20, DrawElementsIndirectCommand.SizeInBytes);

        Assert.Equal(0, Marshal.OffsetOf<DrawElementsIndirectCommand>(nameof(DrawElementsIndirectCommand.Count)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<DrawElementsIndirectCommand>(nameof(DrawElementsIndirectCommand.InstanceCount)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<DrawElementsIndirectCommand>(nameof(DrawElementsIndirectCommand.FirstIndex)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<DrawElementsIndirectCommand>(nameof(DrawElementsIndirectCommand.BaseVertex)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<DrawElementsIndirectCommand>(nameof(DrawElementsIndirectCommand.BaseInstance)).ToInt32());
    }

    [Fact]
    public void DrawElementsIndirectCommand_ConstructorSetsAllFields()
    {
        var cmd = new DrawElementsIndirectCommand(128, 1, 64, 0, 0);
        Assert.Equal(128u, cmd.Count);
        Assert.Equal(1u, cmd.InstanceCount);
        Assert.Equal(64u, cmd.FirstIndex);
        Assert.Equal(0, cmd.BaseVertex);
        Assert.Equal(0u, cmd.BaseInstance);
    }

    [Fact]
    public void OptimumIndirectRendering_BuildCommands_ConvertsByteOffsetsToIndexOffsets()
    {
        // In Vintage Story, indicesStartsByte contains byte offsets (4 bytes per uint32 index)
        int[] startsByte = [0, 0, 256, 0, 1024, 0];
        int[] sizes = [60, 120, 300];
        var commands = new DrawElementsIndirectCommand[3];

        int count = OptimumIndirectRendering.BuildCommands(startsByte, sizes, 3, commands);
        Assert.Equal(3, count);

        Assert.Equal(60u, commands[0].Count);
        Assert.Equal(0u, commands[0].FirstIndex);
        Assert.Equal(1u, commands[0].InstanceCount);

        Assert.Equal(120u, commands[1].Count);
        Assert.Equal(64u, commands[1].FirstIndex); // 256 / 4
        Assert.Equal(1u, commands[1].InstanceCount);

        Assert.Equal(300u, commands[2].Count);
        Assert.Equal(256u, commands[2].FirstIndex); // 1024 / 4
        Assert.Equal(1u, commands[2].InstanceCount);
    }

    [Fact]
    public void OptimumIndirectRendering_BuildCommands_HandlesNullOrPartialInputsSafely()
    {
        var target = new DrawElementsIndirectCommand[2];
        Assert.Equal(0, OptimumIndirectRendering.BuildCommands(null, [10], 1, target));
        Assert.Equal(0, OptimumIndirectRendering.BuildCommands([0], null, 1, target));
        Assert.Equal(0, OptimumIndirectRendering.BuildCommands([0], [10], 1, null));

        int[] startsByte = [0, 0];
        int[] sizes = [10];
        Assert.Equal(1, OptimumIndirectRendering.BuildCommands(startsByte, sizes, 5, target));
    }

    [Fact]
    public void OptimumIndirectRendering_ValidateCommand_ValidatesBoundsAndInstances()
    {
        var validCmd = new DrawElementsIndirectCommand(100, 1, 50, 0, 0);
        Assert.True(OptimumIndirectRendering.ValidateCommand(validCmd, 200));

        var zeroInstanceCmd = new DrawElementsIndirectCommand(100, 0, 50, 0, 0);
        Assert.False(OptimumIndirectRendering.ValidateCommand(zeroInstanceCmd, 200));

        var zeroCountCmd = new DrawElementsIndirectCommand(0, 1, 50, 0, 0);
        Assert.False(OptimumIndirectRendering.ValidateCommand(zeroCountCmd, 200));

        var outOfBoundsCmd = new DrawElementsIndirectCommand(151, 1, 50, 0, 0);
        Assert.False(OptimumIndirectRendering.ValidateCommand(outOfBoundsCmd, 200));
    }

    [Fact]
    public void OptimumConfig_IndirectDrawGating_RequiresBothSupportAndEnablement()
    {
        bool origEnabled = OptimumConfig.IndirectDrawEnabled;
        bool origSupported = OptimumConfig.IndirectDrawSupported;

        try
        {
            // Default capability support depends on platform detection, but when unsupported (e.g. macOS OpenGL 4.1):
            OptimumConfig.IndirectDrawSupported = false;
            OptimumConfig.IndirectDrawEnabled = true;
            Assert.False(OptimumConfig.EffectiveIndirectDraw, "EffectiveIndirectDraw must be false when hardware/driver does not support OpenGL 4.3 MDI");

            // When supported but disabled:
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = false;
            Assert.False(OptimumConfig.EffectiveIndirectDraw, "EffectiveIndirectDraw must be false when disabled by config");

            // When both supported and enabled:
            OptimumConfig.IndirectDrawSupported = true;
            OptimumConfig.IndirectDrawEnabled = true;
            Assert.True(OptimumConfig.EffectiveIndirectDraw, "EffectiveIndirectDraw must be true when supported and enabled");
        }
        finally
        {
            OptimumConfig.IndirectDrawEnabled = origEnabled;
            OptimumConfig.IndirectDrawSupported = origSupported;
        }
    }

    [Fact]
    public void OptimumDiagnostics_ChunkDrawSubmission_RecordsMetricsProperly()
    {
        OptimumDiagnostics.ResetChunkDrawSubmissionCounters();
        try
        {
            Assert.Equal(0, OptimumDiagnostics.ChunkIndirectDrawCalls);
            Assert.Equal(0, OptimumDiagnostics.ChunkConventionalDrawCalls);

            // Record 2 conventional batches
            OptimumDiagnostics.RecordChunkDrawSubmission(5, false, 5000);
            OptimumDiagnostics.RecordChunkDrawSubmission(3, false, 3000);

            // Record 1 indirect batch
            OptimumDiagnostics.RecordChunkDrawSubmission(10, true, 2000);

            Assert.Equal(1, OptimumDiagnostics.ChunkIndirectDrawCalls);
            Assert.Equal(2, OptimumDiagnostics.ChunkConventionalDrawCalls);
            Assert.Equal(10, OptimumDiagnostics.ChunkIndirectGroups);
            Assert.Equal(8, OptimumDiagnostics.ChunkConventionalGroups);
            Assert.Equal(2000, OptimumDiagnostics.ChunkIndirectSubmissionTicks);
            Assert.Equal(8000, OptimumDiagnostics.ChunkConventionalSubmissionTicks);

            string summary = OptimumDiagnostics.GetChunkRenderSummary();
            Assert.Contains("submissionMs/frame", summary);
            Assert.Contains("indirectDraws/frame", summary);
        }
        finally
        {
            OptimumDiagnostics.ResetChunkDrawSubmissionCounters();
        }
    }
}
