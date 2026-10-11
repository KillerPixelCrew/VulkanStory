using System.Numerics;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using VulkanStory.Contracts;

namespace VulkanStory.Game;

internal sealed partial class GameGraphicsAdapter
{
    private static readonly AccessTools.FieldRef<ClientMain, Dictionary<long, EntityRenderer>> RayEntityRenderers =
        AccessTools.FieldRefAccess<ClientMain, Dictionary<long, EntityRenderer>>("EntityRenderers");
    private static readonly AccessTools.FieldRef<EntityShapeRenderer, MultiTextureMeshRef> RayBody =
        AccessTools.FieldRefAccess<EntityShapeRenderer, MultiTextureMeshRef>("meshRefOpaque");
    private static readonly AccessTools.FieldRef<EntityShapeRenderer, bool> RaySpectator =
        AccessTools.FieldRefAccess<EntityShapeRenderer, bool>("isSpectator");
    private sealed class RayActorPart
    {
        internal ulong Identity;
        internal RayEntityMeshCapture.Template Template = null!;
        internal long Version = -1;
        internal RayTracingVertex[] Vertices = [];
        internal Matrix4x4[] PreviousJoints = [];
        internal RayTracingJoint[] Joints = [];
        internal uint[] Indices = [];
        internal ulong Frame = ulong.MaxValue;
        internal ulong World;
        internal double X, Y, Z;
    }
    private readonly Dictionary<(EntityShapeRenderer Renderer, MeshRef Mesh), RayActorPart> rayActorParts = [];
    private readonly HashSet<(EntityShapeRenderer Renderer, MeshRef Mesh)> rayActorsSeen = [];
    private readonly List<(EntityShapeRenderer Renderer, MeshRef Mesh)> rayActorsRemoved = [];
    private readonly List<int> rayTextureList = [];
    private readonly Dictionary<int, int> rayTexturePages = [];
    private Matrix4x4[] rayJoints = [], rayJointNormals = [];
    private bool[] rayJointValid = [];
    private ulong nextRayActorIdentity, rayActorCaptureFrame = ulong.MaxValue;
    private string rayActorDecline = "";
    internal string RayEntityReadiness => rayActorDecline.Length == 0 ? rayActorParts.Count + " shaped entity mesh parts" : rayActorDecline;

    internal void CaptureRayEntities(ClientMain client, in GameTemporalFrame frame)
    {
        var renderer = RequireDevice();
        if (!renderer.RaySceneCollectionEnabled || frame.FrameId == rayActorCaptureFrame) return;
        rayActorCaptureFrame = frame.FrameId;
        rayTextureList.Clear(); rayTexturePages.Clear(); rayActorsSeen.Clear(); rayActorDecline = "";
        foreach (var atlas in client.api.BlockTextureAtlas.AtlasTextures)
        {
            rayTexturePages.TryAdd(atlas.TextureId, rayTextureList.Count);
            rayTextureList.Add(atlas.TextureId);
        }
        if (client.EntityPlayer?.CameraPos is not { } camera) return;
        ulong world = RayWorldIdentity(client.Api.World);
        foreach (var pair in RayEntityRenderers(client))
        {
            if (pair.Value is not EntityShapeRenderer shape || shape.entity.Pos.Dimension != client.EntityPlayer.Pos.Dimension) continue;
            // Animation was advanced for every loaded renderer by the game's Before stage.
            // Off-screen preparation uses dt=0 and does not advance animation a second time.
            if (!shape.entity.IsRendered)
            {
                shape.BeforeRender(0);
                shape.loadModelMatrix(shape.entity, 0, true);
            }
            if (RaySpectator(shape)) continue;
            MultiTextureMeshRef? body = RayBody(shape);
            var animator = shape.entity.AnimManager?.Animator;
            float[]? bones = animator?.Matrices;
            if (body == null || body.Disposed || bones == null || bones.Length < 16) continue;
            int jointCount = Math.Min(animator!.MaxJointId, bones.Length / 16);
            if (rayJoints.Length < jointCount)
            { rayJoints = new Matrix4x4[jointCount]; rayJointNormals = new Matrix4x4[jointCount]; rayJointValid = new bool[jointCount]; }
            Matrix4x4 model = Matrix(shape.ModelMat);
            bool valid = true;
            for (int i = 0; i < jointCount; i++)
            {
                rayJoints[i] = Matrix(bones.AsSpan(i * 16, 16)) * model;
                rayJointValid[i] = Matrix4x4.Invert(rayJoints[i], out var inverse);
                rayJointNormals[i] = Matrix4x4.Transpose(inverse);
            }
            for (int partIndex = 0; partIndex < body.meshrefs.Length; partIndex++)
            {
                MeshRef mesh = body.meshrefs[partIndex];
                if (mesh.Disposed || partIndex >= body.textureids.Length || !RayEntityMeshCapture.TryGet(mesh, out var template))
                { rayActorDecline = "entity mesh producer unavailable"; continue; }
                MeshData data = template!.Data;
                if (data.xyz == null || data.Indices == null || data.Uv == null || data.mode != EnumDrawMode.Triangles) continue;
                var key = (shape, mesh);
                if (!rayActorParts.TryGetValue(key, out var part))
                {
                    part = new RayActorPart { Identity = (1UL << 63) | ++nextRayActorIdentity, Template = template };
                    rayActorParts.Add(key, part);
                }
                bool reset = frame.Provider.Reset || part.Frame + 1 != frame.FrameId || part.Version != template.Version;
                if (part.Vertices.Length != data.VerticesCount)
                {
                    part.Vertices = new RayTracingVertex[data.VerticesCount];
                    reset = true;
                }
                if (part.Joints.Length != jointCount)
                { part.Joints = new RayTracingJoint[jointCount]; part.PreviousJoints = new Matrix4x4[jointCount]; reset = true; }
                bool geometryChanged = part.Version != template.Version || part.Indices.Length != data.IndicesCount || part.Frame == ulong.MaxValue;
                if (part.Indices.Length != data.IndicesCount) part.Indices = new uint[data.IndicesCount];
                if (geometryChanged) for (int i = 0; i < data.IndicesCount; i++) part.Indices[i] = checked((uint)data.Indices[i]);
                var previousOrigin = new Vector3((float)(part.X - camera.X), (float)(part.Y - camera.Y), (float)(part.Z - camera.Z));
                int stride = Math.Max(1, (data.CustomInts?.InterleaveStride ?? 0) / sizeof(int));
                valid = true;
                for (int i = 0; i < data.VerticesCount; i++)
                {
                    int joint = data.CustomInts?.Values is { } ids && i * stride < ids.Length ? ids[i * stride] : 0;
                    if (joint < 0 || joint >= jointCount || !rayJointValid[joint]) { valid = false; break; }
                    if (!geometryChanged) continue;
                    int flags = data.Flags is { } values && i < values.Length ? values[i] : 0;
                    int packed = data.Normals is { } normals && i < normals.Length ? normals[i] : 0;
                    int rgba = i * 4;
                    uint colour = data.Rgba is { } colors && rgba + 3 < colors.Length
                        ? (uint)(colors[rgba] | colors[rgba + 1] << 8 | colors[rgba + 2] << 16 | colors[rgba + 3] << 24) : uint.MaxValue;
                    part.Vertices[i] = new RayTracingVertex
                    {
                        Position = new Vector3(data.xyz[i * 3], data.xyz[i * 3 + 1], data.xyz[i * 3 + 2]),
                        Flags = (uint)flags, PackedNormal = (uint)packed, Color = colour, JointIndex = (uint)joint,
                        Uv = new Vector2(data.Uv[i * 2], data.Uv[i * 2 + 1]),
                    };
                }
                if (!valid) { rayActorDecline = "entity joint index exceeds its pose"; continue; }
                int texture = body.textureids[partIndex];
                if (!rayTexturePages.TryGetValue(texture, out int page))
                { page = rayTextureList.Count; rayTexturePages.Add(texture, page); rayTextureList.Add(texture); }
                for (int i = 0; i < jointCount; i++) part.Joints[i] = new RayTracingJoint
                {
                    Current = rayJoints[i], Previous = reset ? rayJoints[i] : part.PreviousJoints[i], Normal = rayJointNormals[i],
                };
                var geometry = new RayTracingGeometryUpload(world, part.Vertices, part.Indices,
                    camera.X, camera.Y, camera.Z, shape.entity.Pos.Dimension, page, RayTracingSurface.Cutout, 0,
                    ReferenceEquals(shape.entity, client.EntityPlayer) && client.Player.CameraMode == EnumCameraMode.FirstPerson
                        ? (byte)0x80 : (byte)0xff);
                renderer.SetSkinnedRayGeometry(part.Identity,
                    new RayTracingSkinUpload(geometry, part.Joints, template.Version, previousOrigin, reset));
                Array.Copy(rayJoints, part.PreviousJoints, jointCount);
                part.X = camera.X; part.Y = camera.Y; part.Z = camera.Z; part.Frame = frame.FrameId; part.Version = template.Version;
                part.World = world;
                rayActorsSeen.Add(key);
            }
        }
        rayActorsRemoved.Clear();
        foreach (var pair in rayActorParts) if (!rayActorsSeen.Contains(pair.Key)) rayActorsRemoved.Add(pair.Key);
        foreach (var removed in rayActorsRemoved)
        { renderer.RemoveRayGeometry(rayActorParts[removed].Identity); rayActorParts.Remove(removed); }
        if (rayAtlases.Length != rayTextureList.Count) rayAtlases = new int[rayTextureList.Count];
        rayTextureList.CopyTo(rayAtlases);
    }

    private void RemoveRayActors(ulong world)
    {
        rayActorsRemoved.Clear();
        foreach (var pair in rayActorParts) if (pair.Value.World == world) rayActorsRemoved.Add(pair.Key);
        foreach (var key in rayActorsRemoved) rayActorParts.Remove(key);
        rayActorsRemoved.Clear();
    }

    internal void ResetRayActors()
    {
        rayActorParts.Clear(); rayActorsSeen.Clear(); rayActorsRemoved.Clear();
        rayTextureList.Clear(); rayTexturePages.Clear(); rayAtlases = [];
        rayActorCaptureFrame = ulong.MaxValue; rayActorDecline = "";
    }

    // Column-major game transforms are transposed into System.Numerics' row-vector convention.
    private static Matrix4x4 Matrix(ReadOnlySpan<float> m) => new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
        m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
}
