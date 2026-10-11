using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace VulkanStory.Game.Performance;

/// <summary>Reads previously visible entities' lighting in chunk-sized batches under the original pack/unpack lock.
/// Newly visible entities, moved sample positions, custom block accessors and disposed chunks use the original lookup.</summary>
internal static class EntityLightBatch
{
    private readonly record struct Request(long Chunk, int X, int Y, int Z);
    private readonly record struct Light(ClientChunk Chunk, float R, float G, float B, float Sun);
    private sealed class Batch
    {
        internal readonly List<Request> Requests = new();
        internal readonly Dictionary<(int X, int Y, int Z), Light> Lights = new();
        internal IBlockAccessor? Accessor;
    }
    private static ConditionalWeakTable<SystemRenderEntities, Batch> batches = new();
    [ThreadStatic] private static Batch? current;
    private static readonly AccessTools.FieldRef<ClientSystem, ClientMain> Game = AccessTools.FieldRefAccess<ClientSystem, ClientMain>("game");
    private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> Renderers =
        AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");
    private delegate ushort ReadPackedLight(ChunkData data, int index, out int saturation);
    private static readonly ReadPackedLight ReadPacked = (AccessTools.Method(typeof(ChunkData), "ReadLight",
        [typeof(int), typeof(int).MakeByRefType()]) ?? throw new MissingMethodException(typeof(ChunkData).FullName, "ReadLight(int,out int)"))
        .CreateDelegate<ReadPackedLight>();
    private static readonly AccessTools.FieldRef<WorldChunk, object> ChunkGate = AccessTools.FieldRefAccess<WorldChunk, object>("packUnpackLock");
    private static readonly AccessTools.FieldRef<WorldChunk, ChunkData> Data = AccessTools.FieldRefAccess<WorldChunk, ChunkData>("chunkdata");
    private static readonly MethodInfo Lookup = AccessTools.Method(typeof(IBlockAccessor), "GetLightRGBs", [typeof(int), typeof(int), typeof(int)])!;
    private static MethodInfo Consumer() => AccessTools.Method(typeof(EntityShapeRenderer), "BeforeRender", [typeof(float)])!;
    private static MethodInfo Producer() => AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)])!;
    internal static PerformanceFeature Create() => new("entity-light-batch", s => s.EntityLightBatch, () =>
    {
        if (PatchProcessor.GetOriginalInstructions(Consumer()).Count(i => i.Calls(Lookup)) != 2)
            throw new InvalidOperationException("Entity light sampling anchors changed.");
        _ = Producer();
    }, harmony =>
    {
        harmony.Patch(Producer(), prefix: new HarmonyMethod(typeof(EntityLightBatch), nameof(Prepare)),
            finalizer: new HarmonyMethod(typeof(EntityLightBatch), nameof(Finish)));
        harmony.Patch(Consumer(), transpiler: new HarmonyMethod(typeof(EntityLightBatch), nameof(Transpiler)));
    }, WorldLeft: () => { batches = new(); current = null; });

    private static void Prepare(SystemRenderEntities __instance, out Batch? __state)
    {
        __state = current; current = null;
        if (PerformancePatchSet.Current?.EntityLightBatch != true) return;
        ClientMain game = Game(__instance);
        Batch batch = batches.GetOrCreateValue(__instance);
        batch.Requests.Clear(); batch.Lights.Clear();
        batch.Accessor = game.BlockAccessor;
        ClientWorldMap map = game.WorldMap;
        foreach (var pair in Renderers(game))
        {
            EntityRenderer renderer = pair.Value;
            if (!renderer.entity.IsRendered || renderer is not EntityShapeRenderer) continue;
            Entity entity = renderer.entity;
            int x = (int)(entity.Pos.X + entity.SelectionBox.X1 - entity.OriginSelectionBox.X1);
            int y = (int)entity.Pos.InternalY;
            int z = (int)(entity.Pos.Z + entity.SelectionBox.Z1 - entity.OriginSelectionBox.Z1);
            Add(x, y, z);
            if (entity.SelectionBox.Y2 > 1) Add(x, y + 1, z);
        }
        batch.Requests.Sort(static (a, b) => a.Chunk.CompareTo(b.Chunk));
        int offset = 0;
        while (offset < batch.Requests.Count)
        {
            long key = batch.Requests[offset].Chunk;
            int end = offset + 1;
            while (end < batch.Requests.Count && batch.Requests[end].Chunk == key) end++;
            if (map.GetChunk(key) is ClientChunk chunk && !chunk.Disposed)
                lock (ChunkGate(chunk))
                {
                    if (!chunk.Disposed)
                    {
                        chunk.Unpack_ReadOnly();
                        ChunkData data = Data(chunk);
                        if (data != null)
                            for (int i = offset; i < end; i++)
                            {
                                Request request = batch.Requests[i];
                                int index = MapUtil.Index3d(request.X & 31, request.Y & 31, request.Z & 31, 32, 32);
                                ushort light = ReadPacked(data, index, out int saturation);
                                int rgb = ColorUtil.HsvToRgb(map.hueLevels[light >> 10], map.satLevels[saturation],
                                    (int)(map.BlockLightLevels[(light >> 5) & 31] * 255));
                                batch.Lights[(request.X, request.Y, request.Z)] = new(chunk, (rgb >> 16) / 255f,
                                    ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, map.SunLightLevels[light & 31]);
                            }
                    }
                }
            offset = end;
        }
        current = batch;
        void Add(int x, int y, int z)
        {
            if (map.IsValidPos(x, y, z)) batch.Requests.Add(new(map.ChunkIndex3D(x / 32, y / 32, z / 32), x, y, z));
        }
    }
    private static void Finish(Batch? __state)
    {
        // Release chunk references before world unload; capacity remains available for the next frame.
        current?.Lights.Clear();
        current = __state;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(Lookup))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(EntityLightBatch), nameof(Read));
            }
            yield return instruction;
        }
    }
    private static Vec4f Read(IBlockAccessor accessor, int x, int y, int z)
    {
        if (PerformancePatchSet.Current?.EntityLightBatch == true && current is { } batch && ReferenceEquals(accessor, batch.Accessor) &&
            batch.Lights.TryGetValue((x, y, z), out Light light) && !light.Chunk.Disposed)
            return new Vec4f(light.R, light.G, light.B, light.Sun);
        return accessor.GetLightRGBs(x, y, z);
    }
}
