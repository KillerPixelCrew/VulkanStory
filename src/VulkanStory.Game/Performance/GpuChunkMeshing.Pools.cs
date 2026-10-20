using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game.Performance;

internal static partial class GpuChunkMeshing
{
    private static readonly MethodInfo Insert = AccessTools.Method(typeof(MeshDataPool), "InsertAt",
        [typeof(ICoreClientAPI), typeof(MeshData), typeof(Vec3i), typeof(Sphere), typeof(int), typeof(int), typeof(int)]);
    private static readonly AccessTools.FieldRef<MeshDataPool, MeshRef> PoolMesh = AccessTools.FieldRefAccess<MeshDataPool, MeshRef>("modelRef");
    private static readonly AccessTools.FieldRef<MeshDataPool, int> PoolId = AccessTools.FieldRefAccess<MeshDataPool, int>("poolId");
    private static readonly AccessTools.FieldRef<MeshDataPool, Vec3i> Origin = AccessTools.FieldRefAccess<MeshDataPool, Vec3i>("poolOrigin");
    private static readonly AccessTools.FieldRef<MeshDataPool, List<ModelDataPoolLocation>> Locations =
        AccessTools.FieldRefAccess<MeshDataPool, List<ModelDataPoolLocation>>("poolLocations");

    private static void ValidatePool()
    {
        if (Insert.GetParameters().Length != 7 || PartAtlas.FieldType != typeof(int) || PartPass.FieldType != typeof(EnumChunkRenderPass) ||
            PartLod.FieldType != typeof(MeshData)) throw new InvalidOperationException("Chunk pool reservation layout changed.");
    }

    // TryAdd/TrySqueeze/TryAppend retain their original admission, fragmentation,
    // capacity and cursor bookkeeping. Only InsertAt's CPU geometry loops and
    // upload are replaced for our count-only snapshots.
    private static bool Reserve(MeshDataPool __instance, MeshData modeldata, Vec3i modelOrigin, Sphere frustumCullSphere,
        int indexPosition, int vertexPosition, int listPosition, ref ModelDataPoolLocation __result)
    {
        if (!Payloads.TryGetValue(modeldata, out var payload)) return true;
        lock (Gate)
        {
            if (payload.IsDisposed) throw new InvalidOperationException("Retired chunk payload reached pool publication.");
            Vec3i origin = Origin(__instance);
            var vao = (VAO)PoolMesh(__instance);
            try
            {
                GameGraphicsAdapter.MeshAdapter(vao).PublishChunkMeshing(vao, payload.Batch, payload.Range, vertexPosition,
                    origin == null ? 0 : modelOrigin.X - origin.X,
                    origin == null ? 0 : modelOrigin.Y - origin.Y,
                    origin == null ? 0 : modelOrigin.Z - origin.Z);
            }
            catch (Exception error)
            {
                if (payload.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(payload.ProbeBatch, "pool-publication-failed",
                    new { payload.Range, pool = PoolId(__instance), error = error.Message });
                throw;
            }
            if (payload.ProbeBatch != 0) HeadlessChunkMeshingProbe.Event(payload.ProbeBatch, "pool-publication-recorded",
                new { payload.Range, pool = PoolId(__instance), vertexPosition, indexPosition,
                    x = modelOrigin.X, y = modelOrigin.Y, z = modelOrigin.Z, gpuExecutionProven = false });
            // The submitted reader now owns the geometry through frame retirement.
            // Stock disposal still consumes the CWT marker, without recycling arrays.
            payload.Dispose("published");
            var location = new ModelDataPoolLocation { IndicesStart = indexPosition, IndicesEnd = indexPosition + modeldata.IndicesCount,
                VerticesStart = vertexPosition, VerticesEnd = vertexPosition + modeldata.VerticesCount,
                PoolId = PoolId(__instance), FrustumCullSphere = frustumCullSphere };
            var locations = Locations(__instance);
            if (listPosition == -1) locations.Add(location); else locations.Insert(listPosition, location);
            __instance.CalcFragmentation();
            __result = location;
            return false;
        }
    }
}
