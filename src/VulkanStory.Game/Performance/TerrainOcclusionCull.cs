using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VulkanStory.Contracts;

namespace VulkanStory.Game.Performance;

/// <summary>PERF-03: terrain chunk parts hidden behind already-drawn opaque terrain are left out of the main camera's draws.</summary>
/// <remarks>
/// <para>The game decides per chunk part, on the CPU, whether to draw it (<c>ModelDataPoolLocation.IsVisible</c>): culler visibility
/// plus the camera frustum. This adds one more test for the main camera (<c>CullNormal</c>) only: the part's bounding box against
/// the renderer's occlusion depth, a max-depth pyramid of the opaque terrain from a recent frame. A part rejected here never
/// becomes a draw, so it costs neither submission nor GPU time. Shadow passes and mini-dimension pools use other paths and are
/// untouched.</para>
/// <para>The depth is a frame or two old. The box is grown by the camera's movement since that depth was captured, so geometry
/// uncovered by moving is drawn again before it can be missed; boxes outside the captured view or crossing its near plane are
/// always drawn.</para>
/// </remarks>
internal static class TerrainOcclusionCull
{
    private const double Sqrt3 = 1.7320508075688772;
    private const double BoxPadding = 0.5;
    private static volatile Func<IOcclusionTest?>? source;
    private static Func<(double X, double Y, double Z)?>? camera;
    private static Action? capture, reset;
    [ThreadStatic] private static CullSnapshot? cullSnapshot;
    private readonly record struct CullSnapshot(IOcclusionTest? Test, OcclusionCamera? Camera);

    /// <summary>The active eye's completed occlusion test, or the desktop test in mono; null without a session.</summary>
    internal static IOcclusionTest? Source => source?.Invoke();

    /// <summary>The current camera position, or null without a world.</summary>
    internal static (double X, double Y, double Z)? CameraNow() => camera?.Invoke();
    internal static void InvalidateDepth() => reset?.Invoke();

    /// <summary>The session's occlusion source, current camera position, depth capture and reset; nulls when no session renders.</summary>
    internal static void Attach(Func<IOcclusionTest?>? test, Func<(double X, double Y, double Z)?>? currentCamera, Action? captureDepth, Action? resetDepth)
    { source = test; camera = currentCamera; capture = captureDepth; reset = resetDepth; }

    private static MethodInfo Target() => AccessTools.Method(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.IsVisible),
        [typeof(EnumFrustumCullMode), typeof(FrustumCulling)]) ??
        throw new MissingMethodException(typeof(ModelDataPoolLocation).FullName, nameof(ModelDataPoolLocation.IsVisible));

    private static MethodInfo CullPool() => AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull),
        [typeof(FrustumCulling), typeof(EnumFrustumCullMode)]) ??
        throw new MissingMethodException(typeof(MeshDataPool).FullName, nameof(MeshDataPool.FrustumCull));

    // The opaque terrain pass (render order 0.37) runs before entities (0.4) and long before liquids and OIT.
    private static MethodInfo OpaqueTerrain() => AccessTools.Method(typeof(Vintagestory.Client.NoObf.ChunkRenderer), "RenderOpaque", [typeof(float)]) ??
        throw new MissingMethodException(typeof(Vintagestory.Client.NoObf.ChunkRenderer).FullName, "RenderOpaque");

    internal static PerformanceFeature Create() => new("terrain-occlusion-cull", settings => settings.TerrainOcclusionCull, () =>
    {
        if (Target().ReturnType != typeof(bool)) throw new InvalidOperationException("Pool location visibility signature changed.");
        if (AccessTools.Field(typeof(ModelDataPoolLocation), nameof(ModelDataPoolLocation.FrustumCullSphere)) == null)
            throw new MissingFieldException(typeof(ModelDataPoolLocation).FullName, "FrustumCullSphere");
        _ = OpaqueTerrain();
        _ = CullPool();
    }, harmony =>
    {
        harmony.Patch(Target(), postfix: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(Postfix)));
        harmony.Patch(OpaqueTerrain(), postfix: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(AfterOpaqueTerrain)));
        harmony.Patch(CullPool(), prefix: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(BeforeCullPool)),
            finalizer: new HarmonyMethod(typeof(TerrainOcclusionCull), nameof(FinishCullPool)));
    }, WorldLeft: () => reset?.Invoke());

    // Main thread: the depth now holds the opaque terrain only; it becomes the occlusion source a few frames later.
    private static void AfterOpaqueTerrain()
    {
        if (PerformancePatchSet.Current is { } settings &&
            (settings.TerrainOcclusionCull || settings.EntityOcclusionCull || settings.ResidentTerrain || settings.ResidentEntities)) capture?.Invoke();
    }

    // A pool's FrustumCull calls IsVisible for every part in that pass. Depth
    // publication occurs at frame start on this same thread, outside this scope.
    // Restore even on exceptions/nested culls; unscoped mod calls keep live reads.
    private static void BeforeCullPool(EnumFrustumCullMode frustumCullMode, out CullSnapshot? __state)
    {
        __state = cullSnapshot;
        if (frustumCullMode != EnumFrustumCullMode.CullNormal || PerformancePatchSet.Current?.TerrainOcclusionCull != true)
        { cullSnapshot = null; return; }
        var test = Source;
        cullSnapshot = new CullSnapshot(test, test?.Source);
    }

    private static Exception? FinishCullPool(CullSnapshot? __state, Exception? __exception)
    {
        cullSnapshot = __state;
        return __exception;
    }

    // Main thread, once per visible chunk part per main-camera pass.
    private static void Postfix(ModelDataPoolLocation __instance, EnumFrustumCullMode mode, ref bool __result)
    {
        if (ResidentTerrain.ParityActive) return;
        if (!__result || mode != EnumFrustumCullMode.CullNormal) return;
        if (ResidentTerrain.IsResident(__instance)) return;
        if (PerformancePatchSet.Current?.TerrainOcclusionCull != true) return;
        var test = cullSnapshot is { } snapshot ? snapshot.Test : Source;
        var captured = cullSnapshot is { } current ? current.Camera : test?.Source;
        if (test == null || captured == null) return;
        if (camera?.Invoke() is not { } now) return;
        Interlocked.Increment(ref PerformanceCounters.OcclusionTests);
        Sphere sphere = __instance.FrustumCullSphere;
        if (StockOccluded(sphere, captured, now, test))
        {
            __result = false;
            Interlocked.Increment(ref PerformanceCounters.OcclusionCulled);
        }
    }

    internal static bool StockOccluded(in Sphere sphere, OcclusionCamera captured,
        (double X, double Y, double Z) now, IOcclusionTest test)
    {
        // The game's "sphere" is the part's tight geometry box: centre plus 0.866 x the full size per axis
        // (TesselatedChunk.SetBounds), so the box half-size is that extent / sqrt(3). Half a block of padding covers
        // geometry that a block model draws past its block.
        // Movement since the depth was captured widens it further: anything that movement could uncover stays drawn.
        double dx = now.X - captured.CameraX, dy = now.Y - captured.CameraY, dz = now.Z - captured.CameraZ;
        double grow = Math.Sqrt(dx * dx + dy * dy + dz * dz) + BoxPadding;
        double hx = sphere.radius / Sqrt3 + grow, hy = sphere.radiusY / Sqrt3 + grow, hz = sphere.radiusZ / Sqrt3 + grow;
        return test.IsOccluded(sphere.x - hx, sphere.y - hy, sphere.z - hz, sphere.x + hx, sphere.y + hy, sphere.z + hz);
    }
}

/// <summary>PERF-03 for entities: entities hidden behind opaque terrain skip the main camera's render preparation and draws.</summary>
/// <remarks>
/// The occlusion depth holds terrain only (captured before entities draw), so it is valid for entities. The check is inserted
/// after the game's own visibility tests in <c>SystemRenderEntities.OnBeforeRender</c> (frustum, dimension, range, rendered
/// chunk), so only entities the game would render are tested. A hidden entity takes the game's own not-rendered branch
/// (<c>IsRendered = false</c>), exactly as an entity outside the frustum does; its animation keeps running as for any
/// not-rendered entity. Players are never culled, so their name tags keep the original behaviour.
/// </remarks>
internal static class EntityOcclusionCull
{
    // Entities move on their own between capture and use; this much extra margin covers a few frames of fast movement.
    private const double MovementMargin = 1.5;
    private static readonly FieldInfo RendererEntity = AccessTools.Field(typeof(EntityRenderer), nameof(EntityRenderer.entity))!;
    private static readonly MethodInfo InFrustum = AccessTools.Method(typeof(FrustumCulling), nameof(FrustumCulling.SphereInFrustum),
        [typeof(double), typeof(double), typeof(double), typeof(double)])!;
    private static MethodInfo Target() => AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]) ??
        throw new MissingMethodException(typeof(SystemRenderEntities).FullName, "OnBeforeRender");

    private static readonly FieldInfo IsRendered = AccessTools.Field(typeof(Entity), nameof(Entity.IsRendered))!;

    /// <summary>
    /// The entity local store, the not-rendered branch of the first loop's visibility tests, and the start of its rendered
    /// branch (<c>entity.IsRendered = true</c>), which every passing test jumps or falls into.
    /// </summary>
    private static (int Store, Label Hidden, int Rendered) Anchor(IReadOnlyList<CodeInstruction> body)
    {
        int load = -1;
        for (int index = 0; index + 1 < body.Count; index++)
            if (body[index].LoadsField(RendererEntity) && body[index + 1].IsStloc()) { load = index; break; }
        if (load < 0) throw new InvalidOperationException("Entity render local anchor changed.");
        int test = -1;
        for (int index = load; index < body.Count; index++) if (body[index].Calls(InFrustum)) { test = index; break; }
        if (test < 0 || test + 1 >= body.Count || (body[test + 1].opcode != OpCodes.Brfalse && body[test + 1].opcode != OpCodes.Brfalse_S) ||
            body[test + 1].operand is not Label hidden)
            throw new InvalidOperationException("Entity frustum branch anchor changed.");
        int rendered = -1;
        for (int index = test + 2; index + 2 < body.Count; index++)
            if (body[index + 2].StoresField(IsRendered)) { rendered = index; break; }
        if (rendered < 0 || !body[rendered].IsLdloc() || body[rendered].LocalIndex() != body[load + 1].LocalIndex() ||
            body[rendered + 1].opcode != OpCodes.Ldc_I4_1)
            throw new InvalidOperationException("Entity rendered branch anchor changed.");
        return (load + 1, hidden, rendered);
    }

    internal static PerformanceFeature Create() => new("entity-occlusion-cull", settings => settings.EntityOcclusionCull,
        () => Anchor(PatchProcessor.GetOriginalInstructions(Target())),
        harmony => harmony.Patch(Target(), transpiler: new HarmonyMethod(typeof(EntityOcclusionCull), nameof(Transpiler))));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList();
        var (store, hidden, rendered) = Anchor(body);
        // Runs only for entities that passed the game's own tests: the check takes over the rendered branch's labels.
        CodeInstruction load = IlLocals.LoadOf(body[store]).MoveLabelsFrom(body[rendered]);
        body.InsertRange(rendered,
        [
            load,
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EntityOcclusionCull), nameof(Hidden))),
            new CodeInstruction(OpCodes.Brtrue, hidden),
        ]);
        return body;
    }

    // Main thread, once per frame for each entity that passed the game's own visibility tests.
    private static bool Hidden(Entity entity)
    {
        if (PerformancePatchSet.Current?.EntityOcclusionCull != true || entity is EntityPlayer) return false;
        if (TerrainOcclusionCull.Source is not { Source: { } captured } test || TerrainOcclusionCull.CameraNow() is not { } now) return false;
        Interlocked.Increment(ref PerformanceCounters.EntityOcclusionTests);
        // The game's own frustum sphere for this entity, as a box, grown by camera and entity movement since the capture.
        double dx = now.X - captured.CameraX, dy = now.Y - captured.CameraY, dz = now.Z - captured.CameraZ;
        double r = entity.FrustumSphereRadius + Math.Sqrt(dx * dx + dy * dy + dz * dz) + MovementMargin;
        double x = entity.Pos.X, y = entity.Pos.InternalY, z = entity.Pos.Z;
        if (!test.IsOccluded(x - r, y - r, z - r, x + r, y + r, z + r)) return false;
        Interlocked.Increment(ref PerformanceCounters.EntityOcclusionCulled);
        return true;
    }
}
