using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

internal static class DecalConsumerPatches
{
    private const string Owner = "vulkanstory.routing.graphics-decals";
    private static ProcessRuntime? runtime;
    private static readonly MethodInfo Render = AccessTools.Method(typeof(SystemRenderDecals), "OnRenderFrame3D", [typeof(float)])!;
    private static readonly MethodInfo PoolDraw = AccessTools.Method(typeof(MeshDataPool), "Draw",
        [typeof(ICoreClientAPI), typeof(FrustumCulling), typeof(EnumFrustumCullMode)])!;
    private static void Check(IReadOnlyList<CodeInstruction> body)
    {
        if (body.Count(instruction => instruction.Calls(PoolDraw)) != 1)
            throw new InvalidOperationException("Original decal pool draw anchor changed.");
    }
    internal static StartupPatchGroup CreateGroup(ProcessRuntime owner)
    {
        var harmony = new Harmony(Owner); bool attempted = false;
        return new StartupPatchGroup("graphics-scene-decals", () =>
        {
            if (Render == null || PoolDraw == null || Render.ReturnType != typeof(void) ||
                PoolDraw.ReturnType != typeof(void) || Render.GetMethodBody() == null)
                throw new MissingMethodException("Original decal renderer/pool signature changed.");
            Check(PatchProcessor.GetOriginalInstructions(Render));
        }, () =>
        {
            if (runtime != null) throw new InvalidOperationException("Decal routing already has an owner.");
            runtime = owner; attempted = true;
            harmony.Patch(Render, transpiler: new HarmonyMethod(typeof(DecalConsumerPatches), nameof(Transpiler)) { priority = Priority.First });
        }, () =>
        {
            if (!attempted) return;
            harmony.UnpatchAll(Owner);
            if (ReferenceEquals(runtime, owner)) runtime = null;
            attempted = false;
        });
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var body = instructions.ToList(); Check(body);
        foreach (var instruction in body)
            if (instruction.Calls(PoolDraw))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(DecalConsumerPatches), nameof(DrawPool));
            }
        return body;
    }
    private static void DrawPool(MeshDataPool pool, ICoreClientAPI api, FrustumCulling culler, EnumFrustumCullMode mode)
    {
        if (runtime?.Routing.RoutingEnabled != true) { pool.Draw(api, culler, mode); return; }
        if (ScreenManager.Platform is not ClientPlatformWindows platform || !runtime.TrySession(platform, out var session))
            throw new InvalidOperationException("Active decal routing lost its session.");
        bool motion = session.Graphics.BeginCameraMotionWrite();
        try
        {
            session.Graphics.BeginDecalPass();
            try { pool.Draw(api, culler, mode); }
            finally { session.Graphics.EndDecalPass(); }
        }
        finally { if (motion) session.Graphics.EndMotionWrite(); }
    }
}
