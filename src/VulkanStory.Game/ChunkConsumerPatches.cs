using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

/// <summary>Brackets original chunk pool draws with native terrain state and session motion inputs.</summary>
/// <remarks>Patch discovery and installation belong to startup. Callbacks use the committed routing predicate; game object identity remains in the integration assembly.</remarks>
internal static class ChunkConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-chunks";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Pool = AccessTools.Method(typeof(MeshDataPoolManager), "Render",
        [typeof(Vec3d), typeof(string), typeof(EnumFrustumCullMode)])!;
    private static readonly MethodInfo Sampler = typeof(GL).GetMethod(nameof(GL.BindSampler), [typeof(int), typeof(int)])!;
    private static readonly (string Name, int Pools, int Samplers)[] Profile =
        [("OnRenderBefore", 1, 0), ("RenderShadow", 4, 0), ("RenderOpaque", 5, 9), ("RenderOIT", 3, 0), ("RenderAfterOIT", 1, 0)];
    private static MethodInfo Target(string name) => AccessTools.Method(typeof(ChunkRenderer), name, [typeof(float)]) ??
        throw new MissingMethodException(typeof(ChunkRenderer).FullName, name);
    private static void Check(MethodBase method, IReadOnlyList<CodeInstruction> body)
    {
        var route = Profile.Single(entry => entry.Name == method.Name);
        if (body.Count(instruction => instruction.Calls(Pool)) != route.Pools ||
            body.Count(instruction => instruction.Calls(Sampler)) != route.Samplers)
            throw new InvalidOperationException("Original terrain pool/sampler anchors changed: " + method.Name);
    }
    /// <summary>Creates the dormant patch group for this consumer path; validation and installation remain separate transaction steps.</summary>
    /// <param name="owner">Process runtime that owns this group and its session.</param>
    /// <returns>Validation, installation and removal callbacks for the startup transaction.</returns>
    /// <remarks>Binding or IL-anchor mismatches reject the group. Creating the group does not enable graphics routing.</remarks>
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-chunks", () =>
        {
            if (Pool == null || Sampler == null) throw new MissingMethodException("Original terrain pool/sampler overload is missing.");
            RayTerrainCapture.Validate();
            foreach (var route in Profile)
            {
                MethodInfo method = Target(route.Name);
                if (method.ReturnType != typeof(void) || method.GetMethodBody() == null) throw new MissingMethodException("Original chunk renderer changed.");
                Check(method, PatchProcessor.GetOriginalInstructions(method));
            }
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Chunk routing already has an owner.");
            runtime = owner; attempted = true;
            RayTerrainCapture.Install(harmony, owner);
            foreach (var route in Profile) harmony.Patch(Target(route.Name),
                transpiler: new HarmonyMethod(typeof(ChunkConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            RayTerrainCapture.Detach(owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static readonly FieldInfo PassPools = AccessTools.Field(typeof(ChunkRenderer), nameof(ChunkRenderer.poolsByRenderPass))!;
    /// <summary>Index of the single RenderShadow pool call that draws the BlendNoCull (foliage) pass, or -1 when the anchor is absent.</summary>
    /// <remarks>The pass index is the constant loaded right after the nearest preceding <c>poolsByRenderPass</c> load.</remarks>
    internal static int FoliageShadowSite(IReadOnlyList<CodeInstruction> body)
    {
        int found = -1;
        for (int index = 0; index < body.Count; index++)
        {
            if (!body[index].Calls(Pool)) continue;
            int load = index - 1;
            while (load >= 0 && !body[load].LoadsField(PassPools)) load--;
            if (load < 0 || load + 1 >= index || !body[load + 1].LoadsConstant((int)EnumChunkRenderPass.BlendNoCull)) continue;
            if (found >= 0) return -1;
            found = index;
        }
        return found;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        var body = instructions.ToList(); Check(__originalMethod, body);
        int foliage = __originalMethod.Name == "RenderShadow" ? FoliageShadowSite(body) : -1;
        for (int index = 0; index < body.Count; index++)
        {
            var instruction = body[index];
            if (instruction.Calls(Pool))
            {
                var label = new CodeInstruction(OpCodes.Ldstr, "chunk-" + __originalMethod.Name);
                label.labels.AddRange(instruction.labels); instruction.labels.Clear();
                label.blocks.AddRange(instruction.blocks.Where(block => block.blockType != ExceptionBlockType.EndExceptionBlock));
                instruction.blocks.RemoveAll(block => block.blockType != ExceptionBlockType.EndExceptionBlock);
                yield return label;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(ChunkConsumerPatches), index == foliage ? nameof(RenderShadowFoliagePool) : nameof(RenderPool));
            }
            else if (instruction.Calls(Sampler))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(ChunkConsumerPatches), nameof(ClearSampler)); }
            yield return instruction;
        }
    }
    private static GameGraphicsAdapter? Adapter()
    {
        if (runtime?.Routing.RoutingEnabled != true) return null;
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active terrain routing lost its session.");
        return session.Graphics;
    }
    private static void RenderPool(MeshDataPoolManager pool, Vec3d position, string uniform, EnumFrustumCullMode mode, string name)
    {
        if (Adapter() is not { } graphics) { pool.Render(position, uniform, mode); return; }
        bool motion = graphics.BeginCameraMotionWrite();
        try
        {
            graphics.BeginChunkPool(name);
            try { pool.Render(position, uniform, mode); }
            finally { graphics.EndChunkPool(); }
        }
        finally { if (motion) graphics.EndMotionWrite(); }
    }
    /// <summary>Set by the performance feature once it validated; until then the foliage site renders like every other pool.</summary>
    internal static volatile bool FoliageSkipArmed;
    private static void RenderShadowFoliagePool(MeshDataPoolManager pool, Vec3d position, string uniform, EnumFrustumCullMode mode, string name)
    {
        if (FoliageSkipArmed && mode == EnumFrustumCullMode.CullInstantShadowPassFar &&
            Performance.PerformancePatchSet.Current?.ShadowFarFoliage == true)
        {
            Interlocked.Increment(ref Performance.PerformanceCounters.ShadowFoliagePoolsSkipped);
            return;
        }
        RenderPool(pool, position, uniform, mode, name);
    }
    private static void ClearSampler(int unit, int sampler)
    { if (Adapter() is { } graphics) graphics.BindSampler(unit, sampler); else GL.BindSampler(unit, sampler); }
}
