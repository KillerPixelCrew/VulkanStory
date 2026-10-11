using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace VulkanStory.Game;

internal static class RayEntityMeshCapture
{
    internal sealed class Template(MeshData data)
    {
        internal MeshData Data = data;
        internal long Version;
    }
    private static readonly ConditionalWeakTable<MeshRef, Template> templates = new();
    private static ProcessRuntime? runtime;
    [ThreadStatic] private static int captureDepth;
    internal static bool Capturing => captureDepth != 0;
    private static readonly MethodInfo Ready = AccessTools.Method(typeof(EntityShapeRenderer), "onMeshReady", [typeof(MeshData)])!;
    private static readonly MethodInfo Animate = AccessTools.Method(typeof(AnimationManager), nameof(AnimationManager.OnClientFrame), [typeof(float)])!;
    private static readonly AccessTools.FieldRef<AnimationManager, Entity> AnimatedEntity =
        AccessTools.FieldRefAccess<AnimationManager, Entity>("entity");
    private static readonly AccessTools.FieldRef<AnimationManager, ICoreClientAPI> AnimatedApi =
        AccessTools.FieldRefAccess<AnimationManager, ICoreClientAPI>("capi");
    internal static void Validate()
    {
        if (Ready == null || Ready.ReturnType != typeof(void) || Ready.GetMethodBody() == null)
            throw new MissingMethodException("Original entity mesh-ready callback changed.");
        if (Animate == null || Animate.ReturnType != typeof(void) || Animate.GetMethodBody() == null)
            throw new MissingMethodException("Original entity animation callback changed.");
    }
    internal static void Install(Harmony harmony, ProcessRuntime owner)
    {
        runtime = owner;
        harmony.Patch(Ready, prefix: new HarmonyMethod(typeof(RayEntityMeshCapture), nameof(Begin)),
            finalizer: new HarmonyMethod(typeof(RayEntityMeshCapture), nameof(End)));
        harmony.Patch(Animate, postfix: new HarmonyMethod(typeof(RayEntityMeshCapture), nameof(AfterAnimate)));
    }
    internal static void Detach(ProcessRuntime owner) { if (ReferenceEquals(runtime, owner)) runtime = null; }
    private static void AfterAnimate(AnimationManager __instance, float dt)
    {
        if (runtime?.Routing.RoutingEnabled != true || __instance.Animator == null) return;
        Entity? entity = AnimatedEntity(__instance);
        ICoreClientAPI? api = AnimatedApi(__instance);
        if (entity == null || api == null || entity.IsRendered || entity.IsShadowRendered || !entity.Alive ||
            (!__instance.RunWhilePaused && api.IsGamePaused) ||
            ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session) ||
            !session.Graphics.RaySceneCollectionEnabled || !ReferenceEquals(entity.World, api.World) ||
            entity.Pos.Dimension != api.World.Player?.Entity?.Pos.Dimension) return;
        bool calculate = __instance.Animator.CalculateMatrices;
        try
        {
            __instance.Animator.CalculateMatrices = true;
            __instance.Animator.OnFrame(__instance.ActiveAnimationsByAnimCode, dt);
        }
        finally { __instance.Animator.CalculateMatrices = calculate; }
    }
    private static void Begin(out int __state) { __state = captureDepth; captureDepth++; }
    private static Exception? End(Exception? __exception, int __state) { captureDepth = __state; return __exception; }
    internal static void Remember(MeshRef mesh, MeshData data) => templates.Add(mesh, new Template(data.Clone()));
    internal static bool TryGet(MeshRef mesh, out Template? template) => templates.TryGetValue(mesh, out template);
    internal static void Update(MeshRef mesh, MeshData data)
    {
        if (!templates.TryGetValue(mesh, out var template)) return;
        // Entity callback meshes are complete uploads. Preserve untouched streams
        // when a later update supplies only a subset of their attributes.
        MeshData target = template.Data;
        static void Copy<T>(T[]? source, T[]? destination, int offset, int count)
        {
            if (source == null || destination == null) return;
            if (offset < 0 || count < 0 || count > source.Length || offset + count > destination.Length)
                throw new ArgumentException("Entity mesh update exceeds its retained stream.");
            Array.Copy(source, 0, destination, offset, count);
        }
        Copy(data.xyz, target.xyz, data.XyzOffset / sizeof(float), data.VerticesCount * 3);
        Copy(data.Normals, target.Normals, data.NormalsOffset / sizeof(int), data.VerticesCount);
        Copy(data.Uv, target.Uv, data.UvOffset / sizeof(float), data.VerticesCount * 2);
        Copy(data.Rgba, target.Rgba, data.RgbaOffset, data.VerticesCount * 4);
        Copy(data.Flags, target.Flags, data.FlagsOffset / sizeof(int), data.VerticesCount);
        Copy(data.Indices, target.Indices, data.IndicesOffset / sizeof(int), data.IndicesCount);
        if (data.CustomInts != null && target.CustomInts != null)
            Copy(data.CustomInts.Values, target.CustomInts.Values, data.CustomInts.BaseOffset / sizeof(int), data.CustomInts.Count);
        template.Version++;
    }
}
