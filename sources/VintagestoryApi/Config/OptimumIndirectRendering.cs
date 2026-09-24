using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #75: Memory layout for an OpenGL 4.3 DrawElementsIndirectCommand struct (20 bytes).
/// Used by glMultiDrawElementsIndirect to dispatch chunk mesh batches.
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct DrawElementsIndirectCommand
{
    public uint Count;
    public uint InstanceCount;
    public uint FirstIndex;
    public int BaseVertex;
    public uint BaseInstance;

    public const int SizeInBytes = 20;

    public DrawElementsIndirectCommand(uint count, uint instanceCount, uint firstIndex, int baseVertex, uint baseInstance)
    {
        Count = count;
        InstanceCount = instanceCount;
        FirstIndex = firstIndex;
        BaseVertex = baseVertex;
        BaseInstance = baseInstance;
    }
}

/// <summary>
/// Issue #75: Helper routines for GPU indirect rendering capability detection,
/// command translation, and safe fallback.
/// </summary>
public static class OptimumIndirectRendering
{
    public static int BuildCommands(
        int[] indicesStartsByte,
        int[] indicesSizes,
        int groupCount,
        DrawElementsIndirectCommand[] targetArray)
    {
        if (indicesStartsByte == null || indicesSizes == null || targetArray == null)
        {
            return 0;
        }

        int count = Math.Min(groupCount, Math.Min(indicesSizes.Length, targetArray.Length));
        for (int i = 0; i < count; i++)
        {
            uint firstIndex = (uint)(indicesStartsByte[i * 2] / 4);
            uint indexCount = (uint)Math.Max(0, indicesSizes[i]);
            targetArray[i] = new DrawElementsIndirectCommand(indexCount, 1u, firstIndex, 0, 0u);
        }

        return count;
    }

    public static bool ValidateCommand(in DrawElementsIndirectCommand cmd, int maxIndicesCount)
    {
        if (cmd.InstanceCount == 0 || cmd.Count == 0) return false;
        if (cmd.FirstIndex + cmd.Count > (uint)maxIndicesCount) return false;
        return true;
    }
}
